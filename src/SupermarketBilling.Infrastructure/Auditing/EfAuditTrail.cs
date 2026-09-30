using System.Text.Json;
using SupermarketBilling.Application.Auditing;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Auditing;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Auditing;

internal sealed class EfAuditTrail(SupermarketBillingDbContext db) : IAuditTrail
{
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Stage(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        db.AuditEvents.Add(auditEvent);
    }
}

/// <summary>
/// Convenience for services: builds audit events with the current actor, time and correlation id and stages them
/// in the current unit of work. Payloads must never contain passwords, codes or tokens.
/// </summary>
public sealed class AuditRecorder(IAuditTrail trail, ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(
        string eventType,
        string? entityType = null,
        Guid? entityId = null,
        Guid? businessId = null,
        Guid? storeId = null,
        object? details = null,
        Guid? actorUserId = null)
    {
        trail.Stage(AuditEvent.Create(
            clock.GetUtcNow(),
            eventType,
            entityType,
            entityId?.ToString(),
            actorUserId ?? (currentUser.IsAuthenticated ? currentUser.UserId : null),
            businessId,
            storeId,
            currentUser.CorrelationId,
            JsonSerializer.Serialize(new AuditDetails(currentUser.IpAddress, details), Json)));
    }

    private sealed record AuditDetails(string? Ip, object? Details);
}
