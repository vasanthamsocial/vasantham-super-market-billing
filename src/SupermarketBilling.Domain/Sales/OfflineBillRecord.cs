using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Sales;

public static class OfflineBillStatus
{
    /// <summary>Posted as an invoice when it arrived (any doubts are in <see cref="OfflineBillRecord.Review"/>).</summary>
    public const string Posted = "POSTED";

    /// <summary>Its number is taken but it could not be posted; a manager decides.</summary>
    public const string Quarantined = "QUARANTINED";

    public const string ResolvedPosted = "RESOLVED_POSTED";

    /// <summary>The manager recorded it as not posted (the number stays used, with why).</summary>
    public const string ResolvedVoid = "RESOLVED_VOID";

    public static readonly IReadOnlyList<string> All = [Posted, Quarantined, ResolvedPosted, ResolvedVoid];
}

/// <summary>
/// An invoice a counter issued without the server, as it arrived: what was printed (kept exactly), its number in the
/// counter's offline series, and what became of it. Every number of the series has one, so the series has no gaps.
/// </summary>
public sealed class OfflineBillRecord : ITenantOwned
{
    private OfflineBillRecord()
    {
        NumberPrefix = Number = Payload = PayloadHash = Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid CounterId { get; private set; }

    public Guid DeviceId { get; private set; }

    public string NumberPrefix { get; private set; }

    public long Sequence { get; private set; }

    public string Number { get; private set; }

    public Guid CashierUserId { get; private set; }

    public Guid ShiftId { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public decimal GrandTotal { get; private set; }

    /// <summary>The bill as the counter sent it (JSON), kept as evidence of what the customer was given.</summary>
    public string Payload { get; private set; }

    public string PayloadHash { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public Guid ReceivedByUserId { get; private set; }

    public string Status { get; private set; }

    /// <summary>Why it could not be posted.</summary>
    public string? Reason { get; private set; }

    /// <summary>What a manager should look at on a posted bill (a price not from the current rules, a changed tax rate...).</summary>
    public string? Review { get; private set; }

    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public Guid? InvoiceId { get; private set; }

    public Guid? ResolvedByUserId { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public string? ResolutionNote { get; private set; }

    public uint RowVersion { get; private set; }

    public static OfflineBillRecord Receive(
        Guid businessId, Guid storeId, Guid counterId, OfflineBill bill, string payload, string payloadHash, Guid? invoiceId, string? reason, string? review,
        Guid receivedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if ((invoiceId is null) == (reason is null))
        {
            throw new DomainException("offline_bill.outcome", "An offline bill is either posted or quarantined with a reason.");
        }

        return new OfflineBillRecord
        {
            Id = bill.Id,
            BusinessId = businessId,
            StoreId = storeId,
            CounterId = counterId,
            DeviceId = bill.DeviceId,
            NumberPrefix = bill.NumberPrefix,
            Sequence = bill.Sequence,
            Number = bill.Number,
            CashierUserId = bill.CashierUserId,
            ShiftId = bill.ShiftId,
            IssuedAtUtc = bill.IssuedAtUtc,
            GrandTotal = bill.GrandTotal,
            Payload = payload,
            PayloadHash = payloadHash,
            ReceivedAtUtc = now,
            ReceivedByUserId = receivedBy,
            Status = invoiceId is null ? OfflineBillStatus.Quarantined : OfflineBillStatus.Posted,
            Reason = reason is null ? null : Clip(reason, 500),
            Review = review is null ? null : Clip(review, 1000),
            InvoiceId = invoiceId,
        };
    }

    /// <summary>A manager has looked at what was flagged on a posted bill.</summary>
    public void Reviewed(string note, Guid by, DateTimeOffset now)
    {
        if (Status != OfflineBillStatus.Posted || Review is null || ReviewedAtUtc is not null)
        {
            throw new DomainException("offline_bill.nothing_to_review", "There is nothing on this bill waiting to be reviewed.");
        }

        ResolutionNote = Note(note);
        ReviewedByUserId = by;
        ReviewedAtUtc = now;
    }

    /// <summary>A quarantined bill is posted after all (with the invoice made now), or recorded as not posted. Once, not by its cashier.</summary>
    public void Resolve(Guid? invoiceId, string note, Guid by, DateTimeOffset now)
    {
        if (Status != OfflineBillStatus.Quarantined)
        {
            throw new DomainException("offline_bill.not_quarantined", "Only a bill waiting for a decision can be resolved.");
        }

        if (by == CashierUserId)
        {
            throw new DomainException("offline_bill.self_resolve", "The cashier who issued the bill cannot decide on it. Another person must.");
        }

        ResolutionNote = Note(note);
        Status = invoiceId is null ? OfflineBillStatus.ResolvedVoid : OfflineBillStatus.ResolvedPosted;
        InvoiceId = invoiceId;
        ResolvedByUserId = by;
        ResolvedAtUtc = now;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 3)] + "...";

    private static string Note(string note) =>
        Business.Required(note, "offline_bill.note_required", "Say what was decided and why (max 300 characters).", 300);
}
