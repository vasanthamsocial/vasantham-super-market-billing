using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Identity;

/// <summary>Sign-in, sign-out, MFA, password change and reset, and a user's own sessions.</summary>
public sealed class AuthService(
    SupermarketBillingDbContext db,
    TenantContext tenant,
    TenantResolver tenants,
    SessionService sessions,
    PasswordHashing passwords,
    SecretProtector protector,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public const string MfaSecretPurpose = "mfa-secret";
    private const string Issuer = "SupermarketBilling";
    private const int RecoveryCodeCount = 10;
    private const string InvalidCredentials = "Invalid username or password.";

    private SecurityOptions Settings => options.Value;

    public Task<LoginOutcome> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
        InUserTransactionAsync(ct => LoginCoreAsync(request, client, ct), cancellationToken);

    private async Task<LoginOutcome> LoginCoreAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);
        var now = clock.GetUtcNow();
        var tenantId = await ResolveTenantAsync(request.CompanyCode, cancellationToken).ConfigureAwait(false);
        var user = tenantId is null ? null : await FindUserForUpdateAsync(request.Username, cancellationToken).ConfigureAwait(false);

        if (user is null || !user.IsActive)
        {
            // Same work and same answer whether the username is unknown or disabled.
            passwords.VerifyDummy(request.Password ?? string.Empty);
            audit.Record("auth.login_failed", "user", user?.Id, details: new { reason = user is null ? "unknown_user" : "disabled" }, actorUserId: user?.Id);
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            throw new AppException(ErrorKind.Unauthorized, "invalid_credentials", InvalidCredentials);
        }

        if (user.IsLockedOut(now))
        {
            audit.Record("auth.login_blocked", "user", user.Id, details: new { reason = "locked" }, actorUserId: user.Id);
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            throw LockedOut(user);
        }

        var (valid, needsRehash) = passwords.Verify(user.PasswordHash, request.Password ?? string.Empty);
        if (!valid)
        {
            await RecordFailureAsync(user, "wrong_password", now, cancellationToken).ConfigureAwait(false);
            throw user.IsLockedOut(now)
                ? LockedOut(user)
                : new AppException(ErrorKind.Unauthorized, "invalid_credentials", InvalidCredentials);
        }

        if (needsRehash)
        {
            user.SetPassword(passwords.Hash(request.Password!), user.PasswordChangedAtUtc, user.MustChangePassword);
        }

        user.RecordSuccessfulLogin(now);
        var sessionToken = SecretTokens.NewToken();
        var csrfToken = SecretTokens.NewToken();
        var session = Session.Start(
            user.Id,
            SecretTokens.Hash(sessionToken),
            SecretTokens.Hash(csrfToken),
            now,
            TimeSpan.FromMinutes(Settings.SessionIdleMinutes),
            TimeSpan.FromHours(Settings.SessionAbsoluteHours),
            client.IpAddress,
            client.UserAgent);
        db.Sessions.Add(session);
        audit.Record("auth.login_succeeded", "session", session.Id, details: new { mfaPending = user.MfaEnabled }, actorUserId: user.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);

        var me = await BuildMeAsync(user, session.MfaSatisfied, cancellationToken).ConfigureAwait(false);
        return new LoginOutcome(sessionToken, csrfToken, session.AbsoluteExpiresAtUtc, me);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == currentUser.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        session.Revoke(clock.GetUtcNow(), "logout");
        audit.Record("auth.logout", "session", session.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken cancellationToken)
    {
        var user = await CurrentUserEntityAsync(cancellationToken).ConfigureAwait(false);
        var mfaSatisfied = await db.Sessions.Where(s => s.Id == currentUser.SessionId).Select(s => s.MfaSatisfied)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        return await BuildMeAsync(user, mfaSatisfied, cancellationToken).ConfigureAwait(false);
    }

    public Task<MeResponse> VerifyMfaAsync(MfaCodeRequest request, CancellationToken cancellationToken) =>
        InUserTransactionAsync(ct => VerifyMfaCoreAsync(request, ct), cancellationToken);

    private async Task<MeResponse> VerifyMfaCoreAsync(MfaCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        var user = await LockCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions.FirstAsync(s => s.Id == currentUser.SessionId, cancellationToken).ConfigureAwait(false);
        if (!user.MfaEnabled || session.MfaSatisfied)
        {
            throw AppException.Validation("mfa.not_required", "This session does not need an MFA code.");
        }

        if (user.IsLockedOut(now))
        {
            throw LockedOut(user);
        }

        var secret = protector.Unprotect(user.MfaSecretProtected!, MfaSecretPurpose);
        var step = Totp.Verify(secret, request.Code, now, user.MfaLastUsedStep);
        var usedRecoveryCode = false;
        if (step is null)
        {
            usedRecoveryCode = await TryUseRecoveryCodeAsync(user.Id, request.Code, now, cancellationToken).ConfigureAwait(false);
        }

        if (step is null && !usedRecoveryCode)
        {
            await RecordFailureAsync(user, "wrong_mfa_code", now, cancellationToken).ConfigureAwait(false);
            if (user.IsLockedOut(now))
            {
                session.Revoke(now, "locked");
                await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
                throw LockedOut(user);
            }

            throw new AppException(ErrorKind.Unauthorized, "mfa.invalid_code", "The code is not valid. Check the time on your phone and try again.");
        }

        if (step is { } matched)
        {
            user.RecordMfaStep(matched);
        }

        user.RecordSuccessfulLogin(now);
        session.MarkMfaSatisfied();
        audit.Record("auth.mfa_verified", "session", session.Id, details: new { recoveryCode = usedRecoveryCode });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await BuildMeAsync(user, mfaSatisfied: true, cancellationToken).ConfigureAwait(false);
    }

    public Task<MfaSetupResponse> BeginMfaSetupAsync(CancellationToken cancellationToken) =>
        InUserTransactionAsync(BeginMfaSetupCoreAsync, cancellationToken);

    private async Task<MfaSetupResponse> BeginMfaSetupCoreAsync(CancellationToken cancellationToken)
    {
        var user = await LockCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (user.MfaEnabled)
        {
            throw AppException.Conflict("mfa.already_enabled", "MFA is already enabled. Disable it first to set up a new device.");
        }

        var secret = Totp.NewSecret();
        user.BeginMfaEnrolment(protector.Protect(secret, MfaSecretPurpose));
        audit.Record("auth.mfa_setup_started", "user", user.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new MfaSetupResponse(Totp.Base32Encode(secret), Totp.OtpAuthUri(Issuer, user.Username, secret));
    }

    public Task<MfaConfirmResponse> ConfirmMfaSetupAsync(MfaCodeRequest request, CancellationToken cancellationToken) =>
        InUserTransactionAsync(ct => ConfirmMfaSetupCoreAsync(request, ct), cancellationToken);

    private async Task<MfaConfirmResponse> ConfirmMfaSetupCoreAsync(MfaCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        var user = await LockCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (user.MfaPendingSecretProtected is null)
        {
            throw AppException.Validation("mfa.no_pending_enrolment", "Start MFA setup first.");
        }

        var secret = protector.Unprotect(user.MfaPendingSecretProtected, MfaSecretPurpose);
        var step = Totp.Verify(secret, request.Code, now, lastUsedStep: null)
            ?? throw AppException.Validation("mfa.invalid_code", "The code is not valid. Scan the QR code again and enter the current 6-digit code.");

        user.ConfirmMfaEnrolment(step);
        await db.MfaRecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => SecretTokens.NewHumanCode()).ToList();
        foreach (var code in codes)
        {
            db.MfaRecoveryCodes.Add(MfaRecoveryCode.Create(user.Id, SecretTokens.HashHumanCode(code), now));
        }

        var session = await db.Sessions.FirstAsync(s => s.Id == currentUser.SessionId, cancellationToken).ConfigureAwait(false);
        session.MarkMfaSatisfied();
        audit.Record("auth.mfa_enabled", "user", user.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new MfaConfirmResponse(codes);
    }

    public Task DisableMfaAsync(MfaDisableRequest request, CancellationToken cancellationToken) =>
        InUserTransactionAsync(async ct => { await DisableMfaCoreAsync(request, ct).ConfigureAwait(false); return true; }, cancellationToken);

    private async Task DisableMfaCoreAsync(MfaDisableRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        var user = await LockCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!user.MfaEnabled)
        {
            throw AppException.Validation("mfa.not_enabled", "MFA is not enabled.");
        }

        if (await sessions.IsMfaRequiredByPolicyAsync(user.Id, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden("Your role requires MFA, so it cannot be turned off.");
        }

        var passwordOk = passwords.Verify(user.PasswordHash, request.Password ?? string.Empty).Valid;
        var codeOk = Totp.Verify(protector.Unprotect(user.MfaSecretProtected!, MfaSecretPurpose), request.Code, now, user.MfaLastUsedStep) is not null;
        if (!passwordOk || !codeOk)
        {
            await RecordFailureAsync(user, "mfa_disable_failed", now, cancellationToken).ConfigureAwait(false);
            throw new AppException(ErrorKind.Unauthorized, "invalid_credentials", "The password or code is not correct.");
        }

        user.DisableMfa();
        await db.MfaRecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        audit.Record("auth.mfa_disabled", "user", user.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<MeResponse> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken) =>
        InUserTransactionAsync(ct => ChangePasswordCoreAsync(request, ct), cancellationToken);

    private async Task<MeResponse> ChangePasswordCoreAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        var user = await LockCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!passwords.Verify(user.PasswordHash, request.CurrentPassword ?? string.Empty).Valid)
        {
            await RecordFailureAsync(user, "change_password_wrong_current", now, cancellationToken).ConfigureAwait(false);
            throw AppException.Validation("password.current_incorrect", "The current password is not correct.");
        }

        EnsurePasswordPolicy(request.NewPassword, user.Username);
        if (passwords.Verify(user.PasswordHash, request.NewPassword).Valid)
        {
            throw AppException.Validation("password.reused", "The new password must be different from the current one.");
        }

        user.SetPassword(passwords.Hash(request.NewPassword), now, mustChangePassword: false);
        var revoked = await RevokeSessionsAsync(user.Id, now, "password_changed", exceptSessionId: currentUser.SessionId, cancellationToken).ConfigureAwait(false);
        audit.Record("auth.password_changed", "user", user.Id, details: new { otherSessionsRevoked = revoked });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);

        var mfaSatisfied = await db.Sessions.Where(s => s.Id == currentUser.SessionId).Select(s => s.MfaSatisfied).FirstAsync(cancellationToken).ConfigureAwait(false);
        return await BuildMeAsync(user, mfaSatisfied, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes a manager-issued reset. The code travels in the request body, never in a URL.</summary>
    public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken) =>
        InUserTransactionAsync(async ct => { await ResetPasswordCoreAsync(request, ct).ConfigureAwait(false); return true; }, cancellationToken);

    private async Task ResetPasswordCoreAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        var invalid = AppException.Validation("reset.invalid", "The reset code is not valid or has expired. Ask your manager for a new one.");
        var tenantId = await ResolveTenantAsync(request.CompanyCode, cancellationToken).ConfigureAwait(false);
        var user = tenantId is null ? null : await FindUserForUpdateAsync(request.Username, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            throw invalid;
        }

        var hash = SecretTokens.HashHumanCode(request.ResetCode);
        var token = await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.TokenHash == hash)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (token is null || !token.IsUsable(now))
        {
            audit.Record("auth.password_reset_failed", "user", user.Id, actorUserId: user.Id);
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            throw invalid;
        }

        EnsurePasswordPolicy(request.NewPassword, user.Username);
        token.MarkUsed(now);
        user.SetPassword(passwords.Hash(request.NewPassword), now, mustChangePassword: false);
        var revoked = await RevokeSessionsAsync(user.Id, now, "password_reset", exceptSessionId: null, cancellationToken).ConfigureAwait(false);
        audit.Record("auth.password_reset_completed", "user", user.Id, details: new { issuedBy = token.IssuedByUserId, sessionsRevoked = revoked }, actorUserId: user.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == currentUser.UserId && s.RevokedAtUtc == null && s.IdleExpiresAtUtc > now && s.AbsoluteExpiresAtUtc > now)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(s => new SessionDto(s.Id, s.CreatedAtUtc, s.LastSeenAtUtc, s.IpAddress, s.UserAgent, s.Id == currentUser.SessionId)).ToList();
    }

    public async Task RevokeOwnSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == currentUser.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Session");
        session.Revoke(clock.GetUtcNow(), "revoked_by_user");
        audit.Record("auth.session_revoked", "session", session.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<int> RevokeSessionsAsync(Guid userId, DateTimeOffset now, string reason, Guid? exceptSessionId, CancellationToken cancellationToken)
    {
        var active = await db.Sessions.Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.Id != exceptSessionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in active)
        {
            session.Revoke(now, reason);
        }

        return active.Count;
    }

    internal static void EnsurePasswordPolicy(string? password, string username)
    {
        if (User.CheckPasswordPolicy(password ?? string.Empty, username) is { } problem)
        {
            throw AppException.Validation("password.policy", problem);
        }
    }

    private async Task<MeResponse> BuildMeAsync(User user, bool mfaSatisfied, CancellationToken cancellationToken)
    {
        var state = await sessions.ComputeStateAsync(user, mfaSatisfied, cancellationToken).ConfigureAwait(false);
        var mfaRequired = await sessions.IsMfaRequiredByPolicyAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var memberships = await MembershipReader.ReadAsync(db, user.Id, cancellationToken).ConfigureAwait(false);
        return new MeResponse(user.Id, user.Username, user.DisplayName, state, user.MfaEnabled, mfaRequired, memberships);
    }

    /// <summary>
    /// Runs a credential operation in a transaction in which the user's row is locked (SELECT ... FOR UPDATE).
    /// Concurrent attempts on the same account are therefore handled one after another, so parallel guessing
    /// cannot lose failed-attempt counts or dodge the lockout. Bookkeeping saved before an expected failure
    /// (failed-attempt count, audit) is committed before the failure is reported.
    /// </summary>
    private async Task<T> InUserTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await work(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (AppException)
        {
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Before sign-in, the tenant comes from the installation (in-store server) or the company code (cloud).
    /// It is applied to the connection so the user lookup below is confined to that tenant.
    /// </summary>
    private async Task<Guid?> ResolveTenantAsync(string? companyCode, CancellationToken cancellationToken)
    {
        var tenantId = tenants.Mode == DeploymentMode.Cloud
            ? await tenants.TenantByCodeAsync(companyCode, cancellationToken).ConfigureAwait(false)
            : await tenants.InstallationTenantAsync(cancellationToken).ConfigureAwait(false);
        if (tenantId is { } id)
        {
            await tenant.SetAsync(id, db, cancellationToken).ConfigureAwait(false);
        }

        return tenantId;
    }

    private async Task<User?> FindUserForUpdateAsync(string? username, CancellationToken cancellationToken)
    {
        string normalized;
        try
        {
            normalized = User.NormalizeUsername(username ?? string.Empty);
        }
        catch (DomainException)
        {
            return null;
        }

        var rows = await db.Users.FromSql($"SELECT *, xmin FROM users WHERE username = {normalized} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.SingleOrDefault();
    }

    private async Task<User> LockCurrentUserAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Users.FromSql($"SELECT *, xmin FROM users WHERE id = {currentUser.UserId} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Single();
    }

    private async Task<User> CurrentUserEntityAsync(CancellationToken cancellationToken) =>
        await db.Users.FirstAsync(u => u.Id == currentUser.UserId, cancellationToken).ConfigureAwait(false);

    private async Task RecordFailureAsync(User user, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var locked = user.RecordFailedLogin(now, Settings.MaxFailedLogins, TimeSpan.FromMinutes(Settings.LockoutMinutes));
        audit.Record("auth.login_failed", "user", user.Id, details: new { reason }, actorUserId: user.Id);
        if (locked)
        {
            audit.Record("auth.account_locked", "user", user.Id, details: new { until = user.LockedUntilUtc }, actorUserId: user.Id);
        }

        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryUseRecoveryCodeAsync(Guid userId, string? code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Count(char.IsLetterOrDigit) != 12)
        {
            return false;
        }

        var hash = SecretTokens.HashHumanCode(code);
        var match = await db.MfaRecoveryCodes.FirstOrDefaultAsync(c => c.UserId == userId && c.CodeHash == hash && c.UsedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        match?.MarkUsed(now);
        return match is not null;
    }

    private static AppException LockedOut(User user) => new(
        ErrorKind.Locked,
        "account_locked",
        $"This account is temporarily locked after too many failed attempts. Try again after {user.LockedUntilUtc:HH:mm} UTC or ask a manager to unlock it.");
}

/// <summary>Builds the per-business membership list (roles, stores and effective permissions) for a user.</summary>
internal static class MembershipReader
{
    public static async Task<IReadOnlyList<MembershipDto>> ReadAsync(SupermarketBillingDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await (
                from a in db.RoleAssignments.AsNoTracking()
                join b in db.Businesses.AsNoTracking() on a.BusinessId equals b.Id
                from s in db.Stores.AsNoTracking().Where(s => s.Id == a.StoreId).DefaultIfEmpty()
                where a.UserId == userId && a.RevokedAtUtc == null && b.IsActive
                orderby b.Code, a.RoleCode
                select new { a.Id, a.RoleCode, a.BusinessId, BusinessCode = b.Code, BusinessName = b.TradeName, a.StoreId, StoreName = s == null ? null : s.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.GroupBy(r => new { r.BusinessId, r.BusinessCode, r.BusinessName })
            .Select(g => new MembershipDto(
                g.Key.BusinessId,
                g.Key.BusinessCode,
                g.Key.BusinessName,
                g.Select(r => new RoleGrantDto(r.Id, r.RoleCode, Roles.Get(r.RoleCode).Name, r.StoreId, r.StoreName)).ToList(),
                g.SelectMany(r => Roles.Get(r.RoleCode).Permissions).Distinct().Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
