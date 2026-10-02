using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Accounts;

public static class PartyTypes
{
    public const string Supplier = "SUPPLIER";
    public const string Debtor = "DEBTOR";
}

/// <summary>
/// Kinds of ledger entry. A positive amount increases what is owed (to the supplier, or by the debtor); a negative
/// amount reduces it.
/// </summary>
public static class LedgerEntryTypes
{
    /// <summary>The balance brought forward when the account starts in this system (either sign; once per account).</summary>
    public const string Opening = "OPENING";

    /// <summary>A posted goods receipt: owed to the supplier.</summary>
    public const string Grn = "GRN";

    /// <summary>Paid to a supplier.</summary>
    public const string Payment = "PAYMENT";

    /// <summary>Goods returned to a supplier.</summary>
    public const string DebitNote = "DEBIT_NOTE";

    /// <summary>A credit sale: owed by the debtor.</summary>
    public const string Invoice = "INVOICE";

    /// <summary>Received from a debtor.</summary>
    public const string Receipt = "RECEIPT";

    /// <summary>Goods returned by a debtor against a credit sale.</summary>
    public const string CreditNote = "CREDIT_NOTE";

    /// <summary>A correction approved by a second person (either sign).</summary>
    public const string Adjustment = "ADJUSTMENT";

    public static readonly IReadOnlyList<string> Supplier = [Opening, Grn, Payment, DebitNote, Adjustment];

    public static readonly IReadOnlyList<string> Debtor = [Opening, Invoice, Receipt, CreditNote, Adjustment];

    /// <summary>The sign an amount of this kind must have: +1, -1, or 0 for either.</summary>
    public static int Sign(string type) => type switch
    {
        Grn or Invoice => 1,
        Payment or DebitNote or Receipt or CreditNote => -1,
        _ => 0,
    };
}

/// <summary>
/// One line of a supplier's or debtor's account. Entries are only ever added; each carries the running balance, and
/// the database checks that every balance follows from the one before.
/// </summary>
public abstract class PartyLedgerEntry : ITenantOwned
{
    public Guid Id { get; protected set; }

    public Guid BusinessId { get; protected set; }

    public Guid PartyId { get; protected set; }

    /// <summary>1, 2, 3... per account, without gaps.</summary>
    public long Sequence { get; protected set; }

    public string EntryType { get; protected set; } = string.Empty;

    public Guid? StoreId { get; protected set; }

    public Guid? DocumentId { get; protected set; }

    public string? DocumentNumber { get; protected set; }

    public DateOnly EntryDate { get; protected set; }

    /// <summary>For entries that are owed (bills, invoices, a positive opening): when payment is due.</summary>
    public DateOnly? DueDate { get; protected set; }

    public decimal Amount { get; protected set; }

    public decimal BalanceAfter { get; protected set; }

    public string Narration { get; protected set; } = string.Empty;

    public Guid CreatedByUserId { get; protected set; }

    public DateTimeOffset CreatedAtUtc { get; protected set; }

    /// <summary>A charge (it adds to what is owed) can be settled by payments; a payment settles charges.</summary>
    public bool IsCharge => Amount > 0;

    public sealed record Posting(
        string EntryType, Guid? StoreId, Guid? DocumentId, string? DocumentNumber, DateOnly EntryDate, DateOnly? DueDate, decimal Amount, string Narration);

    protected void Fill(IReadOnlyList<string> allowedTypes, Guid businessId, Guid partyId, long previousSequence, decimal previousBalance, Posting posting, Guid createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(posting);
        if (!allowedTypes.Contains(posting.EntryType))
        {
            throw new DomainException("ledger.type_invalid", $"'{posting.EntryType}' entries do not belong in this account.");
        }

        if (posting.Amount == 0 || decimal.Round(posting.Amount, 2) != posting.Amount)
        {
            throw new DomainException("ledger.amount_invalid", "A ledger amount is a non-zero amount in rupees and paise.");
        }

        if (LedgerEntryTypes.Sign(posting.EntryType) is var sign and not 0 && Math.Sign(posting.Amount) != sign)
        {
            throw new DomainException("ledger.sign_invalid", $"A {posting.EntryType} entry must {(sign > 0 ? "add to" : "reduce")} the balance.");
        }

        var narration = (posting.Narration ?? string.Empty).Trim();
        Id = SequentialGuid.Next(now);
        BusinessId = businessId;
        PartyId = partyId;
        Sequence = previousSequence + 1;
        EntryType = posting.EntryType;
        StoreId = posting.StoreId;
        DocumentId = posting.DocumentId;
        DocumentNumber = posting.DocumentNumber;
        EntryDate = posting.EntryDate;
        DueDate = posting.Amount > 0 ? posting.DueDate ?? posting.EntryDate : null;
        Amount = posting.Amount;
        BalanceAfter = previousBalance + posting.Amount;
        Narration = narration.Length is > 0 and <= 300 ? narration : throw new DomainException("ledger.narration_invalid", "Describe the entry (max 300 characters).");
        CreatedByUserId = createdBy;
        CreatedAtUtc = now;
    }
}

public sealed class SupplierLedgerEntry : PartyLedgerEntry
{
    private SupplierLedgerEntry()
    {
    }

    public static SupplierLedgerEntry Create(Guid businessId, Guid supplierId, long previousSequence, decimal previousBalance, Posting posting, Guid createdBy, DateTimeOffset now)
    {
        var entry = new SupplierLedgerEntry();
        entry.Fill(LedgerEntryTypes.Supplier, businessId, supplierId, previousSequence, previousBalance, posting, createdBy, now);
        return entry;
    }
}

public sealed class DebtorLedgerEntry : PartyLedgerEntry
{
    private DebtorLedgerEntry()
    {
    }

    public static DebtorLedgerEntry Create(Guid businessId, Guid debtorId, long previousSequence, decimal previousBalance, Posting posting, Guid createdBy, DateTimeOffset now)
    {
        var entry = new DebtorLedgerEntry();
        entry.Fill(LedgerEntryTypes.Debtor, businessId, debtorId, previousSequence, previousBalance, posting, createdBy, now);
        return entry;
    }
}

/// <summary>Part of a payment (a negative entry) applied to a charge (a positive entry) of the same account.</summary>
public abstract class PartySettlement : ITenantOwned
{
    public Guid Id { get; protected set; }

    public Guid BusinessId { get; protected set; }

    public Guid PartyId { get; protected set; }

    public Guid ChargeEntryId { get; protected set; }

    public Guid PaymentEntryId { get; protected set; }

    public decimal Amount { get; protected set; }

    public DateTimeOffset CreatedAtUtc { get; protected set; }

    protected void Fill(Guid businessId, Guid partyId, Guid chargeEntryId, Guid paymentEntryId, decimal amount, DateTimeOffset now)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            throw new DomainException("settlement.amount_invalid", "A settled amount is positive, in rupees and paise.");
        }

        (Id, BusinessId, PartyId, ChargeEntryId, PaymentEntryId, Amount, CreatedAtUtc) = (SequentialGuid.Next(now), businessId, partyId, chargeEntryId, paymentEntryId, amount, now);
    }
}

public sealed class SupplierSettlement : PartySettlement
{
    private SupplierSettlement()
    {
    }

    public static SupplierSettlement Create(Guid businessId, Guid supplierId, Guid chargeEntryId, Guid paymentEntryId, decimal amount, DateTimeOffset now)
    {
        var settlement = new SupplierSettlement();
        settlement.Fill(businessId, supplierId, chargeEntryId, paymentEntryId, amount, now);
        return settlement;
    }
}

public sealed class DebtorSettlement : PartySettlement
{
    private DebtorSettlement()
    {
    }

    public static DebtorSettlement Create(Guid businessId, Guid debtorId, Guid chargeEntryId, Guid paymentEntryId, decimal amount, DateTimeOffset now)
    {
        var settlement = new DebtorSettlement();
        settlement.Fill(businessId, debtorId, chargeEntryId, paymentEntryId, amount, now);
        return settlement;
    }
}

/// <summary>An open charge or an unapplied payment of an account: what is left of it after settlements.</summary>
public sealed record OpenItem(Guid EntryId, DateOnly EntryDate, DateOnly? DueDate, long Sequence, decimal Remaining);

public static class SettlementPlanner
{
    /// <summary>
    /// Applies up to <paramref name="amount"/> to the open charges, oldest due date first (then oldest entry), or to
    /// the charges and amounts named. Returns what is applied to each charge; whatever is left stays unapplied.
    /// </summary>
    public static IReadOnlyList<(Guid ChargeEntryId, decimal Amount)> Plan(
        IReadOnlyList<OpenItem> openCharges, decimal amount, IReadOnlyList<(Guid ChargeEntryId, decimal Amount)>? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(openCharges);
        if (chosen is { Count: > 0 })
        {
            if (chosen.GroupBy(c => c.ChargeEntryId).Any(g => g.Count() > 1))
            {
                throw new DomainException("settlement.duplicate", "Each bill may be named only once.");
            }

            foreach (var (chargeId, applied) in chosen)
            {
                var open = openCharges.FirstOrDefault(c => c.EntryId == chargeId)
                    ?? throw new DomainException("settlement.not_open", "A bill named is not open on this account.");
                if (applied <= 0 || decimal.Round(applied, 2) != applied || applied > open.Remaining)
                {
                    throw new DomainException("settlement.too_much", $"At most Rs. {open.Remaining:0.00} can be applied to that bill.");
                }
            }

            return chosen.Sum(c => c.Amount) <= amount
                ? chosen
                : throw new DomainException("settlement.exceeds_payment", "More is applied to bills than the payment amount.");
        }

        var plan = new List<(Guid, decimal)>();
        var left = amount;
        foreach (var charge in openCharges.OrderBy(c => c.DueDate ?? c.EntryDate).ThenBy(c => c.Sequence))
        {
            if (left <= 0)
            {
                break;
            }

            var applied = Math.Min(left, charge.Remaining);
            if (applied > 0)
            {
                plan.Add((charge.EntryId, applied));
                left -= applied;
            }
        }

        return plan;
    }
}

public static class PartyPaymentMethods
{
    public const string Cash = "CASH";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Upi = "UPI";
    public const string Cheque = "CHEQUE";

    public static readonly IReadOnlyList<string> All = [Cash, BankTransfer, Upi, Cheque];
}

/// <summary>Money paid to a supplier (numbered per store). It never changes; its ledger entry reduces what is owed.</summary>
public sealed class SupplierPayment : ITenantOwned
{
    private SupplierPayment()
    {
        Number = Method = Note = IdempotencyKey = RequestHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string Number { get; private set; }

    public long SequenceNumber { get; private set; }

    public DateOnly PaymentDate { get; private set; }

    public string Method { get; private set; }

    /// <summary>Cheque number, bank or UPI reference.</summary>
    public string? Reference { get; private set; }

    public decimal Amount { get; private set; }

    public string Note { get; private set; }

    public Guid PaidByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    public static SupplierPayment Create(
        Guid id, Guid businessId, Guid storeId, Guid supplierId, string number, long sequence, DateOnly paymentDate, string method, string? reference,
        decimal amount, string? note, Guid paidBy, string idempotencyKey, string requestHash, DateTimeOffset now)
    {
        if (!PartyPaymentMethods.All.Contains(method))
        {
            throw new DomainException("payment.method_invalid", $"Unknown payment method '{method}'.");
        }

        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            throw new DomainException("payment.amount_invalid", "A payment is a positive amount in rupees and paise.");
        }

        var cleanReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        if (cleanReference is { Length: > 40 })
        {
            throw new DomainException("payment.reference_invalid", "A payment reference is at most 40 characters.");
        }

        if (method == PartyPaymentMethods.Cheque && cleanReference is null)
        {
            throw new DomainException("payment.cheque_number_required", "Enter the cheque number.");
        }

        var cleanNote = (note ?? string.Empty).Trim();
        return new SupplierPayment
        {
            Id = id,
            BusinessId = businessId,
            StoreId = storeId,
            SupplierId = supplierId,
            Number = number,
            SequenceNumber = sequence,
            PaymentDate = paymentDate,
            Method = method,
            Reference = cleanReference,
            Amount = amount,
            Note = cleanNote.Length <= 300 ? cleanNote : throw new DomainException("payment.note_invalid", "A note is at most 300 characters."),
            PaidByUserId = paidBy,
            CreatedAtUtc = now,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
        };
    }
}

public static class ReceiptMethods
{
    public const string Cash = "CASH";
    public const string Card = "CARD";
    public const string Upi = "UPI";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Cheque = "CHEQUE";

    public static readonly IReadOnlyList<string> All = [Cash, Card, Upi, BankTransfer, Cheque];
}

/// <summary>
/// Money received from a debtor (numbered per store). Taken at a counter, it belongs to the cashier's open shift (and
/// cash goes into that drawer); taken in the office, it has no shift. It never changes; its ledger entry reduces what is owed.
/// </summary>
public sealed class DebtorReceipt : ITenantOwned
{
    private DebtorReceipt()
    {
        Number = Method = Note = IdempotencyKey = RequestHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid DebtorId { get; private set; }

    public string Number { get; private set; }

    public long SequenceNumber { get; private set; }

    public DateOnly ReceiptDate { get; private set; }

    public string Method { get; private set; }

    /// <summary>Cheque number, card slip, UPI or bank reference.</summary>
    public string? Reference { get; private set; }

    public decimal Amount { get; private set; }

    public string Note { get; private set; }

    public Guid? CounterId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public Guid? ShiftId { get; private set; }

    /// <summary>Who received the money.</summary>
    public Guid CashierUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    public sealed record AtCounter(Guid CounterId, Guid DeviceId, Guid ShiftId);

    public static DebtorReceipt Create(
        Guid id, Guid businessId, Guid storeId, Guid debtorId, string number, long sequence, DateOnly receiptDate, string method, string? reference, decimal amount,
        string? note, AtCounter? counter, Guid receivedBy, string idempotencyKey, string requestHash, DateTimeOffset now)
    {
        if (!ReceiptMethods.All.Contains(method))
        {
            throw new DomainException("receipt.method_invalid", $"Unknown payment method '{method}'.");
        }

        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            throw new DomainException("receipt.amount_invalid", "A receipt is a positive amount in rupees and paise.");
        }

        var cleanReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        if (cleanReference is { Length: > 40 })
        {
            throw new DomainException("receipt.reference_invalid", "A payment reference is at most 40 characters.");
        }

        if (method == ReceiptMethods.Cheque && cleanReference is null)
        {
            throw new DomainException("receipt.cheque_number_required", "Enter the cheque number.");
        }

        var cleanNote = (note ?? string.Empty).Trim();
        return new DebtorReceipt
        {
            Id = id,
            BusinessId = businessId,
            StoreId = storeId,
            DebtorId = debtorId,
            Number = number,
            SequenceNumber = sequence,
            ReceiptDate = receiptDate,
            Method = method,
            Reference = cleanReference,
            Amount = amount,
            Note = cleanNote.Length <= 300 ? cleanNote : throw new DomainException("receipt.note_invalid", "A note is at most 300 characters."),
            CounterId = counter?.CounterId,
            DeviceId = counter?.DeviceId,
            ShiftId = counter?.ShiftId,
            CashierUserId = receivedBy,
            CreatedAtUtc = now,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
        };
    }
}
