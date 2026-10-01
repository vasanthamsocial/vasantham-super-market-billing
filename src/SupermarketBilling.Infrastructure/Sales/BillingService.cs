using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Sales;

/// <summary>
/// Counter billing. The server prices every line from the price rules, decides the document type and taxes from the
/// registration in force, and issues the invoice in one transaction that takes the counter's next number, takes the
/// stock out (with its cost), uses any supervisor approvals, and records the payments. The client's figures are only
/// a preview: a bill whose total differs from what the cashier collected is refused.
/// </summary>
public sealed class BillingService(
    SupermarketBillingDbContext db,
    CounterService counters,
    AuthService auth,
    IAccessControl access,
    DocumentNumbers numbers,
    StockEngine stock,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string LedgerDocumentType = "SALES_INVOICE";
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(10);

    public async Task<PosContextDto> ContextAsync(string? deviceToken, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var (registration, index) = await RegistrationAsync(pos, cancellationToken).ConfigureAwait(false);
        var prefix = pos.Counter.InvoicePrefix(index);
        var next = await db.DocumentSequences.AsNoTracking()
            .Where(s => s.StoreId == pos.Store.Id && s.Series == Counter.InvoiceSeries(prefix)).Select(s => (long?)s.NextNumber)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 1;
        return new PosContextDto(
            pos.Business.Id, pos.Business.TradeName, pos.Store.Id, pos.Store.Name, pos.Store.StateCode, pos.Counter.Id, pos.Counter.Code, pos.Counter.Name,
            pos.Device.Id, pos.Device.Name, registration.Mode, Counter.InvoiceNumber(prefix, next),
            await CanAsync(pos, Permissions.PosPriceOverride, cancellationToken).ConfigureAwait(false),
            await CanAsync(pos, Permissions.PosDiscount, cancellationToken).ConfigureAwait(false),
            await CanAsync(pos, Permissions.StockNegativeOverride, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A supervisor approves a price or discount at this counter by entering their own credentials.</summary>
    public async Task<SupervisorApprovalResponse> ApproveAsync(string? deviceToken, SupervisorApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        User supervisor;
        try
        {
            supervisor = await auth.VerifyCredentialsAsync(request.Username, request.Password, request.MfaCode, cancellationToken).ConfigureAwait(false);
        }
        catch (AppException e) when (e.Kind == ErrorKind.Unauthorized)
        {
            // Not 401: the cashier's own session is fine, only the approver's credentials were wrong.
            throw AppException.Validation("approval.invalid_credentials", "The approver's username, password or code is not correct.");
        }

        var permission = request.Kind == SupervisorApprovalKinds.PriceOverride ? Permissions.PosPriceOverride : Permissions.PosDiscount;
        var grants = await AccessControl.LoadGrantsAsync(db, supervisor.Id, cancellationToken).ConfigureAwait(false);
        if (!AccessControl.Covers(grants, permission, pos.Counter.BusinessId, pos.Counter.StoreId))
        {
            audit.Record("pos.approval_refused", "user", supervisor.Id, pos.Counter.BusinessId, pos.Counter.StoreId, details: new { request.Kind, reason = "no_permission" });
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            throw AppException.Forbidden($"{supervisor.DisplayName} is not allowed to approve this in {pos.Store.Name}.");
        }

        if (request.Kind == SupervisorApprovalKinds.PriceOverride && request.VariantUnitId is { } pack
            && !await db.VariantUnits.AnyAsync(v => v.Id == pack && v.BusinessId == pos.Counter.BusinessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Item pack");
        }

        var token = SecretTokens.NewToken();
        var approval = SupervisorApproval.Grant(pos.Counter.BusinessId, pos.Counter.Id, request.Kind, request.VariantUnitId, request.Price, request.MaxAmount,
            request.Reason, supervisor.Id, currentUser.UserId, SecretTokens.Hash(token), clock.GetUtcNow(), ApprovalLifetime);
        db.SupervisorApprovals.Add(approval);
        audit.Record("pos.approval_granted", "supervisor_approval", approval.Id, pos.Counter.BusinessId, pos.Counter.StoreId, details: new
        {
            counter = pos.Counter.Code,
            approval.Kind,
            approval.VariantUnitId,
            approval.ApprovedPrice,
            approval.MaxAmount,
            approval.Reason,
            approvedBy = supervisor.Username,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new SupervisorApprovalResponse(approval.Id, token, supervisor.DisplayName, approval.ExpiresAtUtc);
    }

    /// <summary>Prices a cart without issuing anything: what the POS shows while the cashier scans.</summary>
    public async Task<CartDto> PriceAsync(string? deviceToken, CartRequest cart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cart);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var bill = await BuildAsync(pos, cart, approvals: null, discountApproval: null, cancellationToken).ConfigureAwait(false);
        return ToCartDto(bill);
    }

    public async Task<InvoiceDto> IssueAsync(string? deviceToken, IssueInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Cart);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var businessId = pos.Counter.BusinessId;
        if (request.NegativeStockOverride && !await CanAsync(pos, Permissions.StockNegativeOverride, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden("Only a manager can confirm selling stock below zero.");
        }

        var requestHash = Hash(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A retry (lost response, double click) waits here, then finds the first attempt's invoice.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"invoice|" + businessId + "|" + request.IdempotencyKey}))", cancellationToken)
            .ConfigureAwait(false);
        var existing = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.BusinessId == businessId && i.IdempotencyKey == request.IdempotencyKey).Select(i => new { i.Id, i.RequestHash })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? await InvoiceAsync(existing.Id, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("idempotency.mismatch", "This bill was already issued with different contents. Start a new bill.");
        }

        var approvals = await LockApprovalsAsync(request.Cart.Lines.Select(l => l.OverrideApprovalToken).Append(request.DiscountApprovalToken), cancellationToken)
            .ConfigureAwait(false);
        var discountApproval = request.DiscountApprovalToken is { } dt ? approvals.GetValueOrDefault(dt) : null;
        var bill = await BuildAsync(pos, request.Cart, approvals, discountApproval, cancellationToken).ConfigureAwait(false);
        if (bill.Lines.Any(l => l.NeedsPriceApproval))
        {
            throw AppException.Forbidden("A price on this bill needs a supervisor's approval.");
        }

        if (bill.NeedsDiscountApproval)
        {
            throw AppException.Forbidden("The discounts on this bill need a supervisor's approval.");
        }

        if (bill.Result.GrandTotal != request.ExpectedGrandTotal)
        {
            throw AppException.Conflict("invoice.total_changed",
                $"The bill total is Rs. {bill.Result.GrandTotal:0.00}, not Rs. {request.ExpectedGrandTotal:0.00} (a price changed). Check the bill and collect again.");
        }

        var now = clock.GetUtcNow();
        var prefix = pos.Counter.InvoicePrefix(bill.RegistrationIndex);
        var sequence = await numbers.NextAsync(businessId, pos.Store.Id, Counter.InvoiceSeries(prefix), cancellationToken).ConfigureAwait(false);
        var invoiceId = Guid.CreateVersion7(now);
        var number = Counter.InvoiceNumber(prefix, sequence);
        var payments = (request.Payments ?? []).Select(p => new PaymentInput(p.Method, p.Amount, p.Reference)).ToList();
        var invoice = SalesInvoice.Issue(invoiceId, businessId, pos.Store.Id, pos.Counter, pos.Device.Id, prefix, sequence, bill.Registration.Mode, bill.Channel,
            bill.BusinessDate, currentUser.UserId, bill.Seller, bill.Buyer, bill.PlaceOfSupply, bill.Result, payments, discountApproval?.Id,
            request.NegativeStockOverride, request.IdempotencyKey, requestHash, now);

        // Stock leaves with its cost, under the same locks and negative-stock rules as every other movement.
        await stock.StartAsync(new StockPostingDocument(businessId, LedgerDocumentType, invoiceId, number, request.NegativeStockOverride, bill.BusinessDate, now),
            cancellationToken).ConfigureAwait(false);
        await stock.LockAsync(bill.Lines.Select(l => (pos.Store.Id, l.Variant.Id)), cancellationToken).ConfigureAwait(false);
        foreach (var line in bill.Lines)
        {
            var taken = await stock.IssueAsync(pos.Store.Id, new StockItem(line.Variant.Id, line.Variant.Name, line.Product.Id), line.BaseQuantity,
                MovementTypes.Sale, line.Request.BatchId, countLoss: false, cancellationToken).ConfigureAwait(false);
            var cost = taken.Sum(t => StockMath.Value(t.Quantity, t.UnitCost));
            line.OverrideApproval?.Use(pos.Counter.Id, currentUser.UserId, invoiceId, now);
            invoice.AddLine(SalesInvoiceLine.Create(businessId, invoiceId, line.LineNumber,
                new SalesInvoiceLine.Item(line.Product.Id, line.Variant.Id, line.Pack.Id, line.Variant.Name, line.Product.HsnSac, line.UnitCode, line.Request.Quantity,
                    line.BaseQuantity, line.Mrp),
                new SalesInvoiceLine.Pricing(line.Rule?.Id, line.RateType, line.OverrideApproval?.Id, line.UnitPrice, line.TaxInclusive, line.Product.SupplyType,
                    line.Product.GstRatePercent, line.Product.CessRatePercent),
                bill.Result.Lines[line.LineNumber - 1], cost, now));
        }

        await RedeemCreditNotesAsync(businessId, invoiceId, payments, now, cancellationToken).ConfigureAwait(false);
        discountApproval?.Use(pos.Counter.Id, currentUser.UserId, invoiceId, now);
        pos.Device.Seen(now);
        db.SalesInvoices.Add(invoice);
        stock.Flush();
        audit.Record("sales.invoice_issued", "sales_invoice", invoiceId, businessId, pos.Store.Id, details: new
        {
            invoice.Number,
            invoice.Kind,
            counter = pos.Counter.Code,
            device = pos.Device.Name,
            invoice.GrandTotal,
            invoice.DiscountTotal,
            lines = bill.Lines.Count,
            overrides = bill.Lines.Where(l => l.Rule is null).Select(l => new { l.Pack.Id, l.UnitPrice, l.RateType, approval = l.OverrideApproval?.Id }),
            belowMinimum = bill.Lines.Count(l => l.BelowMinimum),
            discountApproval = discountApproval?.Id,
            payments = payments.Select(p => new { p.Method, p.Amount }),
            invoice.NegativeStockOverride,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await InvoiceAsync(invoiceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Store credit used to pay: each credit note is locked, its remaining credit checked, and the use recorded, so the
    /// same credit can never be spent twice (not even by two counters at once).
    /// </summary>
    private async Task RedeemCreditNotesAsync(Guid businessId, Guid invoiceId, IReadOnlyList<PaymentInput> payments, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var group in payments.Where(p => p.Method == PaymentMethods.CreditNote).GroupBy(p => p.Reference!.Trim().ToUpperInvariant()))
        {
            var number = group.Key;
            var note = (await db.SalesReturns.FromSql($"SELECT * FROM sales_returns WHERE business_id = {businessId} AND number = {number} FOR UPDATE")
                    .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault() ?? throw AppException.NotFound($"Credit note {number}");
            var redeemed = await db.CreditNoteRedemptions.Where(r => r.ReturnId == note.Id).SumAsync(r => (decimal?)r.Amount, cancellationToken).ConfigureAwait(false) ?? 0;
            try
            {
                db.CreditNoteRedemptions.Add(CreditNoteRedemption.Redeem(note, redeemed, invoiceId, group.Sum(p => p.Amount), now));
            }
            catch (DomainException e)
            {
                throw AppException.Conflict(e.Code, e.Message);
            }
        }
    }

    /// <summary>An invoice issued on this device's counter (for reprinting at the counter).</summary>
    public async Task<InvoiceDto> CounterInvoiceAsync(string? deviceToken, Guid invoiceId, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        if (!await db.SalesInvoices.AnyAsync(i => i.Id == invoiceId && i.CounterId == pos.Counter.Id, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Invoice");
        }

        return await InvoiceAsync(invoiceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The PDF of an invoice issued on this device's counter.</summary>
    public async Task<(byte[] Content, string FileName)> CounterInvoicePdfAsync(string? deviceToken, Guid invoiceId, CancellationToken cancellationToken) =>
        await PdfAsync(await CounterInvoiceAsync(deviceToken, invoiceId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<(byte[] Content, string FileName)> InvoicePdfAsync(Guid businessId, Guid invoiceId, CancellationToken cancellationToken) =>
        await PdfAsync(await GetAsync(businessId, invoiceId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<(byte[] Content, string FileName)> PdfAsync(InvoiceDto invoice, CancellationToken cancellationToken)
    {
        var timeZone = await db.Stores.AsNoTracking().Where(s => s.Id == invoice.StoreId).Select(s => s.TimeZone).FirstAsync(cancellationToken).ConfigureAwait(false);
        return (Documents.InvoicePdf.Render(invoice, timeZone), invoice.Number + ".pdf");
    }

    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListAsync(Guid businessId, Guid storeId, DateOnly? date, string? search, CancellationToken cancellationToken)
    {
        await RequireSalesViewAsync(businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.SalesInvoices.AsNoTracking().Where(i => i.StoreId == storeId);
        if (date is { } d)
        {
            query = query.Where(i => i.BusinessDate == d);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(i => i.Number == term || (i.BuyerName != null && EF.Functions.ILike(i.BuyerName, "%" + search.Trim() + "%")));
        }

        return await (
                from i in query
                join c in db.Counters.AsNoTracking() on i.CounterId equals c.Id
                join u in db.Users.AsNoTracking() on i.CashierUserId equals u.Id
                orderby i.IssuedAtUtc descending
                select new InvoiceSummaryDto(i.Id, i.Number, i.Kind, i.BusinessDate, i.IssuedAtUtc, c.Code, u.DisplayName, i.BuyerName, i.GrandTotal))
            .Take(500)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvoiceDto> GetAsync(Guid businessId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var storeId = await db.SalesInvoices.AsNoTracking().Where(i => i.Id == invoiceId && i.BusinessId == businessId).Select(i => (Guid?)i.StoreId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Invoice");
        await RequireSalesViewAsync(businessId, storeId, cancellationToken).ConfigureAwait(false);
        return await InvoiceAsync(invoiceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireSalesViewAsync(Guid businessId, Guid storeId, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(Permissions.SalesView, businessId, storeId, cancellationToken).ConfigureAwait(false))
        {
            var visible = await access.BusinessesWithPermissionAsync(Permissions.StoresView, cancellationToken).ConfigureAwait(false);
            throw visible.Contains(businessId) ? AppException.Forbidden() : AppException.NotFound("Business");
        }
    }

    private async Task<BuiltBill> BuildAsync(
        PosDevice pos, CartRequest cart, Dictionary<string, SupervisorApproval>? approvals, SupervisorApproval? discountApproval,
        CancellationToken cancellationToken)
    {
        var channel = (cart.Channel ?? string.Empty).Trim().ToUpperInvariant();
        if (channel is not (SalesChannels.Retail or SalesChannels.Wholesale))
        {
            throw AppException.Validation("invoice.channel_invalid", "Choose retail or wholesale billing.");
        }

        if (cart.Lines is null || cart.Lines.Count == 0 || cart.Lines.Count > 300)
        {
            throw AppException.Validation("invoice.lines_required", "A bill needs 1 to 300 items.");
        }

        var now = clock.GetUtcNow();
        var businessDate = BusinessCalendar.Today(clock, pos.Store.TimeZone);
        var (registration, registrationIndex) = await RegistrationAsync(pos, cancellationToken).ConfigureAwait(false);
        var (buyer, placeOfSupply) = Buyer(cart.Buyer, pos.Store.StateCode);
        if (registration.Mode == TaxRegistrationModes.GstComposition && placeOfSupply != pos.Store.StateCode)
        {
            throw AppException.Validation("composition.inter_state", "A composition dealer cannot sell to another state. Bill the buyer in this state or not at all.");
        }

        var sellerGstin = registration.Mode == TaxRegistrationModes.NotGstRegistered ? null : pos.Store.Gstin ?? registration.Gstin;
        if (registration.Mode != TaxRegistrationModes.NotGstRegistered && sellerGstin is null)
        {
            throw AppException.Conflict("gstin.missing", "The store has no GSTIN on record, so a GST invoice cannot be issued.");
        }

        var seller = new SalesInvoice.Seller(pos.Business.LegalName, sellerGstin, pos.Store.Address ?? pos.Business.Address ?? pos.Store.Name, pos.Store.StateCode);
        var canOverride = await CanAsync(pos, Permissions.PosPriceOverride, cancellationToken).ConfigureAwait(false);
        var canDiscount = await CanAsync(pos, Permissions.PosDiscount, cancellationToken).ConfigureAwait(false);

        var packIds = cart.Lines.Select(l => l.VariantUnitId).Distinct().ToList();
        var packs = await (
                from vu in db.VariantUnits.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on vu.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                join packUnit in db.Units.AsNoTracking() on vu.UnitId equals packUnit.Id
                join baseUnit in db.Units.AsNoTracking() on p.BaseUnitId equals baseUnit.Id
                where packIds.Contains(vu.Id) && vu.BusinessId == pos.Counter.BusinessId
                select new { Pack = vu, Variant = v, Product = p, PackUnitCode = packUnit.Code, BaseUnit = baseUnit })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var rules = await db.PriceRules.AsNoTracking().Where(r => packIds.Contains(r.VariantUnitId) && r.Status == PriceRuleStatus.Active)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var mrps = await db.VariantMrps.AsNoTracking().Where(m => packIds.Contains(m.VariantUnitId) && m.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var lines = new List<BuiltLine>();
        var inputs = new List<BillLineInput>();
        foreach (var request in cart.Lines)
        {
            var row = packs.FirstOrDefault(p => p.Pack.Id == request.VariantUnitId) ?? throw AppException.NotFound("Item pack");
            var name = row.Variant.Name;
            if (!row.Pack.IsActive || !row.Variant.IsActive || !row.Product.IsActive)
            {
                throw AppException.Conflict("item.inactive", $"{name} is no longer sold.");
            }

            if (request.Quantity <= 0)
            {
                throw AppException.Validation("invoice.quantity_invalid", $"{name}: the quantity must be more than zero.");
            }

            var baseQuantity = row.Pack.ToBase(request.Quantity);
            if (StockMath.Quantity(baseQuantity) != baseQuantity)
            {
                throw AppException.Validation("invoice.quantity_precision", $"{name}: at most {StockMath.QuantityScale} decimals in {row.BaseUnit.Code}.");
            }

            row.BaseUnit.ValidateQuantity(baseQuantity);
            var mrp = ChooseMrp(name, request.Mrp, mrps.Where(m => m.VariantUnitId == row.Pack.Id).Select(m => m.Mrp).Distinct().ToList());
            var taxRate = row.Product.GstRatePercent + row.Product.CessRatePercent;
            var quote = PriceResolver.Resolve(rules.Where(r => r.VariantUnitId == row.Pack.Id),
                new PriceQuery(row.Pack.Id, request.Quantity, channel, pos.Store.Id, null, false, mrp, taxRate, now));

            PriceRule? rule = null;
            SupervisorApproval? overrideApproval = null;
            var needsApproval = false;
            decimal unitPrice;
            bool taxInclusive;
            string rateType;
            if (request.OverridePrice is { } overridePrice)
            {
                unitPrice = overridePrice;
                if (unitPrice < 0 || unitPrice != InvoiceCalculator.Money(unitPrice))
                {
                    throw AppException.Validation("price.invalid", $"{name}: enter the price in rupees and paise.");
                }

                taxInclusive = quote.Rule?.TaxInclusive ?? true;

                // With a supervisor's token the override is theirs; without one, only someone allowed to override may.
                // A preview (no approvals loaded) trusts that a token was obtained; issuing checks it.
                var hasToken = !string.IsNullOrEmpty(request.OverrideApprovalToken);
                if (hasToken && approvals is not null)
                {
                    overrideApproval = approvals[request.OverrideApprovalToken!];
                    if (overrideApproval.Kind != SupervisorApprovalKinds.PriceOverride || overrideApproval.VariantUnitId != row.Pack.Id
                        || overrideApproval.ApprovedPrice != unitPrice)
                    {
                        throw AppException.Forbidden($"{name}: the supervisor's approval does not match this item and price.");
                    }
                }

                needsApproval = !hasToken && !canOverride;
                rateType = hasToken ? SaleRateTypes.Override : SaleRateTypes.OverrideSelf;
            }
            else
            {
                rule = quote.Rule ?? throw AppException.Conflict("price.missing", $"{name} has no price for {channel.ToLowerInvariant()} billing. Ask a manager to add one.");
                unitPrice = rule.Price;
                taxInclusive = rule.TaxInclusive;
                rateType = rule.RateType;
            }

            var collectsTax = registration.Mode == TaxRegistrationModes.GstRegular && row.Product.SupplyType == SupplyTypes.Taxable;
            var inclusiveUnitPrice = collectsTax ? PriceMath.InclusiveOf(unitPrice, taxInclusive, taxRate) : unitPrice;
            if (mrp is { } cap && inclusiveUnitPrice > cap)
            {
                throw AppException.Validation("price.above_mrp", $"{name}: Rs. {inclusiveUnitPrice:0.00} is above the MRP of Rs. {cap:0.00}.");
            }

            var gross = InvoiceCalculator.Money(request.Quantity * unitPrice);
            var itemDiscount = Discount(name, request.DiscountAmount, request.DiscountPercent, gross);
            lines.Add(new BuiltLine(lines.Count + 1, request, row.Pack, row.Variant, row.Product, row.PackUnitCode, baseQuantity, mrp, rule, rateType, unitPrice,
                taxInclusive, overrideApproval, quote.MinimumPriceInclusive, needsApproval));
            inputs.Add(new BillLineInput(request.Quantity, unitPrice, taxInclusive, row.Product.SupplyType, row.Product.GstRatePercent, row.Product.CessRatePercent,
                itemDiscount));
        }

        var available = inputs.Sum(i => InvoiceCalculator.Money(i.Quantity * i.UnitPrice) - i.ItemDiscount);
        var billDiscount = Discount("The bill", cart.BillDiscountAmount, cart.BillDiscountPercent, available);
        BillResult result;
        try
        {
            result = InvoiceCalculator.Calculate(new BillInput(registration.Mode, placeOfSupply != pos.Store.StateCode, inputs, billDiscount));
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        if (discountApproval is not null && discountApproval.Kind != SupervisorApprovalKinds.Discount)
        {
            throw AppException.Forbidden("The discount approval on this bill is not a discount approval.");
        }

        if (discountApproval is not null && result.Discount > discountApproval.MaxAmount)
        {
            throw AppException.Forbidden($"The discounts (Rs. {result.Discount:0.00}) are more than the supervisor approved (Rs. {discountApproval.MaxAmount:0.00}).");
        }

        var discountAllowed = canDiscount || discountApproval is not null;

        // Below the minimum selling price is reported, and only reachable through an authorised override or discount.
        foreach (var line in lines)
        {
            line.BelowMinimum = line.MinimumPriceInclusive is { } floor
                && result.Lines[line.LineNumber - 1].Total < InvoiceCalculator.Money(floor * line.Request.Quantity);
        }

        return new BuiltBill(registration, registrationIndex, channel, businessDate, seller, buyer, placeOfSupply, lines, result, result.Discount > 0 && !discountAllowed);
    }

    private static decimal Discount(string what, decimal? amount, decimal? percent, decimal of)
    {
        if (amount is not null && percent is not null)
        {
            throw AppException.Validation("discount.ambiguous", $"{what}: give the discount as an amount or a percentage, not both.");
        }

        if (percent is { } p)
        {
            return p is >= 0 and <= 100
                ? InvoiceCalculator.Money(of * p / 100)
                : throw AppException.Validation("discount.percent_invalid", $"{what}: a discount percentage is between 0 and 100.");
        }

        return amount is { } a
            ? a >= 0 && a == InvoiceCalculator.Money(a) ? a : throw AppException.Validation("discount.amount_invalid", $"{what}: enter the discount in rupees and paise.")
            : 0;
    }

    private static decimal? ChooseMrp(string name, decimal? requested, List<decimal> active)
    {
        if (requested is { } mrp)
        {
            return active.Contains(mrp) ? mrp : throw AppException.Validation("mrp.unknown", $"{name} has no MRP of Rs. {mrp:0.00}.");
        }

        return active.Count switch
        {
            0 => null,
            1 => active[0],
            _ => throw AppException.Validation("mrp.choose", $"{name} has more than one MRP ({string.Join(", ", active.Order().Select(m => $"Rs. {m:0.00}"))}). Choose the one on the pack."),
        };
    }

    private static (SalesInvoice.Buyer Buyer, string PlaceOfSupply) Buyer(BuyerRequest? request, string storeState)
    {
        if (request is null)
        {
            return (new SalesInvoice.Buyer(null, null, null, null), storeState);
        }

        string? gstin = null;
        var state = string.IsNullOrWhiteSpace(request.StateCode) ? storeState : request.StateCode.Trim();
        if (!string.IsNullOrWhiteSpace(request.Gstin))
        {
            gstin = Gstin.Normalize(request.Gstin);
            if (!Gstin.IsValid(gstin))
            {
                throw AppException.Validation("buyer.gstin_invalid", "The buyer's GSTIN is not valid (check the 15 characters).");
            }

            state = Gstin.StateCode(gstin);
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                throw AppException.Validation("buyer.name_required", "A buyer with a GSTIN needs their name on the invoice.");
            }
        }

        if (state.Length != 2 || !state.All(char.IsAsciiDigit))
        {
            throw AppException.Validation("buyer.state_invalid", "The place of supply is a two-digit state code.");
        }

        return (new SalesInvoice.Buyer(request.Name, gstin, request.Phone, request.Address), state);
    }

    private Task<(TaxRegistration Registration, int Index)> RegistrationAsync(PosDevice pos, CancellationToken cancellationToken) =>
        RegistrationInForceAsync(db, pos.Counter.BusinessId, BusinessCalendar.Today(clock, pos.Store.TimeZone), cancellationToken);

    /// <summary>The registration in force on a date, and its position in the business's history (which picks the invoice series).</summary>
    internal static async Task<(TaxRegistration Registration, int Index)> RegistrationInForceAsync(
        SupermarketBillingDbContext db, Guid businessId, DateOnly date, CancellationToken cancellationToken)
    {
        var history = await db.TaxRegistrations.AsNoTracking().Where(r => r.BusinessId == businessId).OrderBy(r => r.EffectiveFrom)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var current = TaxRegistration.InForce(history, date)
            ?? throw AppException.Conflict("tax_mode.missing", "This business has no tax registration in force today.");
        return (current, history.FindIndex(r => r.Id == current.Id));
    }

    private async Task<Dictionary<string, SupervisorApproval>> LockApprovalsAsync(IEnumerable<string?> tokens, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SupervisorApproval>(StringComparer.Ordinal);
        foreach (var token in tokens.Where(t => !string.IsNullOrEmpty(t)).Distinct())
        {
            var hash = SecretTokens.Hash(token!);
            var approval = (await db.SupervisorApprovals.FromSql($"SELECT * FROM supervisor_approvals WHERE token_hash = {hash} FOR UPDATE")
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault() ?? throw AppException.Forbidden("A supervisor approval on this bill is not valid. Ask the supervisor again.");
            result[token!] = approval;
        }

        return result;
    }

    private Task<bool> CanAsync(PosDevice pos, string permission, CancellationToken cancellationToken) =>
        access.HasPermissionAsync(permission, pos.Counter.BusinessId, pos.Counter.StoreId, cancellationToken);

    internal async Task<InvoiceDto> InvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var header = await (
                from inv in db.SalesInvoices.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments)
                join c in db.Counters.AsNoTracking() on inv.CounterId equals c.Id
                join u in db.Users.AsNoTracking() on inv.CashierUserId equals u.Id
                where inv.Id == invoiceId
                select new { Invoice = inv, CounterCode = c.Code, Cashier = u.DisplayName })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var i = header.Invoice;
        var lines = i.Lines.OrderBy(l => l.LineNumber).Select(l => new CartLineDto(
            l.LineNumber, l.VariantId, l.VariantUnitId, l.Description, l.UnitCode, l.HsnSac, l.Quantity, l.Mrp, l.UnitPrice, l.TaxInclusive, l.RateType, l.PriceRuleId,
            false, false, l.SupplyType, l.GstRatePercent, l.CessRatePercent, l.Gross, l.ItemDiscount, l.BillDiscount, l.Taxable, l.Cgst, l.Sgst, l.Igst, l.Cess,
            l.Total)).ToList();
        return new InvoiceDto(
            i.Id, i.Number, i.Kind, i.TaxMode, i.Channel, i.BusinessDate, i.IssuedAtUtc, i.StoreId, i.CounterId, header.CounterCode, header.Cashier, i.SellerName,
            i.SellerGstin, i.SellerAddress, i.SellerStateCode, i.BuyerName, i.BuyerGstin, i.BuyerPhone, i.BuyerAddress, i.PlaceOfSupplyStateCode, i.IsInterState,
            lines, i.GrossTotal, i.DiscountTotal, i.TaxableTotal, i.CgstTotal, i.SgstTotal, i.IgstTotal, i.CessTotal, i.RoundOff, i.GrandTotal, i.PaidTotal,
            i.ChangeDue, i.Payments.OrderBy(p => p.PaymentOrder).Select(p => new InvoicePaymentDto(p.Method, p.Amount, p.Reference)).ToList(),
            i.TaxMode == TaxRegistrationModes.GstComposition ? InvoiceKinds.CompositionDeclaration : null);
    }

    private static CartDto ToCartDto(BuiltBill bill)
    {
        var r = bill.Result;
        var lines = bill.Lines.Select(l =>
        {
            var a = r.Lines[l.LineNumber - 1];
            return new CartLineDto(
                l.LineNumber, l.Variant.Id, l.Pack.Id, l.Variant.Name, l.UnitCode, l.Product.HsnSac, l.Request.Quantity, l.Mrp, l.UnitPrice, l.TaxInclusive, l.RateType,
                l.Rule?.Id, l.BelowMinimum, l.NeedsPriceApproval, l.Product.SupplyType, l.Product.GstRatePercent, l.Product.CessRatePercent, a.Gross, a.ItemDiscount,
                a.BillDiscount, a.Taxable, a.Cgst, a.Sgst, a.Igst, a.Cess, a.Total);
        }).ToList();
        return new CartDto(r.Kind, bill.Registration.Mode, bill.PlaceOfSupply != bill.Seller.StateCode, bill.PlaceOfSupply, lines, r.Gross, r.Discount, r.Taxable,
            r.Cgst, r.Sgst, r.Igst, r.Cess, r.RoundOff, r.GrandTotal, bill.NeedsDiscountApproval);
    }

    private static string Hash(IssueInvoiceRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HashJson))));

    private sealed class BuiltLine(
        int lineNumber, CartLineRequest request, VariantUnit pack, ProductVariant variant, Product product, string unitCode, decimal baseQuantity, decimal? mrp,
        PriceRule? rule, string rateType, decimal unitPrice, bool taxInclusive, SupervisorApproval? overrideApproval, decimal? minimumPriceInclusive,
        bool needsPriceApproval)
    {
        public int LineNumber { get; } = lineNumber;

        public CartLineRequest Request { get; } = request;

        public VariantUnit Pack { get; } = pack;

        public ProductVariant Variant { get; } = variant;

        public Product Product { get; } = product;

        public string UnitCode { get; } = unitCode;

        public decimal BaseQuantity { get; } = baseQuantity;

        public decimal? Mrp { get; } = mrp;

        public PriceRule? Rule { get; } = rule;

        public string RateType { get; } = rateType;

        public decimal UnitPrice { get; } = unitPrice;

        public bool TaxInclusive { get; } = taxInclusive;

        public SupervisorApproval? OverrideApproval { get; } = overrideApproval;

        public decimal? MinimumPriceInclusive { get; } = minimumPriceInclusive;

        public bool NeedsPriceApproval { get; set; } = needsPriceApproval;

        public bool BelowMinimum { get; set; }
    }

    private sealed record BuiltBill(
        TaxRegistration Registration, int RegistrationIndex, string Channel, DateOnly BusinessDate, SalesInvoice.Seller Seller, SalesInvoice.Buyer Buyer, string PlaceOfSupply,
        List<BuiltLine> Lines, BillResult Result, bool NeedsDiscountApproval);
}
