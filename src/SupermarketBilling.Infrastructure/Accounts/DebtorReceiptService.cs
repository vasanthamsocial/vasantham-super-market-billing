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
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Sales;

namespace SupermarketBilling.Infrastructure.Accounts;

/// <summary>
/// Money received from debtors: in the office (no shift), at a counter in the cashier's open shift (cash goes into that
/// drawer), or in the field in the collector's open round. Numbered per store, posted to the debtor's ledger and applied
/// to the invoices named, or the oldest due first; any rest stays as an advance. Cheques and drafts enter the cheque
/// register. A posted receipt is never changed: it can only be reversed.
/// </summary>
public sealed class DebtorReceiptService(
    SupermarketBillingDbContext db,
    PartyLedgerService ledger,
    OrganisationService organisation,
    CounterService counters,
    ShiftService shifts,
    DocumentNumbers numbers,
    Messaging.MessageOutbox outbox,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string Series = "RCT";
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    /// <summary>A receipt taken in the office (needs <c>receivables.manage</c> in the store).</summary>
    public async Task<DebtorReceiptDto> CreateAsync(Guid businessId, DebtorReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var storeId = request.StoreId ?? throw AppException.Validation("receipt.store_required", "Choose the store the money was received in.");
        await organisation.RequireAsync(Permissions.ReceivablesManage, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId && s.BusinessId == businessId && s.IsActive, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Store");
        return await ReceiveAsync(businessId, store, new Place(null, null), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A receipt taken at a counter, in the cashier's open shift.</summary>
    public async Task<DebtorReceiptDto> CreateAtCounterAsync(string? deviceToken, DebtorReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        return await ReceiveAsync(pos.Counter.BusinessId, pos.Store, new Place(pos, null), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A receipt taken in the field, in the collector's open round (the caller has checked the collector may collect from the debtor).</summary>
    internal async Task<DebtorReceiptDto> CreateInFieldAsync(Guid businessId, Store store, Guid sessionId, DebtorReceiptRequest request, CancellationToken cancellationToken) =>
        await ReceiveAsync(businessId, store, new Place(null, sessionId), request, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DebtorReceiptDto>> ListAsync(Guid businessId, Guid? storeId, Guid? debtorId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.DebtorsView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.DebtorReceipts.AsNoTracking().Where(r => r.BusinessId == businessId);
        query = storeId is { } s ? query.Where(r => r.StoreId == s) : query;
        query = debtorId is { } d ? query.Where(r => r.DebtorId == d) : query;
        var ids = await query.OrderByDescending(r => r.CreatedAtUtc).Take(200).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DebtorReceiptDto>();
        foreach (var id in ids)
        {
            result.Add(await GetCoreAsync(id, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    private sealed record Place(PosDevice? Counter, Guid? SessionId);

    private async Task<DebtorReceiptDto> ReceiveAsync(Guid businessId, Store store, Place place, DebtorReceiptRequest request, CancellationToken cancellationToken)
    {
        var key = request.IdempotencyKey ?? throw AppException.Validation("idempotency.key_required", "Each receipt needs an idempotency key.");
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request with { StoreId = store.Id }, HashJson))));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"debtor-receipt|" + businessId + "|" + key}))", cancellationToken).ConfigureAwait(false);
        var existing = await db.DebtorReceipts.AsNoTracking().Where(r => r.BusinessId == businessId && r.IdempotencyKey == key)
            .Select(r => new { r.Id, r.RequestHash }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? await GetCoreAsync(existing.Id, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("idempotency.mismatch", "This receipt was already recorded with different details.");
        }

        var debtor = await db.Debtors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.DebtorId && d.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Debtor");

        // At a counter the receipt belongs to the open shift; in the field to the collector's open round. Both are held
        // FOR SHARE, so the shift cannot close and the round cannot be handed over while the receipt is being saved.
        DebtorReceipt.AtCounter? atCounter = null;
        if (place.Counter is { } pos)
        {
            var shift = await shifts.RequireOpenShiftAsync(pos, cancellationToken).ConfigureAwait(false);
            atCounter = new DebtorReceipt.AtCounter(pos.Counter.Id, pos.Device.Id, shift.Id);
        }

        if (place.SessionId is { } sessionId)
        {
            var open = await db.CollectorSessions.FromSql($"SELECT *, xmin FROM collector_sessions WHERE id = {sessionId} FOR SHARE").AsNoTracking()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (open.SingleOrDefault() is not { Status: CollectorSessionStatus.Open } round || round.CollectorUserId != currentUser.UserId)
            {
                throw AppException.Conflict("session.not_open", "Your collection round is not open (it may have been handed over). Start a new round.");
            }
        }

        var now = clock.GetUtcNow();
        var date = BusinessCalendar.Today(clock, store.TimeZone);
        var id = Guid.CreateVersion7(now);
        var sequence = await numbers.NextAsync(businessId, store.Id, Series, cancellationToken).ConfigureAwait(false);
        DebtorReceipt receipt;
        Cheque? cheque = null;
        try
        {
            receipt = DebtorReceipt.Create(id, businessId, store.Id, debtor.Id, DocumentNumbers.Format(store.Code, Series, sequence), sequence, date, request.Method,
                request.Reference, request.Amount, request.Note, atCounter, currentUser.UserId, key, requestHash, now, place.SessionId);
            if (ReceiptMethods.IsInstrument(receipt.Method))
            {
                cheque = Cheque.Receive(businessId, receipt, request.BankName, request.ChequeDate, now);
            }
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.DebtorReceipts.Add(receipt);
        if (cheque is not null)
        {
            db.Cheques.Add(cheque);
            db.ChequeEvents.Add(ChequeEvent.Record(businessId, cheque.Id, ChequeStatus.Received, date, null, currentUser.UserId, now));
        }

        var reference = receipt.Reference is null ? string.Empty : $" ({receipt.Reference})";
        var entry = await ledger.PostAsync(PartyTypes.Debtor, businessId, debtor.Id,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Receipt, store.Id, receipt.Id, receipt.Number, date, null, -receipt.Amount,
                $"Receipt {receipt.Number}, {receipt.Method.Replace('_', ' ').ToLowerInvariant()}{reference}"),
            currentUser.UserId, now, cancellationToken).ConfigureAwait(false);
        var applied = await ledger.ApplyPaymentAsync(PartyTypes.Debtor, businessId, debtor.Id, entry.Id, request.Allocations, now, cancellationToken).ConfigureAwait(false);

        // Confirmation with the balance the server now holds, sent after this commits; a messaging failure never touches the receipt.
        await outbox.QueueAsync(businessId, debtor, Domain.Messaging.MessageKinds.Receipt, receipt.Id, receipt.Number, new Dictionary<string, string>
        {
            ["party"] = debtor.DisplayName,
            ["receipt_number"] = receipt.Number,
            ["amount"] = Domain.Messaging.MessageFormat.Money(receipt.Amount),
            ["method"] = receipt.Method.Replace('_', ' ').ToLowerInvariant() + reference,
            ["previous_balance"] = Domain.Messaging.MessageFormat.Money(entry.BalanceAfter + receipt.Amount),
            ["current_balance"] = Domain.Messaging.MessageFormat.Money(entry.BalanceAfter),
        }, cancellationToken).ConfigureAwait(false);
        audit.Record("debtor.paid", "debtor_receipt", receipt.Id, businessId, store.Id, details: new
        {
            receipt.Number, debtor = debtor.Code, receipt.Method, receipt.Reference, receipt.Amount, applied = applied.Sum(a => a.Amount),
            counter = place.Counter?.Counter.Code, shift = atCounter?.ShiftId, session = place.SessionId,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(receipt.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverses a receipt inside the caller's transaction (bounced or cancelled cheque, or an approved correction): the
    /// bills it paid are unpaid again and the debtor owes the amount again. A receipt is reversed at most once.
    /// </summary>
    internal async Task ReverseCoreAsync(Guid businessId, Guid receiptId, string kind, string reason, Guid? approvalId, Guid by, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var receipt = await db.DebtorReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId && r.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Receipt");
        await ledger.LockAsync(PartyTypes.Debtor, receipt.DebtorId, cancellationToken).ConfigureAwait(false);
        if (await db.ReceiptReversals.AnyAsync(r => r.ReceiptId == receiptId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("reversal.exists", $"Receipt {receipt.Number} is already reversed.");
        }

        ReceiptReversal reversal;
        try
        {
            reversal = ReceiptReversal.Record(businessId, receiptId, kind, reason, approvalId, by, now);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        var entryId = await db.DebtorLedger.AsNoTracking().Where(e => e.DocumentId == receiptId && e.EntryType == LedgerEntryTypes.Receipt).Select(e => e.Id)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var today = BusinessCalendar.Today(clock);
        var what = kind switch { ReversalKinds.Bounced => "bounced", ReversalKinds.Cancelled => "cancelled", _ => "reversed" };
        var narration = $"Receipt {receipt.Number} {what}: {reversal.Reason}";
        await ledger.ReverseDebtorPaymentAsync(businessId, receipt.DebtorId, entryId, receipt.Amount,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.ReceiptReversal, receipt.StoreId, receiptId, receipt.Number, today, today, receipt.Amount,
                narration.Length <= 300 ? narration : narration[..300]),
            by, now, cancellationToken).ConfigureAwait(false);
        db.ReceiptReversals.Add(reversal);
        audit.Record("debtor.receipt_reversed", "debtor_receipt", receiptId, businessId, receipt.StoreId, details: new
        {
            receipt.Number, receipt.Amount, kind, reversal.Reason, approval = approvalId,
        });
    }

    internal async Task<DebtorReceiptDto> GetCoreAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var row = await (from x in db.DebtorReceipts.AsNoTracking()
                         join d in db.Debtors.AsNoTracking() on x.DebtorId equals d.Id
                         join u in db.Users.AsNoTracking() on x.CashierUserId equals u.Id
                         from c in db.Counters.AsNoTracking().Where(c => c.Id == x.CounterId).DefaultIfEmpty()
                         from q in db.Cheques.AsNoTracking().Where(q => q.ReceiptId == x.Id).DefaultIfEmpty()
                         from v in db.ReceiptReversals.AsNoTracking().Where(v => v.ReceiptId == x.Id).DefaultIfEmpty()
                         where x.Id == receiptId
                         select new
                         {
                             Receipt = x, Debtor = d.TradeName ?? d.LegalName, ReceivedBy = u.DisplayName, Counter = c == null ? null : c.Code,
                             ChequeStatus = q == null ? null : q.Status, ReversalKind = v == null ? null : v.Kind, ReversalReason = v == null ? null : v.Reason,
                         })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var r = row.Receipt;
        var entry = await db.DebtorLedger.AsNoTracking().Where(e => e.DocumentId == receiptId && e.EntryType == LedgerEntryTypes.Receipt)
            .Select(e => new { e.Id, e.BalanceAfter }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var applied = await (from st in db.DebtorSettlements.AsNoTracking()
                             join c in db.DebtorLedger.AsNoTracking() on st.ChargeEntryId equals c.Id
                             where st.PaymentEntryId == entry.Id && c.EntryType != LedgerEntryTypes.ReceiptReversal
                             group st.Amount by new { c.Id, c.EntryType, c.DocumentNumber, c.EntryDate, c.Sequence } into g
                             orderby g.Key.Sequence
                             select new { g.Key.Id, g.Key.EntryType, g.Key.DocumentNumber, g.Key.EntryDate, Amount = g.Sum() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var appliedTo = applied.Where(a => a.Amount > 0).Select(a => new AppliedToDto(a.Id, a.EntryType, a.DocumentNumber, a.EntryDate, a.Amount)).ToList();
        var unapplied = row.ReversalKind is null ? r.Amount - appliedTo.Sum(a => a.Amount) : 0;
        return new DebtorReceiptDto(r.Id, r.Number, r.StoreId, r.DebtorId, row.Debtor, r.ReceiptDate, r.Method, r.Reference, r.Amount, r.Note, row.ReceivedBy,
            row.Counter, r.ShiftId, r.CreatedAtUtc, appliedTo, unapplied, entry.BalanceAfter, r.CollectorSessionId, row.ChequeStatus, row.ReversalKind, row.ReversalReason);
    }
}
