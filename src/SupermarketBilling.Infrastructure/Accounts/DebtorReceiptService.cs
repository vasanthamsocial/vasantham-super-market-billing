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
/// Money received from debtors: in the office (no shift), or at a counter in the cashier's open shift (cash goes into
/// that drawer). Numbered per store, posted to the debtor's ledger and applied to the invoices named, or the oldest
/// due first; any rest stays as an advance that later credit sales use up.
/// </summary>
public sealed class DebtorReceiptService(
    SupermarketBillingDbContext db,
    PartyLedgerService ledger,
    OrganisationService organisation,
    CounterService counters,
    ShiftService shifts,
    DocumentNumbers numbers,
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
        return await ReceiveAsync(businessId, store, null, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A receipt taken at a counter, in the cashier's open shift.</summary>
    public async Task<DebtorReceiptDto> CreateAtCounterAsync(string? deviceToken, DebtorReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        return await ReceiveAsync(pos.Counter.BusinessId, pos.Store, pos, request, cancellationToken).ConfigureAwait(false);
    }

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

    private async Task<DebtorReceiptDto> ReceiveAsync(Guid businessId, Store store, PosDevice? pos, DebtorReceiptRequest request, CancellationToken cancellationToken)
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

        // At a counter the receipt belongs to the open shift (held FOR SHARE, so the shift cannot close meanwhile).
        DebtorReceipt.AtCounter? atCounter = null;
        if (pos is not null)
        {
            var shift = await shifts.RequireOpenShiftAsync(pos, cancellationToken).ConfigureAwait(false);
            atCounter = new DebtorReceipt.AtCounter(pos.Counter.Id, pos.Device.Id, shift.Id);
        }

        var now = clock.GetUtcNow();
        var date = BusinessCalendar.Today(clock, store.TimeZone);
        var id = Guid.CreateVersion7(now);
        var sequence = await numbers.NextAsync(businessId, store.Id, Series, cancellationToken).ConfigureAwait(false);
        DebtorReceipt receipt;
        try
        {
            receipt = DebtorReceipt.Create(id, businessId, store.Id, debtor.Id, DocumentNumbers.Format(store.Code, Series, sequence), sequence, date, request.Method,
                request.Reference, request.Amount, request.Note, atCounter, currentUser.UserId, key, requestHash, now);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.DebtorReceipts.Add(receipt);
        var reference = receipt.Reference is null ? string.Empty : $" ({receipt.Reference})";
        var entry = await ledger.PostAsync(PartyTypes.Debtor, businessId, debtor.Id,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Receipt, store.Id, receipt.Id, receipt.Number, date, null, -receipt.Amount,
                $"Receipt {receipt.Number}, {receipt.Method.Replace('_', ' ').ToLowerInvariant()}{reference}"),
            currentUser.UserId, now, cancellationToken).ConfigureAwait(false);
        var applied = await ledger.ApplyPaymentAsync(PartyTypes.Debtor, businessId, debtor.Id, entry.Id, request.Allocations, now, cancellationToken).ConfigureAwait(false);
        audit.Record("debtor.paid", "debtor_receipt", receipt.Id, businessId, store.Id, details: new
        {
            receipt.Number, debtor = debtor.Code, receipt.Method, receipt.Reference, receipt.Amount, applied = applied.Sum(a => a.Amount),
            counter = pos?.Counter.Code, shift = atCounter?.ShiftId,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(receipt.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DebtorReceiptDto> GetCoreAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var row = await (from x in db.DebtorReceipts.AsNoTracking()
                         join d in db.Debtors.AsNoTracking() on x.DebtorId equals d.Id
                         join u in db.Users.AsNoTracking() on x.CashierUserId equals u.Id
                         from c in db.Counters.AsNoTracking().Where(c => c.Id == x.CounterId).DefaultIfEmpty()
                         where x.Id == receiptId
                         select new { Receipt = x, Debtor = d.TradeName ?? d.LegalName, ReceivedBy = u.DisplayName, Counter = c == null ? null : c.Code })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var r = row.Receipt;
        var entry = await db.DebtorLedger.AsNoTracking().Where(e => e.DocumentId == receiptId && e.EntryType == LedgerEntryTypes.Receipt)
            .Select(e => new { e.Id, e.BalanceAfter }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var applied = await (from st in db.DebtorSettlements.AsNoTracking()
                             join c in db.DebtorLedger.AsNoTracking() on st.ChargeEntryId equals c.Id
                             where st.PaymentEntryId == entry.Id
                             orderby c.Sequence
                             select new AppliedToDto(c.Id, c.EntryType, c.DocumentNumber, c.EntryDate, st.Amount)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new DebtorReceiptDto(r.Id, r.Number, r.StoreId, r.DebtorId, row.Debtor, r.ReceiptDate, r.Method, r.Reference, r.Amount, r.Note, row.ReceivedBy,
            row.Counter, r.ShiftId, r.CreatedAtUtc, applied, r.Amount - applied.Sum(a => a.Amount), entry.BalanceAfter);
    }
}
