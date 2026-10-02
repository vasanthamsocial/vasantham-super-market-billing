using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Infrastructure.Accounts;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Documents;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Sales;

namespace SupermarketBilling.Infrastructure.Purchases;

/// <summary>
/// Purchase returns (debit notes) against posted goods receipts. The receipt is locked while a return is made, so
/// concurrent returns cannot together send back more than was received. Stock goes out from the receipt's own cost
/// layer first; the value and taxes the supplier credits are deducted from what is owed to them, first from that receipt.
/// </summary>
public sealed class PurchaseReturnService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    DocumentNumbers numbers,
    StockEngine stock,
    PartyLedgerService ledger,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string Series = "DN";
    public const string LedgerDocumentType = "PURCHASE_RETURN";
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    public async Task<ReturnableGrnDto> ReturnableAsync(Guid businessId, Guid grnId, CancellationToken cancellationToken)
    {
        var grn = await GrnAsync(businessId, grnId, cancellationToken).ConfigureAwait(false);
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, grn.StoreId, cancellationToken).ConfigureAwait(false);
        var lines = await db.GrnLines.AsNoTracking().Where(l => l.GrnId == grnId).OrderBy(l => l.LineNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        var returned = await ReturnedAsync(grnId, cancellationToken).ConfigureAwait(false);
        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == grn.SupplierId).Select(s => s.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
        var numbersSoFar = await db.PurchaseReturns.AsNoTracking().Where(r => r.GrnId == grnId).OrderBy(r => r.CreatedAtUtc).Select(r => r.Number)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ReturnableGrnDto(grn.Id, grn.Number, grn.Status, grn.StoreId, grn.SupplierId, supplier, grn.SupplierInvoiceNumber, grn.SupplierInvoiceDate,
            lines.Select(l =>
            {
                var received = l.Quantity + l.FreeQuantity;
                var back = returned.GetValueOrDefault(l.Id).Quantity;
                return new ReturnableGrnLineDto(l.Id, l.LineNumber, l.Description, l.UnitCode, received, back, received - back, l.BatchNumber, l.ExpiresOn,
                    received == 0 ? 0 : decimal.Round(l.Total / received, 2));
            }).ToList(),
            numbersSoFar);
    }

    public async Task<PurchaseReturnDto> PreviewAsync(Guid businessId, PurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grn = await GrnAsync(businessId, request.GrnId, cancellationToken).ConfigureAwait(false);
        await organisation.RequireAsync(Permissions.PurchasesManage, businessId, grn.StoreId, cancellationToken).ConfigureAwait(false);
        var built = await BuildAsync(grn, request, cancellationToken).ConfigureAwait(false);
        var supplier = await db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == grn.SupplierId, cancellationToken).ConfigureAwait(false);
        var totals = built.Lines.Aggregate(LineAmounts.Zero, (sum, l) => sum.Plus(l.Amounts));
        var stockValue = built.Lines.Sum(l => decimal.Round(l.BaseQuantity * l.GrnLine.LandedUnitCost, 2));
        return new PurchaseReturnDto(Guid.Empty, "PREVIEW", grn.StoreId, grn.SupplierId, supplier.Name, supplier.Gstin, supplier.StateCode, grn.Id, grn.Number,
            grn.SupplierInvoiceNumber, grn.SupplierInvoiceDate, BusinessCalendar.Today(clock), (request.Reason ?? string.Empty).Trim(), grn.IsInterState, grn.TaxRecoverable,
            built.Lines.Select((l, i) => new PurchaseReturnLineDto(i + 1, l.GrnLine.Id, l.GrnLine.Description, l.GrnLine.UnitCode, l.Quantity, l.BaseQuantity,
                l.Amounts.Taxable, l.Amounts.Cgst, l.Amounts.Sgst, l.Amounts.Igst, l.Amounts.Cess, l.Amounts.Total,
                decimal.Round(l.BaseQuantity * l.GrnLine.LandedUnitCost, 2))).ToList(),
            totals.Taxable, totals.Cgst, totals.Sgst, totals.Igst, totals.Cess, built.RoundOff, totals.Total + built.RoundOff, stockValue, null, null);
    }

    public async Task<PurchaseReturnDto> CreateAsync(Guid businessId, PurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.IdempotencyKey ?? throw AppException.Validation("idempotency.key_required", "Each purchase return needs an idempotency key.");
        var storeId = (await GrnAsync(businessId, request.GrnId, cancellationToken).ConfigureAwait(false)).StoreId;
        await organisation.RequireAsync(Permissions.PurchasesManage, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HashJson))));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"purchase-return|" + businessId + "|" + key}))", cancellationToken).ConfigureAwait(false);
        var existing = await db.PurchaseReturns.AsNoTracking().Where(r => r.BusinessId == businessId && r.IdempotencyKey == key)
            .Select(r => new { r.Id, r.RequestHash }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? await GetCoreAsync(existing.Id, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("idempotency.mismatch", "This purchase return was already saved with different contents.");
        }

        // The receipt is locked first: returns against it queue here, and what is still returnable is read after the lock.
        var grn = (await db.Grns.FromSql($"SELECT *, xmin FROM grns WHERE id = {request.GrnId} FOR UPDATE").AsNoTracking().ToListAsync(cancellationToken)
            .ConfigureAwait(false)).Single();
        var built = await BuildAsync(grn, request, cancellationToken).ConfigureAwait(false);
        var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == grn.StoreId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var date = BusinessCalendar.Today(clock, store.TimeZone);
        var sequence = await numbers.NextAsync(businessId, store.Id, Series, cancellationToken).ConfigureAwait(false);
        var number = DocumentNumbers.Format(store.Code, Series, sequence);
        var id = Guid.CreateVersion7(now);

        // Stock out: from the cost layer this receipt created, then in valuation order (same batch for batch-tracked goods).
        await stock.StartAsync(new StockPostingDocument(businessId, LedgerDocumentType, id, number, false, date, now), cancellationToken).ConfigureAwait(false);
        await stock.LockAsync(built.Lines.Select(l => (store.Id, l.GrnLine.VariantId)), cancellationToken).ConfigureAwait(false);
        var receiptLayers = await db.StockLedger.AsNoTracking()
            .Where(e => e.DocumentId == grn.Id && e.DocumentType == GrnPoster.LedgerDocumentType && e.MovementType == MovementTypes.Receipt)
            .Select(e => new { e.VariantId, e.BatchId, e.LayerId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<PurchaseReturnLine>();
        foreach (var (line, index) in built.Lines.Select((l, i) => (l, i)))
        {
            var layer = receiptLayers.FirstOrDefault(r => r.VariantId == line.GrnLine.VariantId && r.BatchId == line.BatchId);
            var taken = await stock.IssueFromLayerFirstAsync(store.Id, new StockItem(line.GrnLine.VariantId, line.GrnLine.Description, line.GrnLine.ProductId),
                line.BaseQuantity, MovementTypes.PurchaseReturn, line.BatchId, layer?.LayerId, cancellationToken).ConfigureAwait(false);
            var value = decimal.Round(taken.Sum(t => t.Quantity * t.UnitCost), 2);
            lines.Add(PurchaseReturnLine.Create(businessId, id, index + 1, line.GrnLine, line.Quantity, line.Amounts, value, now));
        }

        stock.Flush();
        var totals = built.Lines.Aggregate(LineAmounts.Zero, (sum, l) => sum.Plus(l.Amounts));
        PurchaseReturn purchaseReturn;
        try
        {
            purchaseReturn = PurchaseReturn.Create(id, businessId, grn, number, sequence, date, request.Reason, totals, built.RoundOff, lines.Sum(l => l.StockValue),
                currentUser.UserId, key, requestHash, now);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.PurchaseReturns.Add(purchaseReturn);
        db.PurchaseReturnLines.AddRange(lines);

        // The supplier owes it back: first against this receipt (while unpaid), any rest stays as a credit for later bills.
        if (purchaseReturn.Total > 0)
        {
            var entry = await ledger.PostAsync(PartyTypes.Supplier, businessId, grn.SupplierId,
                new PartyLedgerEntry.Posting(LedgerEntryTypes.DebitNote, store.Id, id, number, date, null, -purchaseReturn.Total,
                    $"Debit note {number} against {grn.Number}: {purchaseReturn.Reason}"),
                currentUser.UserId, now, cancellationToken).ConfigureAwait(false);
            var bill = await db.SupplierLedger.AsNoTracking().Where(e => e.DocumentId == grn.Id && e.EntryType == LedgerEntryTypes.Grn).Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var (charges, _) = await ledger.OpenItemsAsync(PartyTypes.Supplier, grn.SupplierId, cancellationToken).ConfigureAwait(false);
            if (charges.FirstOrDefault(c => c.EntryId == bill) is { } open)
            {
                await ledger.ApplyPaymentAsync(PartyTypes.Supplier, businessId, grn.SupplierId, entry.Id,
                    [new SettlementAllocation(open.EntryId, Math.Min(open.Remaining, purchaseReturn.Total))], now, cancellationToken).ConfigureAwait(false);
            }
        }

        audit.Record("purchase_return.created", "purchase_return", id, businessId, store.Id, details: new
        {
            number, grn = grn.Number, purchaseReturn.Total, purchaseReturn.StockValue, purchaseReturn.Reason,
            lines = lines.Select(l => new { l.Description, l.Quantity, l.Total }),
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PurchaseReturnSummaryDto>> ListAsync(Guid businessId, Guid storeId, Guid? supplierId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.PurchaseReturns.AsNoTracking().Where(r => r.StoreId == storeId);
        query = supplierId is { } supplier ? query.Where(r => r.SupplierId == supplier) : query;
        return await (from r in query
                      join s in db.Suppliers.AsNoTracking() on r.SupplierId equals s.Id
                      join g in db.Grns.AsNoTracking() on r.GrnId equals g.Id
                      orderby r.CreatedAtUtc descending
                      select new PurchaseReturnSummaryDto(r.Id, r.Number, r.BusinessDate, s.Name, g.Number, r.Reason, r.Total))
            .Take(300).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurchaseReturnDto> GetAsync(Guid businessId, Guid returnId, CancellationToken cancellationToken)
    {
        var storeId = await db.PurchaseReturns.AsNoTracking().Where(r => r.Id == returnId && r.BusinessId == businessId).Select(r => (Guid?)r.StoreId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Purchase return");
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(returnId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(byte[] Content, string FileName)> PdfAsync(Guid businessId, Guid returnId, CancellationToken cancellationToken)
    {
        var note = await GetAsync(businessId, returnId, cancellationToken).ConfigureAwait(false);
        var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == note.StoreId, cancellationToken).ConfigureAwait(false);
        var business = await db.Businesses.AsNoTracking().FirstAsync(b => b.Id == businessId, cancellationToken).ConfigureAwait(false);
        var (registration, _) = await BillingService.RegistrationInForceAsync(db, businessId, note.BusinessDate, cancellationToken).ConfigureAwait(false);
        var issuer = new DebitNotePdf.Issuer(business.LegalName, store.Gstin ?? registration.Gstin, store.Address ?? business.Address ?? store.Name, store.StateCode);
        return (DebitNotePdf.Render(note, issuer), $"{note.Number.Replace('/', '-')}.pdf");
    }

    private async Task<PurchaseReturnDto> GetCoreAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var row = await (from x in db.PurchaseReturns.AsNoTracking()
                         join s in db.Suppliers.AsNoTracking() on x.SupplierId equals s.Id
                         join g in db.Grns.AsNoTracking() on x.GrnId equals g.Id
                         join u in db.Users.AsNoTracking() on x.CreatedByUserId equals u.Id
                         where x.Id == returnId
                         select new { Return = x, Supplier = s, Grn = g, CreatedBy = u.DisplayName }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var r = row.Return;
        var lines = await db.PurchaseReturnLines.AsNoTracking().Where(l => l.PurchaseReturnId == returnId).OrderBy(l => l.LineNumber)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PurchaseReturnDto(r.Id, r.Number, r.StoreId, r.SupplierId, row.Supplier.Name, row.Supplier.Gstin, row.Supplier.StateCode, r.GrnId, row.Grn.Number,
            row.Grn.SupplierInvoiceNumber, row.Grn.SupplierInvoiceDate, r.BusinessDate, r.Reason, r.IsInterState, r.TaxRecoverable,
            lines.Select(l => new PurchaseReturnLineDto(l.LineNumber, l.GrnLineId, l.Description, l.UnitCode, l.Quantity, l.BaseQuantity, l.Taxable, l.Cgst, l.Sgst,
                l.Igst, l.Cess, l.Total, l.StockValue)).ToList(),
            r.Taxable, r.Cgst, r.Sgst, r.Igst, r.Cess, r.RoundOff, r.Total, r.StockValue, row.CreatedBy, r.CreatedAtUtc);
    }

    private async Task<Grn> GrnAsync(Guid businessId, Guid grnId, CancellationToken cancellationToken) =>
        await db.Grns.AsNoTracking().FirstOrDefaultAsync(g => g.Id == grnId && g.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.NotFound("Goods receipt");

    /// <summary>Quantity and amounts already returned per receipt line.</summary>
    private async Task<Dictionary<Guid, (decimal Quantity, LineAmounts Amounts)>> ReturnedAsync(Guid grnId, CancellationToken cancellationToken)
    {
        var rows = await (from l in db.PurchaseReturnLines.AsNoTracking()
                          join r in db.PurchaseReturns.AsNoTracking() on l.PurchaseReturnId equals r.Id
                          where r.GrnId == grnId
                          group l by l.GrnLineId into g
                          select new
                          {
                              g.Key,
                              Quantity = g.Sum(x => x.Quantity),
                              Taxable = g.Sum(x => x.Taxable),
                              Cgst = g.Sum(x => x.Cgst),
                              Sgst = g.Sum(x => x.Sgst),
                              Igst = g.Sum(x => x.Igst),
                              Cess = g.Sum(x => x.Cess),
                          }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(x => x.Key, x => (x.Quantity, new LineAmounts(x.Taxable, x.Cgst, x.Sgst, x.Igst, x.Cess)));
    }

    private async Task<Built> BuildAsync(Grn grn, PurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        if (grn.Status != GrnStatus.Posted)
        {
            throw AppException.Conflict("purchase_return.grn_not_posted", $"Goods receipt {grn.Number} is not posted, so nothing can be returned against it.");
        }

        if ((request.Reason ?? string.Empty).Trim().Length is < 3 or > 200)
        {
            throw AppException.Validation("purchase_return.reason_required", "Say why the goods go back (3 to 200 characters).");
        }

        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500)
        {
            throw AppException.Validation("purchase_return.lines_required", "Choose 1 to 500 lines to return.");
        }

        if (request.Lines.GroupBy(l => l.GrnLineId).Any(g => g.Count() > 1))
        {
            throw AppException.Validation("purchase_return.duplicate_line", "Each receipt line may appear only once on a return.");
        }

        var grnLines = await db.GrnLines.AsNoTracking().Where(l => l.GrnId == grn.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var returned = await ReturnedAsync(grn.Id, cancellationToken).ConfigureAwait(false);
        var batches = await db.Batches.AsNoTracking().Where(b => grnLines.Select(l => l.VariantId).Contains(b.VariantId))
            .Select(b => new { b.Id, b.VariantId, b.BatchNumber }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<BuiltLine>();
        foreach (var requested in request.Lines)
        {
            var grnLine = grnLines.FirstOrDefault(l => l.Id == requested.GrnLineId)
                ?? throw AppException.Validation("purchase_return.line_not_on_receipt", $"That line is not on goods receipt {grn.Number}.");
            var before = returned.GetValueOrDefault(grnLine.Id, (0m, LineAmounts.Zero));
            LineAmounts amounts;
            try
            {
                amounts = DebitNoteCalculator.Line(new LineAmounts(grnLine.Taxable, grnLine.Cgst, grnLine.Sgst, grnLine.Igst, grnLine.Cess),
                    grnLine.Quantity + grnLine.FreeQuantity, before.Item2, before.Item1, requested.Quantity);
            }
            catch (DomainException e)
            {
                throw AppException.Validation(e.Code, $"{grnLine.Description}: {e.Message}");
            }

            var batchId = grnLine.BatchNumber is { } number ? batches.FirstOrDefault(b => b.VariantId == grnLine.VariantId && b.BatchNumber == number)?.Id : null;
            lines.Add(new BuiltLine(grnLine, requested.Quantity, StockMath.Quantity(requested.Quantity * grnLine.FactorToBase), amounts, batchId,
                before.Item1 + requested.Quantity == grnLine.Quantity + grnLine.FreeQuantity));
        }

        // The return that completes the whole receipt also gives back its round-off.
        var completes = grnLines.All(l => lines.FirstOrDefault(x => x.GrnLine.Id == l.Id)?.Completes
                                          ?? returned.GetValueOrDefault(l.Id).Quantity == l.Quantity + l.FreeQuantity);
        return new Built(lines, completes ? grn.RoundOff : 0);
    }

    private sealed record BuiltLine(GrnLine GrnLine, decimal Quantity, decimal BaseQuantity, LineAmounts Amounts, Guid? BatchId, bool Completes);

    private sealed record Built(List<BuiltLine> Lines, decimal RoundOff);
}
