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
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Accounts;

/// <summary>
/// Payments to suppliers: numbered per store, posted to the supplier's ledger and applied to the bills named (or the
/// oldest due first). Whatever is not applied stays on the account as an advance.
/// </summary>
public sealed class SupplierPaymentService(
    SupermarketBillingDbContext db,
    PartyLedgerService ledger,
    OrganisationService organisation,
    DocumentNumbers numbers,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string Series = "PMT";
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    public async Task<SupplierPaymentDto> CreateAsync(Guid businessId, SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.PayablesManage, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        var key = request.IdempotencyKey ?? throw AppException.Validation("idempotency.key_required", "Each payment needs an idempotency key.");
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HashJson))));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"supplier-payment|" + businessId + "|" + key}))", cancellationToken).ConfigureAwait(false);
        var existing = await db.SupplierPayments.AsNoTracking().Where(p => p.BusinessId == businessId && p.IdempotencyKey == key)
            .Select(p => new { p.Id, p.RequestHash }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? await GetCoreAsync(existing.Id, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("idempotency.mismatch", "This payment was already recorded with different details.");
        }

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StoreId && s.BusinessId == businessId && s.IsActive, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Store");
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.SupplierId && s.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Supplier");

        var now = clock.GetUtcNow();
        var date = BusinessCalendar.Today(clock, store.TimeZone);
        var id = Guid.CreateVersion7(now);
        await ledger.LockAsync(PartyTypes.Supplier, supplier.Id, cancellationToken).ConfigureAwait(false);
        var sequence = await numbers.NextAsync(businessId, store.Id, Series, cancellationToken).ConfigureAwait(false);
        SupplierPayment payment;
        try
        {
            payment = SupplierPayment.Create(id, businessId, store.Id, supplier.Id, DocumentNumbers.Format(store.Code, Series, sequence), sequence, date, request.Method,
                request.Reference, request.Amount, request.Note, currentUser.UserId, key, requestHash, now);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.SupplierPayments.Add(payment);
        var reference = payment.Reference is null ? string.Empty : $" ({payment.Reference})";
        var entry = await ledger.PostAsync(PartyTypes.Supplier, businessId, supplier.Id,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Payment, store.Id, payment.Id, payment.Number, date, null, -payment.Amount,
                $"Payment {payment.Number}, {payment.Method.Replace('_', ' ').ToLowerInvariant()}{reference}"),
            currentUser.UserId, now, cancellationToken).ConfigureAwait(false);
        var applied = await ledger.ApplyPaymentAsync(PartyTypes.Supplier, businessId, supplier.Id, entry.Id, request.Allocations, now, cancellationToken).ConfigureAwait(false);
        audit.Record("supplier.paid", "supplier_payment", payment.Id, businessId, store.Id, details: new
        {
            payment.Number, supplier = supplier.Code, payment.Method, payment.Reference, payment.Amount, applied = applied.Sum(a => a.Amount),
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(payment.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SupplierPaymentDto>> ListAsync(Guid businessId, Guid? storeId, Guid? supplierId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.SupplierPayments.AsNoTracking().Where(p => p.BusinessId == businessId);
        query = storeId is { } s ? query.Where(p => p.StoreId == s) : query;
        query = supplierId is { } sup ? query.Where(p => p.SupplierId == sup) : query;
        var ids = await query.OrderByDescending(p => p.CreatedAtUtc).Take(200).Select(p => p.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SupplierPaymentDto>();
        foreach (var id in ids)
        {
            result.Add(await GetCoreAsync(id, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<SupplierPaymentDto> GetAsync(Guid businessId, Guid paymentId, CancellationToken cancellationToken)
    {
        var storeId = await db.SupplierPayments.AsNoTracking().Where(p => p.Id == paymentId && p.BusinessId == businessId).Select(p => (Guid?)p.StoreId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Payment");
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(paymentId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SupplierPaymentDto> GetCoreAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var row = await (from x in db.SupplierPayments.AsNoTracking()
                         join s in db.Suppliers.AsNoTracking() on x.SupplierId equals s.Id
                         join u in db.Users.AsNoTracking() on x.PaidByUserId equals u.Id
                         where x.Id == paymentId
                         select new { Payment = x, Supplier = s.Name, PaidBy = u.DisplayName }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var p = row.Payment;
        var entryId = await db.SupplierLedger.AsNoTracking().Where(e => e.DocumentId == paymentId && e.EntryType == LedgerEntryTypes.Payment).Select(e => e.Id)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var applied = await (from st in db.SupplierSettlements.AsNoTracking()
                             join c in db.SupplierLedger.AsNoTracking() on st.ChargeEntryId equals c.Id
                             where st.PaymentEntryId == entryId
                             orderby c.Sequence
                             select new AppliedToDto(c.Id, c.EntryType, c.DocumentNumber, c.EntryDate, st.Amount)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new SupplierPaymentDto(p.Id, p.Number, p.StoreId, p.SupplierId, row.Supplier, p.PaymentDate, p.Method, p.Reference, p.Amount, p.Note, row.PaidBy,
            p.CreatedAtUtc, applied, p.Amount - applied.Sum(a => a.Amount));
    }
}
