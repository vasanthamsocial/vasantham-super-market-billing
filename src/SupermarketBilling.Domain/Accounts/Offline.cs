using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Accounts;

/// <summary>
/// A phone trusted to record collections while it has no signal (spec section 16), enrolled by a manager for one
/// collector. It may hold at most <see cref="OfflineLimit"/> in collections not yet synchronised, for at most
/// <see cref="MaxOfflineHours"/>; its collections reach the server in order (<see cref="LastSequence"/>).
/// </summary>
public sealed class CollectionDevice : ITenantOwned
{
    private CollectionDevice()
    {
        Name = string.Empty;
        TokenHash = [];
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid CollectorUserId { get; private set; }

    public string Name { get; private set; }

    public byte[] TokenHash { get; private set; }

    public decimal OfflineLimit { get; private set; }

    public int MaxOfflineHours { get; private set; }

    /// <summary>The last collection sequence number received from this phone (0 before the first).</summary>
    public long LastSequence { get; private set; }

    public Guid EnrolledByUserId { get; private set; }

    public DateTimeOffset EnrolledAtUtc { get; private set; }

    public DateTimeOffset? LastSyncedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? RevokedByUserId { get; private set; }

    public uint RowVersion { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static CollectionDevice Enrol(Guid businessId, Guid collectorUserId, string name, byte[] tokenHash, decimal offlineLimit, int maxOfflineHours, Guid enrolledBy,
        DateTimeOffset now)
    {
        var device = new CollectionDevice
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            CollectorUserId = collectorUserId,
            Name = (name ?? string.Empty).Trim() is { Length: > 0 and <= 60 } n ? n : throw new DomainException("device.name_required", "Name the phone (max 60 characters)."),
            TokenHash = tokenHash,
            EnrolledByUserId = enrolledBy,
            EnrolledAtUtc = now,
        };
        device.SetLimits(offlineLimit, maxOfflineHours);
        return device;
    }

    public void SetLimits(decimal offlineLimit, int maxOfflineHours)
    {
        OfflineLimit = offlineLimit is >= 0 and <= 10_000_000 && decimal.Round(offlineLimit, 2) == offlineLimit
            ? offlineLimit
            : throw new DomainException("device.limit_invalid", "The offline limit is 0 to Rs. 1,00,00,000 (0 means no offline collection).");
        MaxOfflineHours = maxOfflineHours is >= 1 and <= 168 ? maxOfflineHours : throw new DomainException("device.hours_invalid", "Offline time is 1 to 168 hours.");
    }

    public void Revoke(Guid by, DateTimeOffset now)
    {
        if (RevokedAtUtc is null)
        {
            (RevokedAtUtc, RevokedByUserId) = (now, by);
        }
    }

    /// <summary>A collection with the next sequence number has been received (accepted or quarantined).</summary>
    public void Received(long sequence, DateTimeOffset now)
    {
        if (sequence != LastSequence + 1)
        {
            throw new DomainException("offline.sequence_gap", $"Expected collection {LastSequence + 1} from this phone, not {sequence}.");
        }

        (LastSequence, LastSyncedAtUtc) = (sequence, now);
    }
}

public static class OfflineStatus
{
    /// <summary>Posted as a receipt when it reached the server.</summary>
    public const string Accepted = "ACCEPTED";

    /// <summary>Could not be posted as it was (too old, over the limit, round handed over, not the collector's party...): waits for a manager.</summary>
    public const string Quarantined = "QUARANTINED";

    /// <summary>A manager posted it as a receipt.</summary>
    public const string ResolvedAccepted = "RESOLVED_ACCEPTED";

    /// <summary>A manager decided it is not posted (for example the money went back to the party), with why.</summary>
    public const string ResolvedRejected = "RESOLVED_REJECTED";

    public static readonly IReadOnlyList<string> All = [Accepted, Quarantined, ResolvedAccepted, ResolvedRejected];
}

/// <summary>
/// A collection recorded on a trusted phone without signal, as received by the server: kept as sent (with the phone's
/// time and sequence), with what became of it. Never deleted; a quarantined one is resolved once.
/// </summary>
public sealed class OfflineSubmission : ITenantOwned
{
    private OfflineSubmission()
    {
        Method = Status = PayloadHash = string.Empty;
    }

    /// <summary>The id the phone gave it (also the receipt's idempotency key), so a resend is recognised.</summary>
    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid DeviceId { get; private set; }

    public long Sequence { get; private set; }

    public Guid CollectorUserId { get; private set; }

    public Guid DebtorId { get; private set; }

    public string Method { get; private set; }

    public decimal Amount { get; private set; }

    public string? Reference { get; private set; }

    public string? Note { get; private set; }

    public string? BankName { get; private set; }

    public DateOnly? ChequeDate { get; private set; }

    /// <summary>When the collector recorded it, by the phone's clock.</summary>
    public DateTimeOffset RecordedAtUtc { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public string PayloadHash { get; private set; }

    public string Status { get; private set; }

    public string? Reason { get; private set; }

    public Guid? ReceiptId { get; private set; }

    public Guid? ResolvedByUserId { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public string? ResolutionNote { get; private set; }

    public uint RowVersion { get; private set; }

    public sealed record Item(Guid Id, long Sequence, Guid DebtorId, string Method, decimal Amount, string? Reference, string? Note, string? BankName, DateOnly? ChequeDate,
        DateTimeOffset RecordedAtUtc);

    public static OfflineSubmission Receive(Guid businessId, CollectionDevice device, Item item, string payloadHash, Guid? receiptId, string? quarantineReason,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(item);
        if ((receiptId is null) == (quarantineReason is null))
        {
            throw new InvalidOperationException("A submission is either accepted with a receipt or quarantined with a reason.");
        }

        return new OfflineSubmission
        {
            Id = item.Id,
            BusinessId = businessId,
            DeviceId = device.Id,
            Sequence = item.Sequence,
            CollectorUserId = device.CollectorUserId,
            DebtorId = item.DebtorId,
            Method = item.Method,
            Amount = item.Amount,
            Reference = item.Reference,
            Note = item.Note,
            BankName = item.BankName,
            ChequeDate = item.ChequeDate,
            RecordedAtUtc = item.RecordedAtUtc,
            ReceivedAtUtc = now,
            PayloadHash = payloadHash,
            Status = receiptId is null ? OfflineStatus.Quarantined : OfflineStatus.Accepted,
            Reason = quarantineReason is { Length: > 300 } r ? r[..300] : quarantineReason,
            ReceiptId = receiptId,
        };
    }

    /// <summary>A manager (never the collector) decides a quarantined collection: posted as a receipt, or not, with why.</summary>
    public void Resolve(Guid? receiptId, string note, Guid userId, DateTimeOffset now)
    {
        if (Status != OfflineStatus.Quarantined)
        {
            throw new DomainException("offline.not_quarantined", "Only a quarantined collection is resolved, once.");
        }

        if (userId == CollectorUserId)
        {
            throw new DomainException("offline.self_resolve", "Someone other than the collector decides their quarantined collections.");
        }

        ResolutionNote = (note ?? string.Empty).Trim() is { Length: > 0 and <= 300 } n ? n : throw new DomainException("offline.note_required", "Say what was decided and why.");
        (Status, ReceiptId, ResolvedByUserId, ResolvedAtUtc) = (receiptId is null ? OfflineStatus.ResolvedRejected : OfflineStatus.ResolvedAccepted, receiptId, userId, now);
    }
}

/// <summary>Why an offline collection cannot be posted as it is; checked in the order given.</summary>
public static class OfflineRules
{
    /// <summary>The phone's clock may be this much ahead of the server's before a collection looks like it is from the future.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    public static string? Check(CollectionDevice device, OfflineSubmission.Item item, decimal heldBefore, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Amount <= 0 || decimal.Round(item.Amount, 2) != item.Amount)
        {
            return "The amount is not a positive amount in rupees and paise.";
        }

        if (item.RecordedAtUtc > now + ClockSkew)
        {
            return "Recorded at a time in the future: the phone's clock is wrong.";
        }

        if (now - item.RecordedAtUtc > TimeSpan.FromHours(device.MaxOfflineHours))
        {
            return $"Kept on the phone longer than the {device.MaxOfflineHours} hours allowed.";
        }

        if (heldBefore + item.Amount > device.OfflineLimit)
        {
            return $"Over the phone's offline limit of Rs. {device.OfflineLimit:0.00}.";
        }

        return null;
    }
}
