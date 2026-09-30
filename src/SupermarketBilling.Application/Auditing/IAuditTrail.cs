using SupermarketBilling.Domain.Auditing;

namespace SupermarketBilling.Application.Auditing;

/// <summary>Appends immutable audit events. There is deliberately no update or delete operation.</summary>
public interface IAuditTrail
{
    /// <summary>Writes the event immediately in its own save.</summary>
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the event to the current unit of work so it commits atomically with the change it describes.
    /// Use this for every business change; the change and its audit record succeed or fail together.
    /// </summary>
    void Stage(AuditEvent auditEvent);
}
