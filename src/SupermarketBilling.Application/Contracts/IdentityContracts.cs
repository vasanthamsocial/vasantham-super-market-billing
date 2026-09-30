namespace SupermarketBilling.Application.Contracts;

/// <summary>What a session is currently allowed to do.</summary>
public static class SessionStates
{
    /// <summary>Fully signed in.</summary>
    public const string Active = "active";

    /// <summary>Password accepted; the MFA code must still be entered.</summary>
    public const string MfaRequired = "mfa_required";

    /// <summary>The user's role requires MFA and it is not set up yet.</summary>
    public const string MfaEnrolmentRequired = "mfa_enrolment_required";

    /// <summary>A temporary or reset password must be changed first.</summary>
    public const string PasswordChangeRequired = "password_change_required";
}

public sealed record LoginRequest(string Username, string Password);

public sealed record ClientInfo(string? IpAddress, string? UserAgent);

/// <summary>Returned to the API only; the API turns the tokens into cookies and never returns them in the body.</summary>
public sealed record LoginOutcome(string SessionToken, string CsrfToken, DateTimeOffset ExpiresAtUtc, MeResponse Me);

public sealed record MeResponse(
    Guid UserId,
    string Username,
    string DisplayName,
    string SessionState,
    bool MfaEnabled,
    bool MfaRequiredByPolicy,
    IReadOnlyList<MembershipDto> Memberships);

public sealed record MembershipDto(
    Guid BusinessId,
    string BusinessCode,
    string BusinessName,
    IReadOnlyList<RoleGrantDto> Roles,
    IReadOnlyList<string> Permissions);

public sealed record RoleGrantDto(Guid AssignmentId, string RoleCode, string RoleName, Guid? StoreId, string? StoreName);

public sealed record MfaCodeRequest(string Code);

public sealed record MfaSetupResponse(string Secret, string OtpAuthUri);

public sealed record MfaConfirmResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record MfaDisableRequest(string Password, string Code);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetPasswordRequest(string Username, string ResetCode, string NewPassword);

public sealed record SessionDto(Guid Id, DateTimeOffset CreatedAtUtc, DateTimeOffset LastSeenAtUtc, string? IpAddress, string? UserAgent, bool IsCurrent);

public sealed record SetupStatusResponse(bool SetupRequired);

public sealed record SetupRequest(
    string SetupCode,
    CreateBusinessRequest Business,
    CreateStoreRequest Store,
    string OwnerUsername,
    string OwnerDisplayName,
    string OwnerPassword);
