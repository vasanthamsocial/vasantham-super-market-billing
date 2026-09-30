using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Identity;

internal sealed record ActiveGrant(Guid AssignmentId, string RoleCode, Guid BusinessId, Guid? StoreId);

/// <summary>Permission checks for the current user, loaded once per request from active role assignments.</summary>
internal sealed class AccessControl(SupermarketBillingDbContext db, ICurrentUser currentUser) : IAccessControl
{
    private List<ActiveGrant>? _grants;

    public async Task<bool> HasPermissionAsync(string permission, Guid businessId, Guid? storeId, CancellationToken cancellationToken)
    {
        var grants = await GrantsAsync(cancellationToken).ConfigureAwait(false);
        return Covers(grants, permission, businessId, storeId);
    }

    public async Task<IReadOnlyList<Guid>> BusinessesWithPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        var grants = await GrantsAsync(cancellationToken).ConfigureAwait(false);
        return grants.Where(g => Roles.Get(g.RoleCode).Permissions.Contains(permission)).Select(g => g.BusinessId).Distinct().ToList();
    }

    public async Task<IReadOnlySet<string>> PermissionsInScopeAsync(Guid businessId, Guid? storeId, CancellationToken cancellationToken)
    {
        var grants = await GrantsAsync(cancellationToken).ConfigureAwait(false);
        return grants
            .Where(g => g.BusinessId == businessId && (g.StoreId is null || storeId is null || g.StoreId == storeId))
            .SelectMany(g => Roles.Get(g.RoleCode).Permissions)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Business-level checks (no store) require a business-wide grant; store-level checks also accept a grant
    /// limited to that store.
    /// </summary>
    internal static bool Covers(IEnumerable<ActiveGrant> grants, string permission, Guid businessId, Guid? storeId) =>
        grants.Any(g =>
            g.BusinessId == businessId
            && (g.StoreId is null || (storeId is not null && g.StoreId == storeId))
            && Roles.Get(g.RoleCode).Permissions.Contains(permission));

    internal static async Task<List<ActiveGrant>> LoadGrantsAsync(SupermarketBillingDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.RoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.RevokedAtUtc == null)
            .Join(db.Businesses.Where(b => b.IsActive), a => a.BusinessId, b => b.Id, (a, _) => a)
            .Select(a => new ActiveGrant(a.Id, a.RoleCode, a.BusinessId, a.StoreId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<List<ActiveGrant>> GrantsAsync(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return [];
        }

        return _grants ??= await LoadGrantsAsync(db, currentUser.UserId, cancellationToken).ConfigureAwait(false);
    }
}
