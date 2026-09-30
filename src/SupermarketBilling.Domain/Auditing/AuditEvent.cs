namespace SupermarketBilling.Domain.Auditing;

/// <summary>
/// An immutable record of something that happened in the system.
/// Rows are append-only: the database rejects UPDATE, DELETE and TRUNCATE on this table.
/// </summary>
public sealed class AuditEvent
{
    public const int MaxEventTypeLength = 100;
    public const int MaxEntityTypeLength = 100;
    public const int MaxEntityIdLength = 100;

    private AuditEvent()
    {
        EventType = string.Empty;
        PayloadJson = "{}";
    }

    public Guid Id { get; private set; }

    /// <summary>Monotonic database sequence, used for ordered export and archival.</summary>
    public long Sequence { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string EventType { get; private set; }

    public string? EntityType { get; private set; }

    public string? EntityId { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public Guid? BusinessId { get; private set; }

    public Guid? StoreId { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>JSON details. Must never contain passwords, MFA codes or session tokens.</summary>
    public string PayloadJson { get; private set; }

    public static AuditEvent Create(
        DateTimeOffset occurredAtUtc,
        string eventType,
        string? entityType = null,
        string? entityId = null,
        Guid? actorUserId = null,
        Guid? businessId = null,
        Guid? storeId = null,
        string? correlationId = null,
        string? payloadJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventType.Length > MaxEventTypeLength)
        {
            throw new ArgumentException($"Event type must be at most {MaxEventTypeLength} characters.", nameof(eventType));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Audit timestamps must be UTC.", nameof(occurredAtUtc));
        }

        return new AuditEvent
        {
            Id = Guid.CreateVersion7(occurredAtUtc),
            OccurredAtUtc = occurredAtUtc,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            ActorUserId = actorUserId,
            BusinessId = businessId,
            StoreId = storeId,
            CorrelationId = correlationId,
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
        };
    }
}
