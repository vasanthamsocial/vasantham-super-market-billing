namespace SupermarketBilling.Application.Contracts;

/// <param name="TaxRegistrationMode">GST_REGULAR, GST_COMPOSITION or NOT_GST_REGISTERED. Defaults to GST_REGULAR when a GSTIN is given, otherwise NOT_GST_REGISTERED.</param>
public sealed record CreateBusinessRequest(string Code, string LegalName, string? TradeName, string StateCode, string? Gstin, string? Address, string? TaxRegistrationMode = null);

public sealed record UpdateBusinessRequest(
    string LegalName, string? TradeName, string StateCode, string? Gstin, string? Address, bool RequireMfaForPrivilegedUsers, uint RowVersion,
    bool RequirePriceApproval = false);

public sealed record BusinessDto(
    Guid Id, string Code, string LegalName, string TradeName, string StateCode, string? Gstin, string? Address,
    bool IsActive, bool RequireMfaForPrivilegedUsers, uint RowVersion, bool RequirePriceApproval);

public sealed record CreateStoreRequest(string Code, string Name, string StateCode, string? Gstin, string? Address);

public sealed record UpdateStoreRequest(string Name, string StateCode, string? Gstin, string? Address, bool IsActive, uint RowVersion);

public sealed record StoreDto(
    Guid Id, Guid BusinessId, string Code, string Name, string StateCode, string? Gstin, string? Address, string TimeZone, bool IsActive, uint RowVersion);

public sealed record CreateUserRequest(string Username, string DisplayName, string TemporaryPassword, string RoleCode, Guid? StoreId);

/// <param name="PendingRoles">Role names requested for this user that are still waiting for approval.</param>
public sealed record UserDto(
    Guid Id, string Username, string DisplayName, bool IsActive, bool IsLockedOut, bool MfaEnabled, bool MustChangePassword,
    DateTimeOffset? LastLoginAtUtc, IReadOnlyList<RoleGrantDto> Roles, IReadOnlyList<string> PendingRoles);

public sealed record SetUserActiveRequest(bool IsActive);

public sealed record GrantRoleRequest(string RoleCode, Guid? StoreId, string? Reason);

/// <summary>Either applied immediately, or queued for a second person's approval.</summary>
public sealed record GrantRoleResponse(string Outcome, Guid? AssignmentId, Guid? ApprovalRequestId, string Message);

public sealed record PasswordResetIssuedResponse(string ResetCode, DateTimeOffset ExpiresAtUtc);

public sealed record RoleDto(string Code, string Name, bool IsPrivileged, bool BusinessWideOnly, IReadOnlyList<string> Permissions);

public sealed record ApprovalDto(
    Guid Id, Guid BusinessId, string Type, string Summary, string? Reason, string Status,
    Guid RequestedByUserId, string RequestedBy, DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc,
    Guid? DecidedByUserId, string? DecidedBy, DateTimeOffset? DecidedAtUtc, string? DecisionNote, bool CanDecide);

public sealed record ApprovalDecisionRequest(string? Note);

public sealed record AuditEventDto(
    long Sequence, DateTimeOffset OccurredAtUtc, string EventType, string? EntityType, string? EntityId,
    Guid? ActorUserId, string? Actor, Guid? StoreId, string PayloadJson);
