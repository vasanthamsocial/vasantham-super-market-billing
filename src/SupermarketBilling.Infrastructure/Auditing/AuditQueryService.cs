using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Auditing;

/// <summary>Read-only access to a business's audit trail, newest first, paged by sequence number.</summary>
public sealed class AuditQueryService(SupermarketBillingDbContext db, OrganisationService organisation)
{
    public async Task<IReadOnlyList<AuditEventDto>> ListAsync(Guid businessId, long? beforeSequence, int limit, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.AuditView, businessId, null, cancellationToken).ConfigureAwait(false);
        limit = Math.Clamp(limit, 1, 500);
        var query = db.AuditEvents.AsNoTracking().Where(e => e.BusinessId == businessId);
        if (beforeSequence is { } before)
        {
            query = query.Where(e => e.Sequence < before);
        }

        return await (
                from e in query
                from u in db.Users.AsNoTracking().Where(u => u.Id == e.ActorUserId).DefaultIfEmpty()
                orderby e.Sequence descending
                select new AuditEventDto(e.Sequence, e.OccurredAtUtc, e.EventType, e.EntityType, e.EntityId, e.ActorUserId, u == null ? null : u.DisplayName, e.StoreId, e.PayloadJson))
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
