using SupermarketBilling.Domain.Auditing;

namespace SupermarketBilling.Application.Auditing;

/// <summary>Appends immutable audit events. There is deliberately no update or delete operation.</summary>
public interface IAuditTrail
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
