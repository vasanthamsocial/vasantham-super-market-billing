namespace SupermarketBilling.Application.Security;

/// <summary>The signed-in user for the current request.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>Throws when not authenticated.</summary>
    Guid UserId { get; }

    Guid SessionId { get; }

    string? IpAddress { get; }

    string CorrelationId { get; }
}

/// <summary>
/// Answers "may the current user do X here?" from their active role assignments.
/// A business-level check (no store) needs a business-wide assignment; a store-level check also accepts an
/// assignment limited to that store.
/// </summary>
public interface IAccessControl
{
    Task<bool> HasPermissionAsync(string permission, Guid businessId, Guid? storeId, CancellationToken cancellationToken);

    /// <summary>Businesses where the user holds <paramref name="permission"/> at any scope.</summary>
    Task<IReadOnlyList<Guid>> BusinessesWithPermissionAsync(string permission, CancellationToken cancellationToken);

    /// <summary>All permissions the user holds in the business (at business or any store scope).</summary>
    Task<IReadOnlySet<string>> PermissionsInScopeAsync(Guid businessId, Guid? storeId, CancellationToken cancellationToken);
}
