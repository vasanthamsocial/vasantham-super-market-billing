using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Sales;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Purchases;

internal sealed record GrnApprovalPayload(Guid GrnId, Guid StoreId);

/// <summary>
/// Goods receipts. The same code previews and saves, so what the receiver sees is what is saved. A receipt whose cost
/// changed beyond the approval threshold, or that sells below cost as a loss-leader, waits for a manager; otherwise
/// (or when nobody else could approve) its goods go into stock at once.
/// </summary>
public sealed class GrnService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    SupplierService suppliers,
    IAccessControl access,
    DocumentNumbers numbers,
    GrnPoster poster,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public const string ApprovalType = "grn.post";
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    public async Task<GrnDto> PreviewAsync(Guid businessId, GrnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.PurchasesManage, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        var built = await BuildAsync(businessId, request, cancellationToken).ConfigureAwait(false);
        return ToDto(built);
    }

    public async Task<GrnDto> CreateAsync(Guid businessId, GrnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.PurchasesManage, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        var key = request.IdempotencyKey ?? throw AppException.Validation("idempotency.key_required", "Each goods receipt needs an idempotency key.");
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HashJson))));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"grn|" + businessId + "|" + key}))", cancellationToken).ConfigureAwait(false);
        var existing = await db.Grns.AsNoTracking().Where(g => g.BusinessId == businessId && g.IdempotencyKey == key)
            .Select(g => new { g.Id, g.RequestHash }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? await GetCoreAsync(existing.Id, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("idempotency.mismatch", "This goods receipt was already saved with different contents.");
        }

        // One receipt per supplier invoice: concurrent entries of the same invoice queue here.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({"grn-invoice|" + request.SupplierId + "|" + (request.SupplierInvoiceNumber ?? string.Empty).Trim().ToUpperInvariant()}))",
            cancellationToken).ConfigureAwait(false);
        var built = await BuildAsync(businessId, request, cancellationToken).ConfigureAwait(false);
        if (built.Issues.Count > 0)
        {
            var first = built.Issues[0];
            throw AppException.Validation(first.Code, first.LineNumber is { } n ? $"Line {n}: {first.Message}" : first.Message);
        }

        var now = clock.GetUtcNow();
        var sequence = await numbers.NextAsync(businessId, built.Store.Id, "GRN", cancellationToken).ConfigureAwait(false);
        var grnId = Guid.CreateVersion7(now);
        Grn grn;
        try
        {
            grn = Grn.Receive(grnId, businessId, DocumentNumbers.Format(built.Store.Code, "GRN", sequence), sequence,
                new Grn.Header(built.Store.Id, built.Supplier.Id, request.SupplierInvoiceNumber ?? string.Empty, request.SupplierInvoiceDate, request.Classification,
                    request.PurchaseOrderReference, built.InterState, built.TaxRecoverable, built.BusinessDate, request.Notes),
                built.Result, built.RoundOff, currentUser.UserId, key, requestHash, now);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.Grns.Add(grn);
        var lines = built.Lines.Select((l, i) => GrnLine.Create(businessId, grnId, i + 1, l.Item, built.Result.Lines[i], l.Change?.Change,
            l.Request.CostChangeReason, l.Request.LossLeaderReason, now)).ToList();
        db.GrnLines.AddRange(lines);
        for (var e = 0; e < built.Expenses.Count; e++)
        {
            var expense = GrnExpense.Create(businessId, grnId, e + 1, built.Expenses[e], (request.Expenses ?? [])[e].Note, now);
            db.GrnExpenses.Add(expense);
            db.GrnAllocations.AddRange(lines.Select((l, i) => GrnAllocation.Create(businessId, expense.Id, l.Id, built.Result.Allocations[e][i], now)));
        }

        string approval;
        if (!built.NeedsApproval)
        {
            approval = "not_required";
            await poster.PostAsync(grn, lines, now, cancellationToken).ConfigureAwait(false);
        }
        else if (!await AnyOtherApproverAsync(businessId, built.Store.Id, cancellationToken).ConfigureAwait(false))
        {
            approval = "waived_no_other_approver";
            await poster.PostAsync(grn, lines, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            approval = "pending";
            var request2 = ApprovalRequest.Create(businessId, ApprovalType, $"Goods receipt {grn.Number} from {built.Supplier.Name}: {Reasons(built)}",
                JsonSerializer.Serialize(new GrnApprovalPayload(grnId, built.Store.Id), UserAdminService.Json), Reasons(built), currentUser.UserId, now,
                TimeSpan.FromDays(options.Value.ApprovalLifetimeDays));
            db.ApprovalRequests.Add(request2);
            grn.AwaitApproval(request2.Id);
            audit.Record("approval.requested", "approval_request", request2.Id, businessId, built.Store.Id, details: new { request2.Type, request2.Summary });
        }

        audit.Record("grn.received", "grn", grnId, businessId, built.Store.Id, details: new
        {
            grn.Number,
            supplier = built.Supplier.Code,
            grn.SupplierInvoiceNumber,
            grn.Classification,
            grn.InvoiceTotal,
            grn.ExpensesTotal,
            grn.LandedTotal,
            approval,
            costChanges = built.Lines.Where(l => l.Change is not null).Select(l => new { l.Item.Description, l.Change!.Change.PercentChange, l.Request.CostChangeReason }),
            lossLeaders = built.Lines.Where(l => l.BelowCost is not null).Select(l => new { l.Item.Description, l.BelowCost!.LossPerPack, l.Request.LossLeaderReason }),
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(grnId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GrnSummaryDto>> ListAsync(Guid businessId, Guid storeId, string? status, CancellationToken cancellationToken)
    {
        await RequireViewAsync(businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.Grns.AsNoTracking().Where(g => g.StoreId == storeId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(g => g.Status == status);
        }

        return await (
                from g in query
                join s in db.Suppliers.AsNoTracking() on g.SupplierId equals s.Id
                orderby g.ReceivedAtUtc descending
                select new GrnSummaryDto(g.Id, g.Number, g.Status, s.Name, g.SupplierInvoiceNumber, g.SupplierInvoiceDate, g.Classification, g.BusinessDate,
                    g.InvoiceTotal, g.LandedTotal))
            .Take(300).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GrnDto> GetAsync(Guid businessId, Guid grnId, CancellationToken cancellationToken)
    {
        var storeId = await db.Grns.AsNoTracking().Where(g => g.Id == grnId && g.BusinessId == businessId).Select(g => (Guid?)g.StoreId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Goods receipt");
        await RequireViewAsync(businessId, storeId, cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(grnId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireViewAsync(Guid businessId, Guid storeId, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false))
        {
            await organisation.RequireAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsEligibleApprover(IReadOnlyCollection<ActiveGrant> grants, Guid businessId, Guid storeId) =>
        AccessControl.Covers(grants, Permissions.ApprovalsDecide, businessId, null) && AccessControl.Covers(grants, Permissions.PurchasesApprove, businessId, storeId);

    private async Task<bool> AnyOtherApproverAsync(Guid businessId, Guid storeId, CancellationToken cancellationToken) =>
        (await ApprovalService.OtherUsersGrantsAsync(db, businessId, [currentUser.UserId], cancellationToken).ConfigureAwait(false))
            .Any(g => IsEligibleApprover(g, businessId, storeId));

    private static string Reasons(Built built)
    {
        var parts = built.Lines.Where(l => l.Change is { Change.NeedsApproval: true })
            .Select(l => $"{l.Item.Description} cost {(l.Change!.Change.PercentChange > 0 ? "+" : string.Empty)}{l.Change.Change.PercentChange:0.##}%")
            .Concat(built.Lines.Where(l => l.BelowCost is not null).Select(l => $"{l.Item.Description} sold below cost (loss Rs. {l.BelowCost!.LossPerPack:0.00} a pack)"));
        var text = string.Join("; ", parts);
        return text.Length <= 400 ? text : text[..397] + "...";
    }

    private async Task<Built> BuildAsync(Guid businessId, GrnRequest request, CancellationToken cancellationToken)
    {
        var issues = new List<GrnIssueDto>();
        if (!PurchaseClassifications.All.Contains(request.Classification))
        {
            throw AppException.Validation("grn.classification_invalid", $"Unknown purchase classification '{request.Classification}'.");
        }

        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500)
        {
            throw AppException.Validation("grn.lines_required", "A goods receipt needs 1 to 500 items.");
        }

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StoreId && s.BusinessId == businessId && s.IsActive, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Store");
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.SupplierId && s.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Supplier");
        if (!supplier.IsActive)
        {
            throw AppException.Conflict("supplier.inactive", $"{supplier.Name} is switched off.");
        }

        switch (request.Classification)
        {
            case PurchaseClassifications.GstTaxInvoice or PurchaseClassifications.BillOfSupply when !supplier.IsGstRegistered:
                issues.Add(new("grn.supplier_not_registered", $"{supplier.Name} has no GSTIN, so this cannot be a GST tax invoice or bill of supply.", null));
                break;
            case PurchaseClassifications.Unregistered when supplier.IsGstRegistered:
                issues.Add(new("grn.supplier_registered", $"{supplier.Name} is GST-registered: classify the document by what it is.", null));
                break;
        }

        var businessDate = BusinessCalendar.Today(clock, store.TimeZone);
        var (registration, _) = await BillingService.RegistrationInForceAsync(db, businessId, businessDate, cancellationToken).ConfigureAwait(false);
        var settings = await suppliers.LoadSettingsAsync(businessId, cancellationToken).ConfigureAwait(false);
        var interState = request.Classification == PurchaseClassifications.Import || supplier.StateCode != store.StateCode;
        var taxRecoverable = PurchaseClassifications.TaxRecoverable(request.Classification, registration.Mode);
        var chargesGst = PurchaseClassifications.ChargesGst(request.Classification);

        var invoiceNumber = (request.SupplierInvoiceNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (await db.Grns.AnyAsync(g => g.BusinessId == businessId && g.SupplierId == supplier.Id && g.SupplierInvoiceNumber == invoiceNumber && g.Status != GrnStatus.Rejected,
                cancellationToken).ConfigureAwait(false))
        {
            issues.Add(new("grn.invoice_already_received", $"Invoice {invoiceNumber} from {supplier.Name} has already been received.", null));
        }

        var packIds = request.Lines.Select(l => l.VariantUnitId).Distinct().ToList();
        var packs = await (
                from vu in db.VariantUnits.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on vu.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                join u in db.Units.AsNoTracking() on vu.UnitId equals u.Id
                where packIds.Contains(vu.Id) && vu.BusinessId == businessId
                select new { Pack = vu, Variant = v, Product = p, UnitCode = u.Code })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var rules = await db.PriceRules.AsNoTracking().Where(r => packIds.Contains(r.VariantUnitId) && r.Status == PriceRuleStatus.Active)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var lineInputs = new List<GrnLineInput>();
        var lines = new List<BuiltLine>();
        foreach (var (request1, number) in request.Lines.Select((l, i) => (l, i + 1)))
        {
            var row = packs.FirstOrDefault(p => p.Pack.Id == request1.VariantUnitId) ?? throw AppException.NotFound($"Item pack on line {number}");
            var product = row.Product;
            var gross = InvoiceCalculator.Money(request1.Quantity * request1.Rate);
            if (request1.DiscountAmount is not null && request1.DiscountPercent is not null)
            {
                throw AppException.Validation("discount.ambiguous", $"Line {number}: give the discount as an amount or a percentage, not both.");
            }

            var discount = request1.DiscountPercent is { } pct
                ? pct is >= 0 and <= 100 ? InvoiceCalculator.Money(gross * pct / 100) : throw AppException.Validation("discount.percent_invalid", $"Line {number}: a discount percentage is 0-100.")
                : request1.DiscountAmount ?? 0;

            var batchNumber = string.IsNullOrWhiteSpace(request1.BatchNumber) ? null : request1.BatchNumber.Trim().ToUpperInvariant();
            if (product.TracksBatches && batchNumber is null)
            {
                issues.Add(new("batch.required", $"{row.Variant.Name} is tracked by batch: enter the batch number.", number));
            }

            if (product.TracksExpiry && request1.ExpiresOn is null)
            {
                issues.Add(new("batch.expiry_required", $"{row.Variant.Name} is tracked by expiry: enter the expiry date.", number));
            }

            var item = new GrnLine.Item(product.Id, row.Variant.Id, row.Pack.Id, row.Variant.Name, row.UnitCode, row.Pack.FactorToBase, request1.Quantity,
                request1.FreeQuantity, request1.Mrp, request1.Rate, discount, chargesGst ? product.GstRatePercent : 0, chargesGst ? product.CessRatePercent : 0,
                batchNumber, request1.ManufacturedOn, request1.ExpiresOn, request1.SellingPrice, request1.Weight, request1.Volume);
            lineInputs.Add(new GrnLineInput(item.Quantity, item.FreeQuantity, item.FactorToBase, item.Rate, discount, item.GstRatePercent, item.CessRatePercent,
                item.Weight, item.Volume));
            lines.Add(new BuiltLine(number, request1, item, product, rules.Where(r => r.VariantUnitId == row.Pack.Id).ToList()));
        }

        var expenses = (request.Expenses ?? []).Select(e => new GrnExpenseInput(e.Kind, e.Amount, e.Method, e.ManualAmounts)).ToList();
        GrnResult result;
        try
        {
            result = GrnCalculator.Calculate(new GrnInput(lineInputs, expenses, interState, chargesGst, taxRecoverable));
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        // The supplier's printed total may differ from ours by round-off only.
        var roundOff = 0m;
        if (request.SupplierInvoiceTotal is { } printed)
        {
            roundOff = printed - result.InvoiceTotal;
            if (Math.Abs(roundOff) > 1)
            {
                issues.Add(new("grn.invoice_total_mismatch",
                    $"The supplier's total (Rs. {printed:0.00}) differs from the lines and taxes (Rs. {result.InvoiceTotal:0.00}) by Rs. {roundOff:0.00}. Check the rates, quantities and taxes.", null));
                roundOff = 0;
            }
        }

        var sellerCollectsTax = registration.Mode == TaxRegistrationModes.GstRegular;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var amounts = result.Lines[i];

            // Cost change against the last posted receipt of the same item and MRP.
            var previous = await (
                    from l in db.GrnLines.AsNoTracking()
                    join g in db.Grns.AsNoTracking() on l.GrnId equals g.Id
                    join s in db.Suppliers.AsNoTracking() on g.SupplierId equals s.Id
                    where l.VariantId == line.Item.VariantId && l.Mrp == line.Item.Mrp && g.Status == GrnStatus.Posted
                    orderby g.PostedAtUtc descending
                    select new { l.LandedUnitCost, Supplier = s.Name, g.Number, g.BusinessDate })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var change = PurchaseRules.CompareCost(previous?.LandedUnitCost, amounts.LandedUnitCost, settings.CostReasonThresholdPercent, settings.CostApprovalThresholdPercent);
            if (change is not null)
            {
                line.Change = new LineChange(change, previous!.Supplier, previous.Number, previous.BusinessDate);
                if (change.NeedsReason && string.IsNullOrWhiteSpace(line.Request.CostChangeReason))
                {
                    issues.Add(new("grn.cost_change_reason_required",
                        $"{line.Item.Description}: the cost moved {change.PercentChange:+0.##;-0.##}% (Rs. {change.PreviousUnitCost:0.####} to {change.NewUnitCost:0.####} a unit). Give a reason.", line.Number));
                }
            }

            // Selling price (given, or the current retail price) against the landed cost.
            var sellingPrice = line.Request.SellingPrice;
            if (sellingPrice is null)
            {
                var quote = PriceResolver.Resolve(line.Rules, new PriceQuery(line.Item.VariantUnitId, 1, SalesChannels.Retail, store.Id, null, false, line.Item.Mrp,
                    line.Product.GstRatePercent + line.Product.CessRatePercent, clock.GetUtcNow()));
                sellingPrice = quote.UnitPriceInclusive;
            }

            if (sellingPrice is { } price)
            {
                var below = PurchaseRules.CheckSellingPrice(amounts.LandedUnitCost, line.Item.FactorToBase, price,
                    line.Product.GstRatePercent + line.Product.CessRatePercent, sellerCollectsTax && line.Product.SupplyType == SupplyTypes.Taxable);
                if (below is not null)
                {
                    line.BelowCost = new BelowCostDto(price, below.CostPerPack, below.SellingNetPerPack, below.LossPerPack, below.MarginPercent);
                    var message = $"{line.Item.Description}: selling at Rs. {price:0.00} is below the landed cost of Rs. {below.CostPerPack:0.00} a pack " +
                                  $"(loss Rs. {below.LossPerPack:0.00}, margin {below.MarginPercent:0.##}%).";
                    if (!settings.AllowLossLeader)
                    {
                        issues.Add(new("grn.below_cost", message + " Correct the cost or the selling price.", line.Number));
                    }
                    else if (string.IsNullOrWhiteSpace(line.Request.LossLeaderReason))
                    {
                        issues.Add(new("grn.loss_leader_reason_required", message + " Give the loss-leader reason; a manager must approve.", line.Number));
                    }
                }
            }
        }

        var needsApproval = lines.Any(l => l.Change?.Change.NeedsApproval == true) || lines.Any(l => l.BelowCost is not null);
        return new Built(store, supplier, registration.Mode, businessDate, interState, taxRecoverable, lines, expenses, result, roundOff, needsApproval, issues, request);
    }

    private static GrnDto ToDto(Built b) => new(
        Guid.Empty, string.Empty, "PREVIEW", b.Store.Id, b.Supplier.Id, b.Supplier.Name, b.Supplier.Gstin, (b.Request.SupplierInvoiceNumber ?? string.Empty).Trim().ToUpperInvariant(),
        b.Request.SupplierInvoiceDate, b.Request.Classification, b.Request.PurchaseOrderReference, b.InterState, b.TaxRecoverable, b.BusinessDate, b.Request.Notes,
        b.Lines.Select((l, i) => LineDto(l.Number, l.Item, b.Result.Lines[i], l.Change, l.BelowCost, l.Request.CostChangeReason, l.Request.LossLeaderReason)).ToList(),
        b.Expenses.Select((e, i) => new GrnExpenseDto(e.Kind, e.Amount, e.Method, (b.Request.Expenses ?? [])[i].Note, b.Result.Allocations[i])).ToList(),
        b.Result.Gross, b.Result.Discount, b.Result.Taxable, b.Result.Cgst, b.Result.Sgst, b.Result.Igst, b.Result.Cess, b.RoundOff, b.Result.InvoiceTotal + b.RoundOff,
        b.Result.Expenses, b.Result.LandedTotal, b.NeedsApproval, null, null, null, null, b.Issues);

    private static GrnLineDto LineDto(int number, GrnLine.Item item, GrnLineResult a, LineChange? change, BelowCostDto? below, string? reason, string? lossReason) => new(
        number, item.VariantId, item.VariantUnitId, item.Description, item.UnitCode, item.FactorToBase, item.Quantity, item.FreeQuantity, a.BaseQuantity, item.Mrp,
        item.Rate, a.Discount, item.GstRatePercent, item.CessRatePercent, a.Taxable, a.Cgst, a.Sgst, a.Igst, a.Cess, a.Total, a.ExpenseShare, a.NonRecoverableTax,
        a.LandedTotal, a.LandedUnitCost, item.BatchNumber, item.ExpiresOn, item.SellingPrice,
        change is null ? null : new CostChangeDto(change.Change.PreviousUnitCost, change.Change.NewUnitCost, change.Change.Difference, change.Change.PercentChange,
            change.Supplier, change.GrnNumber, change.Date, change.Change.NeedsReason, change.Change.NeedsApproval),
        below, reason, lossReason);

    internal async Task<GrnDto> GetCoreAsync(Guid grnId, CancellationToken cancellationToken)
    {
        var row = await (
                from x in db.Grns.AsNoTracking()
                join s in db.Suppliers.AsNoTracking() on x.SupplierId equals s.Id
                join u in db.Users.AsNoTracking() on x.ReceivedByUserId equals u.Id
                where x.Id == grnId
                select new { Grn = x, Supplier = s, ReceivedBy = u.DisplayName })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var g = row.Grn;
        var lines = await db.GrnLines.AsNoTracking().Where(l => l.GrnId == grnId).OrderBy(l => l.LineNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        var expenses = await db.GrnExpenses.AsNoTracking().Where(e => e.GrnId == grnId).OrderBy(e => e.ExpenseOrder).ToListAsync(cancellationToken).ConfigureAwait(false);
        var expenseIds = expenses.Select(e => e.Id).ToList();
        var allocations = await db.GrnAllocations.AsNoTracking().Where(a => expenseIds.Contains(a.ExpenseId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new GrnDto(
            g.Id, g.Number, g.Status, g.StoreId, g.SupplierId, row.Supplier.Name, row.Supplier.Gstin, g.SupplierInvoiceNumber, g.SupplierInvoiceDate, g.Classification,
            g.PurchaseOrderReference, g.IsInterState, g.TaxRecoverable, g.BusinessDate, g.Notes,
            lines.Select(l => new GrnLineDto(l.LineNumber, l.VariantId, l.VariantUnitId, l.Description, l.UnitCode, l.FactorToBase, l.Quantity, l.FreeQuantity,
                l.BaseQuantity, l.Mrp, l.Rate, l.Discount, l.GstRatePercent, l.CessRatePercent, l.Taxable, l.Cgst, l.Sgst, l.Igst, l.Cess, l.Total, l.ExpenseShare,
                l.NonRecoverableTax, l.LandedTotal, l.LandedUnitCost, l.BatchNumber, l.ExpiresOn, l.SellingPrice,
                l.PreviousUnitCost is { } prev ? new CostChangeDto(prev, l.LandedUnitCost, l.LandedUnitCost - prev, l.CostChangePercent ?? 0, null, null, null, false, false) : null,
                null, l.CostChangeReason, l.LossLeaderReason)).ToList(),
            expenses.Select(e => new GrnExpenseDto(e.Kind, e.Amount, e.Method, e.Note,
                lines.Select(l => allocations.FirstOrDefault(a => a.ExpenseId == e.Id && a.LineId == l.Id)?.Amount ?? 0).ToList())).ToList(),
            g.GrossTotal, g.DiscountTotal, g.TaxableTotal, g.CgstTotal, g.SgstTotal, g.IgstTotal, g.CessTotal, g.RoundOff, g.InvoiceTotal, g.ExpensesTotal,
            g.LandedTotal, g.Status == GrnStatus.PendingApproval, g.ApprovalRequestId, row.ReceivedBy, g.ReceivedAtUtc, g.PostedAtUtc, []);
    }

    private sealed record LineChange(CostChange Change, string Supplier, string GrnNumber, DateOnly Date);

    private sealed class BuiltLine(int number, GrnLineRequest request, GrnLine.Item item, Product product, List<PriceRule> rules)
    {
        public int Number { get; } = number;

        public GrnLineRequest Request { get; } = request;

        public GrnLine.Item Item { get; } = item;

        public Product Product { get; } = product;

        public List<PriceRule> Rules { get; } = rules;

        public LineChange? Change { get; set; }

        public BelowCostDto? BelowCost { get; set; }
    }

    private sealed record Built(
        Store Store, Supplier Supplier, string TaxMode, DateOnly BusinessDate, bool InterState, bool TaxRecoverable, List<BuiltLine> Lines,
        List<GrnExpenseInput> Expenses, GrnResult Result, decimal RoundOff, bool NeedsApproval, List<GrnIssueDto> Issues, GrnRequest Request);
}

/// <summary>Approving a goods receipt: its goods go into stock in the approval's transaction. Needs purchase approval for the store.</summary>
internal sealed class GrnApprovalHandler(SupermarketBillingDbContext db, GrnPoster poster) : IApprovalHandler
{
    public string Type => GrnService.ApprovalType;

    public bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, Guid actorUserId, ApprovalRequest request) =>
        GrnService.IsEligibleApprover(actorGrants, request.BusinessId, Payload(request).StoreId);

    public async Task ApplyAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var grnId = Payload(request).GrnId;
        var grn = await db.Grns.FirstAsync(g => g.Id == grnId, cancellationToken).ConfigureAwait(false);
        var lines = await db.GrnLines.AsNoTracking().Where(l => l.GrnId == grnId).ToListAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await poster.PostAsync(grn, lines, now, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException e)
        {
            throw AppException.Conflict(e.Code, e.Message);
        }
    }

    public async Task ClosedWithoutApprovalAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var grnId = Payload(request).GrnId;
        var grn = await db.Grns.FirstAsync(g => g.Id == grnId, cancellationToken).ConfigureAwait(false);
        grn.Reject();
    }

    private static GrnApprovalPayload Payload(ApprovalRequest request) =>
        JsonSerializer.Deserialize<GrnApprovalPayload>(request.PayloadJson, UserAdminService.Json)
        ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");
}
