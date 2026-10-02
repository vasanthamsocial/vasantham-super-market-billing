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
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Infrastructure.Accounts;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Sales;

/// <summary>
/// Returns against issued invoices (credit notes). One transaction per return: the original invoice is locked so two
/// returns cannot both take its last items, the amounts come from the original lines, the counter's next credit note
/// number is taken, restockable goods go back into stock at the cost the sale took, and the refund is recorded
/// (paid out, kept as store credit for an exchange, or taken off the debtor's account for an invoice billed to one).
/// </summary>
public sealed class ReturnService(
    SupermarketBillingDbContext db,
    CounterService counters,
    BillingService billing,
    IAccessControl access,
    DocumentNumbers numbers,
    StockEngine stock,
    ShiftService shifts,
    PartyLedgerService ledger,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string LedgerDocumentType = "SALES_RETURN";
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    /// <summary>Finds an invoice of this store by number, with what can still be returned of each line.</summary>
    public async Task<ReturnableInvoiceDto> FindInvoiceAsync(string? deviceToken, string number, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var normalized = (number ?? string.Empty).Trim().ToUpperInvariant();
        var invoiceId = await db.SalesInvoices.AsNoTracking().Where(i => i.BusinessId == pos.Counter.BusinessId && i.Number == normalized && i.StoreId == pos.Store.Id)
            .Select(i => (Guid?)i.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound($"Invoice {normalized} of this store");
        var invoice = await billing.InvoiceAsync(invoiceId, cancellationToken).ConfigureAwait(false);
        var returned = await ReturnedSoFarAsync(invoiceId, cancellationToken).ConfigureAwait(false);
        var lineIds = await db.SalesInvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoiceId).Select(l => new { l.Id, l.LineNumber })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ReturnableInvoiceDto(invoice, invoice.Lines.Select(l =>
        {
            var id = lineIds.First(x => x.LineNumber == l.LineNumber).Id;
            var done = returned.GetValueOrDefault(id);
            return new ReturnableLineDto(id, l.LineNumber, l.Description, l.UnitCode, l.Quantity, l.Quantity - (done?.Quantity ?? 0),
                InvoiceCalculator.Money(l.Total / l.Quantity));
        }).ToList());
    }

    public async Task<ReturnPreviewDto> PreviewAsync(string? deviceToken, ReturnPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var built = await BuildAsync(pos, request.OriginalInvoiceId, request.Lines, cancellationToken).ConfigureAwait(false);
        var canReturn = await CanReturnAsync(pos, cancellationToken).ConfigureAwait(false);
        var (beforeRounding, grand) = Rounded(built.Amounts);
        return new ReturnPreviewDto(built.Lines, built.Amounts.Sum(a => a.Taxable), built.Amounts.Sum(a => a.Cgst), built.Amounts.Sum(a => a.Sgst),
            built.Amounts.Sum(a => a.Igst), built.Amounts.Sum(a => a.Cess), grand - beforeRounding, grand, !canReturn);
    }

    public async Task<CreditNoteDto> IssueAsync(string? deviceToken, IssueReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var businessId = pos.Counter.BusinessId;
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HashJson))));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"return|" + businessId + "|" + request.IdempotencyKey}))", cancellationToken)
            .ConfigureAwait(false);
        var existing = await db.SalesReturns.AsNoTracking().Where(r => r.BusinessId == businessId && r.IdempotencyKey == request.IdempotencyKey)
            .Select(r => new { r.Id, r.RequestHash }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? await CreditNoteAsync(existing.Id, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("idempotency.mismatch", "This return was already recorded with different contents. Start again.");
        }

        var shift = await shifts.RequireOpenShiftAsync(pos, cancellationToken).ConfigureAwait(false);

        // One return at a time per invoice, so two counters cannot both refund its last items.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"return-of|" + request.OriginalInvoiceId}))", cancellationToken).ConfigureAwait(false);
        var built = await BuildAsync(pos, request.OriginalInvoiceId, request.Lines, cancellationToken).ConfigureAwait(false);
        var (beforeRounding, grandTotal) = Rounded(built.Amounts);
        if (grandTotal != request.ExpectedGrandTotal)
        {
            throw AppException.Conflict("return.total_changed",
                $"The refund is Rs. {grandTotal:0.00}, not Rs. {request.ExpectedGrandTotal:0.00}. Check the return and try again.");
        }

        SupervisorApproval? approval = null;
        if (!await CanReturnAsync(pos, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(request.ApprovalToken))
            {
                throw AppException.Forbidden("A return needs a supervisor's approval.");
            }

            var hash = SecretTokens.Hash(request.ApprovalToken);
            approval = (await db.SupervisorApprovals.FromSql($"SELECT * FROM supervisor_approvals WHERE token_hash = {hash} FOR UPDATE")
                    .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            if (approval is null || approval.Kind != SupervisorApprovalKinds.Return)
            {
                throw AppException.Forbidden("The supervisor approval on this return is not valid. Ask the supervisor again.");
            }

            if (grandTotal > approval.MaxAmount)
            {
                throw AppException.Forbidden($"The refund (Rs. {grandTotal:0.00}) is more than the supervisor approved (Rs. {approval.MaxAmount:0.00}).");
            }
        }

        var now = clock.GetUtcNow();
        var businessDate = BusinessCalendar.Today(clock, pos.Store.TimeZone);
        var (_, registrationIndex) = await BillingService.RegistrationInForceAsync(db, businessId, businessDate, cancellationToken).ConfigureAwait(false);
        var prefix = pos.Counter.InvoicePrefix(registrationIndex);
        var sequence = await numbers.NextAsync(businessId, pos.Store.Id, ReturnCalculator.CreditNoteSeries(prefix), cancellationToken).ConfigureAwait(false);
        var returnId = Guid.CreateVersion7(now);
        var invoice = built.Invoice;
        SalesReturn creditNote;
        try
        {
            creditNote = SalesReturn.Issue(returnId, businessId, pos.Store.Id, pos.Counter.Id, pos.Device.Id, shift.Id, prefix, sequence,
                new SalesReturn.Original(invoice.Id, invoice.Number, invoice.BusinessDate, invoice.TaxMode, invoice.IsInterState, invoice.PlaceOfSupplyStateCode),
                businessDate, currentUser.UserId, request.Reason, approval?.Id, built.Amounts,
                (request.Refunds ?? []).Select(r => new PaymentInput(r.Method, r.Amount, r.Reference)).ToList(), request.IdempotencyKey, requestHash, now);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        // Restockable goods go back at the cost the sale took them out at (and into the batch they came from).
        var restocked = built.Items.Where(i => i.Request.Restock).ToList();
        if (restocked.Count > 0)
        {
            await stock.StartAsync(new StockPostingDocument(businessId, LedgerDocumentType, returnId, creditNote.Number, false, businessDate, now), cancellationToken)
                .ConfigureAwait(false);
            await stock.LockAsync(restocked.Select(i => (pos.Store.Id, i.Original.VariantId)), cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in built.Items)
        {
            var cost = 0m;
            if (item.Request.Restock)
            {
                var unitCost = item.Original.BaseQuantity == 0 ? 0 : StockMath.Cost(item.Original.CostOfGoods / item.Original.BaseQuantity);
                var batchId = await db.StockLedger.AsNoTracking()
                    .Where(e => e.DocumentId == invoice.Id && e.VariantId == item.Original.VariantId && e.BatchId != null)
                    .OrderBy(e => e.Quantity).Select(e => e.BatchId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                var batch = batchId is { } b ? await db.Batches.FirstAsync(x => x.Id == b, cancellationToken).ConfigureAwait(false) : null;
                stock.Receive(pos.Store.Id, new StockItem(item.Original.VariantId, item.Original.Description, item.Original.ProductId), item.BaseQuantity, unitCost,
                    MovementTypes.SaleReturn, batch);
                cost = StockMath.Value(item.BaseQuantity, unitCost);
            }

            creditNote.AddLine(SalesReturnLine.Create(businessId, returnId, item.LineNumber, item.Original.Id, item.Original.VariantId, item.Request.Quantity,
                item.BaseQuantity, item.Request.Restock, item.Amounts, cost, now));
        }

        approval?.Use(pos.Counter.Id, currentUser.UserId, returnId, now);
        db.SalesReturns.Add(creditNote);
        stock.Flush();

        // Back to the account: off what the debtor owes, first on the invoice returned against (the account is locked after the stock).
        var toAccount = creditNote.Refunds.Where(r => r.Method == RefundMethods.OnAccount).Sum(r => r.Amount);
        if (toAccount > 0)
        {
            var debtorId = invoice.DebtorId
                ?? throw AppException.Validation("refund.no_account", $"Invoice {invoice.Number} was not billed to a customer account, so nothing can go back to an account.");
            var entry = await ledger.PostAsync(PartyTypes.Debtor, businessId, debtorId,
                new PartyLedgerEntry.Posting(LedgerEntryTypes.CreditNote, pos.Store.Id, returnId, creditNote.Number, businessDate, null, -toAccount,
                    $"Credit note {creditNote.Number} for {invoice.Number}"),
                currentUser.UserId, now, cancellationToken).ConfigureAwait(false);
            var charge = await db.DebtorLedger.AsNoTracking().Where(e => e.DocumentId == invoice.Id && e.EntryType == LedgerEntryTypes.Invoice)
                .Select(e => (Guid?)e.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var (charges, _) = await ledger.OpenItemsAsync(PartyTypes.Debtor, debtorId, cancellationToken).ConfigureAwait(false);
            if (charges.FirstOrDefault(c => c.EntryId == charge) is { } open)
            {
                await ledger.ApplyPaymentAsync(PartyTypes.Debtor, businessId, debtorId, entry.Id,
                    [new SettlementAllocation(open.EntryId, Math.Min(open.Remaining, toAccount))], now, cancellationToken).ConfigureAwait(false);
            }
        }

        audit.Record("sales.return_issued", "sales_return", returnId, businessId, pos.Store.Id, details: new
        {
            creditNote.Number,
            original = invoice.Number,
            counter = pos.Counter.Code,
            creditNote.GrandTotal,
            creditNote.StoreCredit,
            creditNote.Reason,
            lines = built.Items.Select(i => new { line = i.Original.LineNumber, i.Request.Quantity, i.Request.Restock }),
            refunds = creditNote.Refunds.Select(r => new { r.Method, r.Amount }),
            approval = approval?.Id,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await CreditNoteAsync(returnId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A credit note of this device's store (counter reprint, or checking store credit before an exchange).</summary>
    public async Task<CreditNoteDto> CounterCreditNoteAsync(string? deviceToken, string number, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var normalized = (number ?? string.Empty).Trim().ToUpperInvariant();
        var id = await db.SalesReturns.AsNoTracking().Where(r => r.BusinessId == pos.Counter.BusinessId && r.Number == normalized)
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound($"Credit note {normalized}");
        return await CreditNoteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CreditNoteSummaryDto>> ListAsync(Guid businessId, Guid storeId, DateOnly? date, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(Permissions.SalesView, businessId, storeId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden();
        }

        var query = db.SalesReturns.AsNoTracking().Where(r => r.StoreId == storeId);
        if (date is { } d)
        {
            query = query.Where(r => r.BusinessDate == d);
        }

        var rows = await (
                from r in query
                join c in db.Counters.AsNoTracking() on r.CounterId equals c.Id
                join u in db.Users.AsNoTracking() on r.CashierUserId equals u.Id
                orderby r.IssuedAtUtc descending
                select new { Return = r, c.Code, u.DisplayName, Redeemed = db.CreditNoteRedemptions.Where(x => x.ReturnId == r.Id).Sum(x => (decimal?)x.Amount) ?? 0 })
            .Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(x => new CreditNoteSummaryDto(x.Return.Id, x.Return.Number, x.Return.OriginalInvoiceNumber, x.Return.BusinessDate, x.Return.IssuedAtUtc,
            x.Code, x.DisplayName, x.Return.GrandTotal, x.Return.StoreCredit - x.Redeemed)).ToList();
    }

    public async Task<CreditNoteDto> GetAsync(Guid businessId, Guid returnId, CancellationToken cancellationToken)
    {
        var storeId = await db.SalesReturns.AsNoTracking().Where(r => r.Id == returnId && r.BusinessId == businessId).Select(r => (Guid?)r.StoreId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Credit note");
        if (!await access.HasPermissionAsync(Permissions.SalesView, businessId, storeId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden();
        }

        return await CreditNoteAsync(returnId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(byte[] Content, string FileName)> PdfAsync(Guid businessId, Guid returnId, CancellationToken cancellationToken)
    {
        var note = await GetAsync(businessId, returnId, cancellationToken).ConfigureAwait(false);
        var timeZone = await db.Stores.AsNoTracking().Where(s => s.Id == note.StoreId).Select(s => s.TimeZone).FirstAsync(cancellationToken).ConfigureAwait(false);
        return (Documents.CreditNotePdf.Render(note, timeZone), note.Number.Replace('/', '-') + ".pdf");
    }

    internal async Task<CreditNoteDto> CreditNoteAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var row = await (
                from r in db.SalesReturns.AsNoTracking().Include(x => x.Lines).Include(x => x.Refunds)
                join i in db.SalesInvoices.AsNoTracking() on r.OriginalInvoiceId equals i.Id
                join c in db.Counters.AsNoTracking() on r.CounterId equals c.Id
                join u in db.Users.AsNoTracking() on r.CashierUserId equals u.Id
                where r.Id == returnId
                select new { Return = r, Invoice = i, CounterCode = c.Code, Cashier = u.DisplayName })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var originals = await db.SalesInvoiceLines.AsNoTracking().Where(l => l.InvoiceId == row.Invoice.Id).ToDictionaryAsync(l => l.Id, cancellationToken).ConfigureAwait(false);
        var redeemed = await db.CreditNoteRedemptions.AsNoTracking().Where(x => x.ReturnId == returnId).SumAsync(x => (decimal?)x.Amount, cancellationToken).ConfigureAwait(false) ?? 0;
        var ret = row.Return;
        var inv = row.Invoice;
        return new CreditNoteDto(
            ret.Id, ret.Number, inv.Id, ret.OriginalInvoiceNumber, ret.OriginalInvoiceDate, ret.TaxMode, ret.BusinessDate, ret.IssuedAtUtc, ret.StoreId, row.CounterCode, row.Cashier,
            ret.Reason, inv.SellerName, inv.SellerGstin, inv.SellerAddress, inv.SellerStateCode, inv.BuyerName, inv.BuyerGstin, ret.PlaceOfSupplyStateCode, ret.IsInterState,
            ret.Lines.OrderBy(l => l.LineNumber).Select(l => ToDto(l.LineNumber, originals[l.OriginalLineId], l.Quantity, l.Restocked,
                new BillLineResult(l.Gross, l.ItemDiscount, l.BillDiscount, l.Taxable, l.Cgst, l.Sgst, l.Igst, l.Cess, l.Total))).ToList(),
            ret.TaxableTotal, ret.CgstTotal, ret.SgstTotal, ret.IgstTotal, ret.CessTotal, ret.RoundOff, ret.GrandTotal,
            ret.Refunds.OrderBy(x => x.RefundOrder).Select(x => new InvoicePaymentDto(x.Method, x.Amount, x.Reference)).ToList(), ret.StoreCredit, ret.StoreCredit - redeemed);
    }

    private async Task<Built> BuildAsync(PosDevice pos, Guid invoiceId, IReadOnlyList<ReturnLineRequest>? requests, CancellationToken cancellationToken)
    {
        if (requests is null || requests.Count == 0)
        {
            throw AppException.Validation("return.lines_required", "Choose at least one item to return.");
        }

        if (requests.GroupBy(r => r.OriginalLineId).Any(g => g.Count() > 1))
        {
            throw AppException.Validation("return.duplicate_line", "Each invoice line may appear only once in a return.");
        }

        var invoice = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.BusinessId == pos.Counter.BusinessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Invoice");
        if (invoice.StoreId != pos.Store.Id)
        {
            throw AppException.Validation("return.other_store", $"Invoice {invoice.Number} was issued by another store; return it there.");
        }

        var returned = await ReturnedSoFarAsync(invoiceId, cancellationToken).ConfigureAwait(false);
        var items = new List<BuiltItem>();
        foreach (var request in requests)
        {
            var original = invoice.Lines.FirstOrDefault(l => l.Id == request.OriginalLineId) ?? throw AppException.NotFound("Invoice line");
            var done = returned.GetValueOrDefault(original.Id);
            BillLineResult amounts;
            try
            {
                amounts = ReturnCalculator.Line(new ReturnableLine(original.Quantity, Amounts(original), done?.Amounts ?? Zero, done?.Quantity ?? 0), request.Quantity);
            }
            catch (DomainException e)
            {
                throw AppException.Validation(e.Code, $"{original.Description}: {e.Message}");
            }

            var baseQuantity = original.BaseQuantity * request.Quantity / original.Quantity;
            if (StockMath.Quantity(baseQuantity) != baseQuantity)
            {
                throw AppException.Validation("return.quantity_precision", $"{original.Description}: that quantity cannot be returned exactly; return whole packs.");
            }

            items.Add(new BuiltItem(items.Count + 1, request, original, baseQuantity, amounts));
        }

        return new Built(invoice, items, items.Select(i => i.Amounts).ToList(),
            items.Select(i => ToDto(i.LineNumber, i.Original, i.Request.Quantity, i.Request.Restock, i.Amounts)).ToList());
    }

    private async Task<Dictionary<Guid, Returned>> ReturnedSoFarAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var rows = await (
                from l in db.SalesReturnLines.AsNoTracking()
                join r in db.SalesReturns.AsNoTracking() on l.ReturnId equals r.Id
                where r.OriginalInvoiceId == invoiceId
                select l)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(l => l.OriginalLineId).ToDictionary(g => g.Key, g => new Returned(
            g.Sum(l => l.Quantity),
            new BillLineResult(g.Sum(l => l.Gross), g.Sum(l => l.ItemDiscount), g.Sum(l => l.BillDiscount), g.Sum(l => l.Taxable), g.Sum(l => l.Cgst),
                g.Sum(l => l.Sgst), g.Sum(l => l.Igst), g.Sum(l => l.Cess), g.Sum(l => l.Total))));
    }

    private Task<bool> CanReturnAsync(PosDevice pos, CancellationToken cancellationToken) =>
        access.HasPermissionAsync(Permissions.PosReturn, pos.Counter.BusinessId, pos.Counter.StoreId, cancellationToken);

    private static (decimal BeforeRounding, decimal GrandTotal) Rounded(IReadOnlyList<BillLineResult> amounts)
    {
        var before = amounts.Sum(a => a.Total);
        return (before, decimal.Round(before, 0, MidpointRounding.AwayFromZero));
    }

    private static readonly BillLineResult Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static BillLineResult Amounts(SalesInvoiceLine l) => new(l.Gross, l.ItemDiscount, l.BillDiscount, l.Taxable, l.Cgst, l.Sgst, l.Igst, l.Cess, l.Total);

    private static CreditNoteLineDto ToDto(int lineNumber, SalesInvoiceLine original, decimal quantity, bool restocked, BillLineResult a) =>
        new(lineNumber, original.Id, original.Description, original.HsnSac, original.UnitCode, quantity, restocked, original.GstRatePercent, a.Taxable, a.Cgst, a.Sgst,
            a.Igst, a.Cess, a.Total);

    private sealed record Returned(decimal Quantity, BillLineResult Amounts);

    private sealed record BuiltItem(int LineNumber, ReturnLineRequest Request, SalesInvoiceLine Original, decimal BaseQuantity, BillLineResult Amounts);

    private sealed record Built(SalesInvoice Invoice, List<BuiltItem> Items, List<BillLineResult> Amounts, List<CreditNoteLineDto> Lines);
}
