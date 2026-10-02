using System.Globalization;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Sales;

public static class ShiftStatus
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";
}

public static class CashMovementKinds
{
    /// <summary>Cash added to the drawer (for example more change from the safe).</summary>
    public const string PayIn = "PAY_IN";

    /// <summary>Cash paid out of the drawer for an expense; needs a supervisor (or the permission).</summary>
    public const string PayOut = "PAY_OUT";

    /// <summary>Cash taken from the drawer to the safe during the shift, to keep the drawer low.</summary>
    public const string Drop = "DROP";

    public static readonly IReadOnlyList<string> All = [PayIn, PayOut, Drop];
}

/// <summary>Indian notes and coins counted in a drawer (Rs. 10 and 20 exist as both; they are counted together).</summary>
public static class Denominations
{
    public static readonly IReadOnlyList<decimal> All = [2000, 500, 200, 100, 50, 20, 10, 5, 2, 1];

    /// <summary>Validates a count and returns its total.</summary>
    public static decimal Total(IReadOnlyDictionary<decimal, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        foreach (var (denomination, count) in counts)
        {
            if (!All.Contains(denomination))
            {
                throw new DomainException("count.denomination_invalid", string.Create(CultureInfo.InvariantCulture, $"Rs. {denomination} is not a note or coin that is counted."));
            }

            if (count is < 0 or > 100_000)
            {
                throw new DomainException("count.number_invalid", "Each count must be between 0 and 100000.");
            }
        }

        return counts.Sum(c => c.Key * c.Value);
    }
}

/// <summary>
/// A cashier's shift on a counter: from the opening float to the closing count. Billing needs the cashier's own open
/// shift; every bill and return records it. Closing is blind: the cashier counts before seeing what was expected. A
/// difference needs a note and is reviewed by a manager who is not the cashier.
/// </summary>
public sealed class Shift : ITenantOwned
{
    private Shift()
    {
        Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid CounterId { get; private set; }

    public Guid CashierUserId { get; private set; }

    public string Status { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    public decimal OpeningFloat { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public Guid? ClosedByUserId { get; private set; }

    public decimal? ExpectedCash { get; private set; }

    public decimal? CountedCash { get; private set; }

    public decimal? Difference { get; private set; }

    public string? CloseNote { get; private set; }

    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public string? ReviewNote { get; private set; }

    public uint RowVersion { get; private set; }

    public bool NeedsReview => Status == ShiftStatus.Closed && Difference is not 0 && ReviewedByUserId is null;

    public static Shift Open(Guid businessId, Guid storeId, Guid counterId, Guid cashier, DateOnly businessDate, decimal openingFloat, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        StoreId = storeId,
        CounterId = counterId,
        CashierUserId = cashier,
        Status = ShiftStatus.Open,
        BusinessDate = businessDate,
        OpenedAtUtc = now,
        OpeningFloat = openingFloat >= 0 ? openingFloat : throw new DomainException("shift.float_invalid", "The opening float cannot be negative."),
    };

    /// <summary>Closes the shift with the counted cash; a difference from the expected cash needs a note.</summary>
    public void Close(decimal expected, decimal counted, string? note, Guid closedBy, DateTimeOffset now)
    {
        if (Status != ShiftStatus.Open)
        {
            throw new DomainException("shift.closed", "This shift is already closed.");
        }

        var difference = counted - expected;
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (difference != 0 && (trimmed is null || trimmed.Length < 5))
        {
            throw new DomainException("shift.note_required",
                string.Create(CultureInfo.InvariantCulture, $"The drawer is Rs. {Math.Abs(difference):0.00} {(difference > 0 ? "over" : "short")}. Explain the difference to close the shift."));
        }

        Status = ShiftStatus.Closed;
        ExpectedCash = expected;
        CountedCash = counted;
        Difference = difference;
        CloseNote = trimmed is { Length: > 300 } ? trimmed[..300] : trimmed;
        ClosedByUserId = closedBy;
        ClosedAtUtc = now;
    }

    /// <summary>A manager (never the shift's cashier) accepts the explanation of a difference.</summary>
    public void Review(Guid reviewer, string note, DateTimeOffset now)
    {
        if (!NeedsReview)
        {
            throw new DomainException("shift.review_not_needed", "This shift has no difference waiting for review.");
        }

        if (reviewer == CashierUserId || reviewer == ClosedByUserId)
        {
            throw new DomainException("shift.review_self", "Someone other than the person who closed the shift must review its difference.");
        }

        ReviewedByUserId = reviewer;
        ReviewedAtUtc = now;
        ReviewNote = Business.Required(note, "shift.review_note_required", "Write what was found (max 300 characters).", 300);
    }
}

/// <summary>One line of a drawer count: how many of one note or coin.</summary>
public sealed class ShiftCount : ITenantOwned
{
    public const string Opening = "OPENING";
    public const string Closing = "CLOSING";

    private ShiftCount()
    {
        Kind = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ShiftId { get; private set; }

    public string Kind { get; private set; }

    public decimal Denomination { get; private set; }

    public int Count { get; private set; }

    public static IEnumerable<ShiftCount> From(Guid businessId, Guid shiftId, string kind, IReadOnlyDictionary<decimal, int> counts, DateTimeOffset now) =>
        counts.Where(c => c.Value > 0).OrderByDescending(c => c.Key).Select(c => new ShiftCount
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            ShiftId = shiftId,
            Kind = kind,
            Denomination = c.Key,
            Count = c.Value,
        });
}

/// <summary>Cash into or out of the drawer during a shift other than sales and refunds.</summary>
public sealed class CashMovement : ITenantOwned
{
    private CashMovement()
    {
        Kind = Reason = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ShiftId { get; private set; }

    public string Kind { get; private set; }

    public decimal Amount { get; private set; }

    public string Reason { get; private set; }

    public Guid RecordedByUserId { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static CashMovement Record(Guid businessId, Guid shiftId, string kind, decimal amount, string reason, Guid recordedBy, Guid? approvalId, DateTimeOffset now)
    {
        if (!CashMovementKinds.All.Contains(kind))
        {
            throw new DomainException("cash_movement.kind_invalid", $"Unknown cash movement '{kind}'.");
        }

        if (amount <= 0 || amount != InvoiceCalculator.Money(amount))
        {
            throw new DomainException("cash_movement.amount_invalid", "Enter a positive amount in rupees and paise.");
        }

        return new CashMovement
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            ShiftId = shiftId,
            Kind = kind,
            Amount = amount,
            Reason = Business.Required(reason, "cash_movement.reason_required", "Give the reason (max 200 characters).", 200),
            RecordedByUserId = recordedBy,
            ApprovalId = approvalId,
            RecordedAtUtc = now,
        };
    }
}

/// <summary>What the drawer should hold: the float, cash taken net of change, less cash refunds, plus pay-ins, less pay-outs and drops.</summary>
public static class ShiftCash
{
    /// <param name="cashReceived">Cash taken from debtors at the counter during the shift.</param>
    public static decimal Expected(
        decimal openingFloat, decimal cashTendered, decimal changeGiven, decimal cashRefunded, decimal payIns, decimal payOuts, decimal drops, decimal cashReceived = 0) =>
        openingFloat + cashTendered - changeGiven - cashRefunded + payIns - payOuts - drops + cashReceived;
}
