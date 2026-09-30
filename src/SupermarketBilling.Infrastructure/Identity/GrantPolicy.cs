using SupermarketBilling.Domain.Identity;

namespace SupermarketBilling.Infrastructure.Identity;

/// <summary>
/// Rules preventing privilege escalation: nobody can grant, approve or reset beyond their own permissions.
/// </summary>
internal static class GrantPolicy
{
    /// <summary>Permissions the grants give at the scope (business-wide grants, plus a grant for that exact store).</summary>
    public static HashSet<string> PermissionsAt(IEnumerable<ActiveGrant> grants, Guid businessId, Guid? storeId) =>
        grants.Where(g => g.BusinessId == businessId && (g.StoreId is null || (storeId is not null && g.StoreId == storeId)))
            .SelectMany(g => Roles.Get(g.RoleCode).Permissions)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The actor holds <paramref name="permission"/> at the scope and every permission the role would give.</summary>
    public static bool CanGrant(IReadOnlyCollection<ActiveGrant> actorGrants, string permission, RoleDefinition role, Guid businessId, Guid? storeId)
    {
        var held = PermissionsAt(actorGrants, businessId, storeId);
        return held.Contains(permission) && role.Permissions.IsSubsetOf(held);
    }

    /// <summary>
    /// The actor may manage the target (disable, reset password, revoke sessions) only if, for every grant the
    /// target holds, the actor has the permission at that scope and at least the same permissions.
    /// </summary>
    public static bool CanManage(IReadOnlyCollection<ActiveGrant> actorGrants, IReadOnlyCollection<ActiveGrant> targetGrants, string permission) =>
        targetGrants.All(t =>
        {
            var held = PermissionsAt(actorGrants, t.BusinessId, t.StoreId);
            return held.Contains(permission) && Roles.Get(t.RoleCode).Permissions.IsSubsetOf(held);
        });
}
