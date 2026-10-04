using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Accounts;

public static class CollectorSessionStatus
{
    /// <summary>Collecting: field receipts are recorded in it.</summary>
    public const string Open = "OPEN";

    /// <summary>The collector has counted and handed over the cash and instruments; waiting for the receiver's count.</summary>
    public const string HandedOver = "HANDED_OVER";

    /// <summary>Another person counted what was received; the variance is recorded.</summary>
    public const string Confirmed = "CONFIRMED";
}

/// <summary>
/// A collector's round (spec section 17): the cash and instruments collected in the field until they are handed over.
/// The collector declares their count; another person counts again and confirms (maker-checker). Expected cash is
/// what the session's cash receipts add up to.
/// </summary>
public sealed class CollectorSession : ITenantOwned
{
    private CollectorSession()
    {
        Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    /// <summary>Where the cash is handed over.</summary>
    public Guid StoreId { get; private set; }

    public Guid CollectorUserId { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string Status { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    public decimal? ExpectedCash { get; private set; }

    public decimal? DeclaredCash { get; private set; }

    public DateTimeOffset? HandedOverAtUtc { get; private set; }

    public Guid? ReceivedByUserId { get; private set; }

    public decimal? CountedCash { get; private set; }

    /// <summary>Counted less expected: negative is a shortage.</summary>
    public decimal? Variance { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static CollectorSession Open(Guid businessId, Guid storeId, Guid collector, DateOnly businessDate, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        StoreId = storeId,
        CollectorUserId = collector,
        BusinessDate = businessDate,
        Status = CollectorSessionStatus.Open,
        OpenedAtUtc = now,
    };

    public void HandOver(decimal expected, decimal declared, DateTimeOffset now)
    {
        if (Status != CollectorSessionStatus.Open)
        {
            throw new DomainException("session.not_open", "This collection round is already handed over.");
        }

        (ExpectedCash, DeclaredCash, HandedOverAtUtc, Status) = (expected, declared, now, CollectorSessionStatus.HandedOver);
    }

    /// <summary>The receiver's own count. A difference needs an explanation.</summary>
    public void Confirm(Guid receiver, decimal counted, string? note, DateTimeOffset now)
    {
        if (Status != CollectorSessionStatus.HandedOver)
        {
            throw new DomainException("session.not_handed_over", "Only a handed-over round can be confirmed.");
        }

        if (receiver == CollectorUserId)
        {
            throw new DomainException("session.self_confirm", "The collector cannot confirm their own handover; another person must count it.");
        }

        var variance = counted - ExpectedCash!.Value;
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (variance != 0 && trimmed is not { Length: >= 5 })
        {
            throw new DomainException("session.variance_note_required", $"The count differs by Rs. {variance:0.00}: explain the difference.");
        }

        (ReceivedByUserId, CountedCash, Variance, Note, ConfirmedAtUtc, Status) =
            (receiver, counted, variance, trimmed is { Length: > 300 } ? trimmed[..300] : trimmed, now, CollectorSessionStatus.Confirmed);
    }
}

/// <summary>The notes and coins counted at a handover: by the collector (declared) and by the receiver (counted).</summary>
public sealed class CollectorSessionCount : ITenantOwned
{
    public const string Declared = "DECLARED";
    public const string Counted = "COUNTED";

    private CollectorSessionCount()
    {
        Kind = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid SessionId { get; private set; }

    public string Kind { get; private set; }

    public decimal Denomination { get; private set; }

    public int Count { get; private set; }

    public static IEnumerable<CollectorSessionCount> From(Guid businessId, Guid sessionId, string kind, IReadOnlyDictionary<decimal, int> counts, DateTimeOffset now)
    {
        Denominations.Total(counts);
        return counts.Where(c => c.Value > 0).Select(c => new CollectorSessionCount
        {
            Id = SequentialGuid.Next(now), BusinessId = businessId, SessionId = sessionId, Kind = kind, Denomination = c.Key, Count = c.Value,
        }).ToList();
    }
}

public static class ChequeStatus
{
    public const string Received = "RECEIVED";
    public const string Deposited = "DEPOSITED";
    public const string Cleared = "CLEARED";

    /// <summary>Dishonoured by the bank: the receipt is reversed, so the debtor owes the amount again.</summary>
    public const string Bounced = "BOUNCED";

    /// <summary>Given back or voided before it was paid: the receipt is reversed.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>A bounced or cancelled cheque for which the debtor gave a new payment.</summary>
    public const string Replaced = "REPLACED";

    public static readonly IReadOnlyList<string> All = [Received, Deposited, Cleared, Bounced, Cancelled, Replaced];

    /// <summary>The moves a cheque may make.</summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (Received, Deposited) or (Received, Cancelled) => true,
        (Deposited, Cleared) or (Deposited, Bounced) => true,
        (Bounced, Replaced) or (Cancelled, Replaced) => true,
        _ => false,
    };
}

/// <summary>A cheque or demand draft received from a debtor, followed until its money is in the bank (spec section 17).</summary>
public sealed class Cheque : ITenantOwned
{
    private Cheque()
    {
        Kind = Number = Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ReceiptId { get; private set; }

    public Guid DebtorId { get; private set; }

    /// <summary>CHEQUE or DEMAND_DRAFT.</summary>
    public string Kind { get; private set; }

    public string Number { get; private set; }

    public string? BankName { get; private set; }

    public DateOnly? ChequeDate { get; private set; }

    public decimal Amount { get; private set; }

    public string Status { get; private set; }

    public Guid? ReplacedByReceiptId { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static Cheque Receive(Guid businessId, DebtorReceipt receipt, string? bankName, DateOnly? chequeDate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Method is not (ReceiptMethods.Cheque or ReceiptMethods.DemandDraft))
        {
            throw new DomainException("cheque.not_an_instrument", "Only a cheque or demand draft is followed in the cheque register.");
        }

        return new Cheque
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            ReceiptId = receipt.Id,
            DebtorId = receipt.DebtorId,
            Kind = receipt.Method,
            Number = receipt.Reference!,
            BankName = string.IsNullOrWhiteSpace(bankName) ? null : bankName.Trim() is { Length: <= 100 } b ? b
                : throw new DomainException("cheque.bank_invalid", "A bank name is at most 100 characters."),
            ChequeDate = chequeDate,
            Amount = receipt.Amount,
            Status = ChequeStatus.Received,
            ReceivedAtUtc = now,
        };
    }

    public void Move(string to, Guid? replacedBy)
    {
        if (!ChequeStatus.CanMove(Status, to))
        {
            throw new DomainException("cheque.move_invalid", $"A {Status.ToLowerInvariant()} cheque cannot become {to.ToLowerInvariant()}.");
        }

        if (to == ChequeStatus.Replaced && replacedBy is null)
        {
            throw new DomainException("cheque.replacement_required", "Name the receipt that replaced this cheque.");
        }

        Status = to;
        ReplacedByReceiptId = to == ChequeStatus.Replaced ? replacedBy : ReplacedByReceiptId;
    }
}

/// <summary>One step in a cheque's life (append-only): who moved it, when, and why.</summary>
public sealed class ChequeEvent : ITenantOwned
{
    private ChequeEvent()
    {
        Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ChequeId { get; private set; }

    public string Status { get; private set; }

    public DateOnly EventDate { get; private set; }

    public string? Note { get; private set; }

    public Guid RecordedByUserId { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static ChequeEvent Record(Guid businessId, Guid chequeId, string status, DateOnly date, string? note, Guid recordedBy, DateTimeOffset now) => new()
    {
        Id = SequentialGuid.Next(now),
        BusinessId = businessId,
        ChequeId = chequeId,
        Status = status,
        EventDate = date,
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() is { Length: <= 300 } n ? n : throw new DomainException("cheque.note_invalid", "A note is at most 300 characters."),
        RecordedByUserId = recordedBy,
        RecordedAtUtc = now,
    };
}

public static class ReversalKinds
{
    /// <summary>A cheque or draft the bank dishonoured.</summary>
    public const string Bounced = "BOUNCED";

    /// <summary>A cheque or draft given back or voided before it was paid.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>A receipt recorded in error, reversed with another person's approval.</summary>
    public const string Correction = "CORRECTION";
}

/// <summary>
/// The reversal of a debtor receipt (posted collections are never edited or deleted, spec section 15): what it paid is
/// owed again. One per receipt; the ledger keeps both the receipt and its reversal.
/// </summary>
public sealed class ReceiptReversal : ITenantOwned
{
    private ReceiptReversal()
    {
        Kind = Reason = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ReceiptId { get; private set; }

    public string Kind { get; private set; }

    public string Reason { get; private set; }

    public Guid? ApprovalRequestId { get; private set; }

    public Guid ReversedByUserId { get; private set; }

    public DateTimeOffset ReversedAtUtc { get; private set; }

    public static ReceiptReversal Record(Guid businessId, Guid receiptId, string kind, string? reason, Guid? approvalId, Guid by, DateTimeOffset now)
    {
        var text = (reason ?? string.Empty).Trim();
        return new ReceiptReversal
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            ReceiptId = receiptId,
            Kind = kind is ReversalKinds.Bounced or ReversalKinds.Cancelled or ReversalKinds.Correction ? kind
                : throw new DomainException("reversal.kind_invalid", $"Unknown reversal '{kind}'."),
            Reason = text.Length is >= 3 and <= 300 ? text : throw new DomainException("reversal.reason_required", "Say why the receipt is reversed (3 to 300 characters)."),
            ApprovalRequestId = approvalId,
            ReversedByUserId = by,
            ReversedAtUtc = now,
        };
    }
}

public static class VisitOutcomes
{
    public const string NoPayment = "NO_PAYMENT";
    public const string NotAvailable = "NOT_AVAILABLE";
    public const string ShopClosed = "SHOP_CLOSED";
    public const string Disputed = "DISPUTED";

    public static readonly IReadOnlyList<string> All = [NoPayment, NotAvailable, ShopClosed, Disputed];
}

/// <summary>A visit that brought no money, and why (a visit that did is shown by its receipt).</summary>
public sealed class VisitOutcome : ITenantOwned
{
    private VisitOutcome()
    {
        Outcome = Note = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid DebtorId { get; private set; }

    public Guid CollectorUserId { get; private set; }

    public DateOnly VisitDate { get; private set; }

    public string Outcome { get; private set; }

    public string Note { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static VisitOutcome Record(Guid businessId, Guid debtorId, Guid collector, DateOnly date, string outcome, string? note, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        DebtorId = debtorId,
        CollectorUserId = collector,
        VisitDate = date,
        Outcome = VisitOutcomes.All.Contains(outcome) ? outcome : throw new DomainException("visit.outcome_invalid", $"Unknown visit outcome '{outcome}'."),
        Note = (note ?? string.Empty).Trim() is { Length: <= 300 } n ? n : throw new DomainException("visit.note_invalid", "A note is at most 300 characters."),
        RecordedAtUtc = now,
    };
}
