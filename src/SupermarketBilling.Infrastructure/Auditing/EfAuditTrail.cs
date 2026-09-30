using SupermarketBilling.Application.Auditing;
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
}
