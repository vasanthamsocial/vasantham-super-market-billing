using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Accounts;

/// <summary>
/// Posts to supplier and debtor ledgers and applies payments to charges. Every posting first locks the account's row
/// (suppliers or debtors, FOR UPDATE), so entries of one account are numbered and balanced one after another; the
/// database re-checks the chain. Runs inside the caller's transaction; entries and settlements made in the same unit
/// of work are taken into account before they are saved.
/// </summary>
public sealed class PartyLedgerService(SupermarketBillingDbContext db)
{
    private readonly HashSet<(string, Guid)> locked = [];
    private readonly Dictionary<(string, Guid), (long Sequence, decimal Balance)> positions = [];
    private readonly List<PartyLedgerEntry> posted = [];
    private readonly List<PartySettlement> settled = [];

    public async Task LockAsync(string partyType, Guid partyId, CancellationToken cancellationToken)
    {
        if (!locked.Add((partyType, partyId)))
        {
            return;
        }

        // Not composed further, so the row lock is taken by exactly this statement.
        var found = partyType switch
        {
            PartyTypes.Supplier => (await db.Suppliers.FromSql($"SELECT *, xmin FROM suppliers WHERE id = {partyId} FOR UPDATE").AsNoTracking()
                .ToListAsync(cancellationToken).ConfigureAwait(false)).Count,
            PartyTypes.Debtor => (await db.Debtors.FromSql($"SELECT *, xmin FROM debtors WHERE id = {partyId} FOR UPDATE").AsNoTracking()
                .ToListAsync(cancellationToken).ConfigureAwait(false)).Count,
            _ => throw new ArgumentOutOfRangeException(nameof(partyType)),
        };
        if (found == 0)
        {
            locked.Remove((partyType, partyId));
            throw AppException.NotFound(partyType == PartyTypes.Supplier ? "Supplier" : "Debtor");
        }
    }

    /// <summary>Adds an entry to the account (locking it first), with the next number and running balance.</summary>
    public async Task<PartyLedgerEntry> PostAsync(string partyType, Guid businessId, Guid partyId, PartyLedgerEntry.Posting posting, Guid createdBy, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await LockAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        var key = (partyType, partyId);
        if (!positions.TryGetValue(key, out var position))
        {
            var last = await Entries(partyType).Where(e => e.PartyId == partyId).OrderByDescending(e => e.Sequence)
                .Select(e => new { e.Sequence, e.BalanceAfter }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            position = (last?.Sequence ?? 0, last?.BalanceAfter ?? 0);
        }

        PartyLedgerEntry entry;
        try
        {
            if (partyType == PartyTypes.Supplier)
            {
                var supplierEntry = SupplierLedgerEntry.Create(businessId, partyId, position.Sequence, position.Balance, posting, createdBy, now);
                db.SupplierLedger.Add(supplierEntry);
                entry = supplierEntry;
            }
            else
            {
                var debtorEntry = DebtorLedgerEntry.Create(businessId, partyId, position.Sequence, position.Balance, posting, createdBy, now);
                db.DebtorLedger.Add(debtorEntry);
                entry = debtorEntry;
            }
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        positions[key] = (entry.Sequence, entry.BalanceAfter);
        posted.Add(entry);
        return entry;
    }

    /// <summary>Whether the account has any entry yet (an opening balance can only be the first).</summary>
    public async Task<bool> HasEntriesAsync(string partyType, Guid partyId, CancellationToken cancellationToken) =>
        posted.Any(e => e.PartyId == partyId) || await Entries(partyType).AnyAsync(e => e.PartyId == partyId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Applies an unapplied payment (or credit) to the account's open charges: the ones named, or the oldest due
    /// first. Returns what was applied.
    /// </summary>
    public async Task<IReadOnlyList<(Guid ChargeEntryId, decimal Amount)>> ApplyPaymentAsync(
        string partyType, Guid businessId, Guid partyId, Guid paymentEntryId, IReadOnlyList<SettlementAllocation>? chosen, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await LockAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        var (charges, payments) = await OpenItemsAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        var payment = payments.FirstOrDefault(p => p.EntryId == paymentEntryId)
            ?? throw AppException.Validation("settlement.nothing_to_apply", "That payment is already fully applied.");
        IReadOnlyList<(Guid, decimal)> plan;
        try
        {
            plan = SettlementPlanner.Plan(charges, payment.Remaining, chosen?.Select(c => (c.ChargeEntryId, c.Amount)).ToList());
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        foreach (var (chargeId, amount) in plan)
        {
            AddSettlement(partyType, businessId, partyId, chargeId, paymentEntryId, amount, now);
        }

        return plan;
    }

    /// <summary>Uses any unapplied payments of the account against its open charges, oldest first on both sides.</summary>
    public async Task<decimal> ApplyUnappliedAsync(string partyType, Guid businessId, Guid partyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LockAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        var (charges, payments) = await OpenItemsAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        var remaining = charges.ToDictionary(c => c.EntryId, c => c.Remaining);
        var total = 0m;
        foreach (var payment in payments.OrderBy(p => p.Sequence))
        {
            var open = charges.Where(c => remaining[c.EntryId] > 0).Select(c => c with { Remaining = remaining[c.EntryId] }).ToList();
            foreach (var (chargeId, amount) in SettlementPlanner.Plan(open, payment.Remaining))
            {
                AddSettlement(partyType, businessId, partyId, chargeId, payment.EntryId, amount, now);
                remaining[chargeId] -= amount;
                total += amount;
            }
        }

        return total;
    }

    /// <summary>
    /// Reverses a debtor receipt: what it settled is taken back (those bills are unpaid again), a RECEIPT_REVERSAL entry
    /// puts the amount back on the account, and that entry is settled against the receipt so neither stays open.
    /// </summary>
    public async Task<PartyLedgerEntry> ReverseDebtorPaymentAsync(
        Guid businessId, Guid debtorId, Guid receiptEntryId, decimal amount, PartyLedgerEntry.Posting reversal, Guid createdBy, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await LockAsync(PartyTypes.Debtor, debtorId, cancellationToken).ConfigureAwait(false);
        var saved = await db.DebtorSettlements.AsNoTracking().Where(s => s.PaymentEntryId == receiptEntryId)
            .Select(s => new { s.Id, s.ChargeEntryId, s.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byCharge = saved.Where(s => settled.All(l => l.Id != s.Id))
            .Concat(settled.Where(s => s.PaymentEntryId == receiptEntryId).Select(s => new { s.Id, s.ChargeEntryId, s.Amount }))
            .GroupBy(s => s.ChargeEntryId).Select(g => (Charge: g.Key, Net: g.Sum(s => s.Amount))).Where(x => x.Net > 0).ToList();
        foreach (var (charge, net) in byCharge)
        {
            var undo = DebtorSettlement.Undo(businessId, debtorId, charge, receiptEntryId, net, now);
            db.DebtorSettlements.Add(undo);
            settled.Add(undo);
        }

        var entry = await PostAsync(PartyTypes.Debtor, businessId, debtorId, reversal with { Amount = amount }, createdBy, now, cancellationToken).ConfigureAwait(false);
        AddSettlement(PartyTypes.Debtor, businessId, debtorId, entry.Id, receiptEntryId, amount, now);
        return entry;
    }

    /// <summary>The account's open charges and unapplied payments, including what this unit of work added.</summary>
    public async Task<(List<OpenItem> Charges, List<OpenItem> Payments)> OpenItemsAsync(string partyType, Guid partyId, CancellationToken cancellationToken)
    {
        var all = await AllWithRemainingAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        return (all.Where(x => x.Entry.Amount > 0 && x.Remaining > 0).Select(x => Open(x.Entry, x.Remaining)).ToList(),
            all.Where(x => x.Entry.Amount < 0 && x.Remaining > 0).Select(x => Open(x.Entry, x.Remaining)).ToList());
    }

    /// <summary>Every entry of the account with what is left of it: unpaid (charges) or unapplied (payments).</summary>
    internal async Task<List<(PartyLedgerEntry Entry, decimal Remaining)>> AllWithRemainingAsync(string partyType, Guid partyId, CancellationToken cancellationToken)
    {
        var saved = await Entries(partyType).AsNoTracking().Where(e => e.PartyId == partyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var entries = saved.Where(e => posted.All(p => p.Id != e.Id)).Concat(posted.Where(p => p.PartyId == partyId)).OrderBy(e => e.Sequence).ToList();
        var settlements = await Settlements(partyType).AsNoTracking().Where(s => s.PartyId == partyId)
            .Select(s => new { s.Id, s.ChargeEntryId, s.PaymentEntryId, s.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var used = settlements.Where(s => settled.All(l => l.Id != s.Id))
            .Concat(settled.Where(s => s.PartyId == partyId).Select(s => new { s.Id, s.ChargeEntryId, s.PaymentEntryId, s.Amount }))
            .ToList();
        var byCharge = used.GroupBy(s => s.ChargeEntryId).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
        var byPayment = used.GroupBy(s => s.PaymentEntryId).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
        return entries.Select(e => (e, e.Amount > 0 ? e.Amount - byCharge.GetValueOrDefault(e.Id) : -e.Amount - byPayment.GetValueOrDefault(e.Id))).ToList();
    }

    internal IQueryable<PartyLedgerEntry> Entries(string partyType) => partyType switch
    {
        PartyTypes.Supplier => db.SupplierLedger,
        PartyTypes.Debtor => db.DebtorLedger,
        _ => throw new ArgumentOutOfRangeException(nameof(partyType)),
    };

    internal IQueryable<PartySettlement> Settlements(string partyType) => partyType switch
    {
        PartyTypes.Supplier => db.SupplierSettlements,
        PartyTypes.Debtor => db.DebtorSettlements,
        _ => throw new ArgumentOutOfRangeException(nameof(partyType)),
    };

    private static OpenItem Open(PartyLedgerEntry e, decimal remaining) => new(e.Id, e.EntryDate, e.DueDate, e.Sequence, remaining);

    private void AddSettlement(string partyType, Guid businessId, Guid partyId, Guid chargeId, Guid paymentId, decimal amount, DateTimeOffset now)
    {
        PartySettlement settlement;
        if (partyType == PartyTypes.Supplier)
        {
            var s = SupplierSettlement.Create(businessId, partyId, chargeId, paymentId, amount, now);
            db.SupplierSettlements.Add(s);
            settlement = s;
        }
        else
        {
            var s = DebtorSettlement.Create(businessId, partyId, chargeId, paymentId, amount, now);
            db.DebtorSettlements.Add(s);
            settlement = s;
        }

        settled.Add(settlement);
    }
}
