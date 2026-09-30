using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Identity;

public sealed record AuthenticatedSession(Guid SessionId, Guid UserId, string Username, string State);

/// <summary>Validates session cookies on every request and computes what the session may do.</summary>
public sealed class SessionService(
    SupermarketBillingDbContext db, TenantContext tenant, TenantResolver tenants, IOptions<SecurityOptions> options, TimeProvider clock)
{
    private static readonly string[] PrivilegedRoles = Roles.All.Where(r => r.IsPrivileged).Select(r => r.Code).ToArray();
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    public async Task<AuthenticatedSession?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = SecretTokens.Hash(token);
        var now = clock.GetUtcNow();

        // Sessions are protected by row-level security, so the tenant is looked up first (through a narrowly
        // scoped database function) and then every following query runs inside that tenant.
        if (await tenants.TenantBySessionAsync(hash, cancellationToken).ConfigureAwait(false) is not { } tenantId)
        {
            return null;
        }

        await tenant.SetAsync(tenantId, db, cancellationToken).ConfigureAwait(false);
        var row = await db.Sessions.AsNoTracking()
            .Where(s => s.TokenHash == hash)
            .Join(db.Users.AsNoTracking(), s => s.UserId, u => u.Id, (s, u) => new { Session = s, User = u })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row is null || !row.Session.IsValid(now) || !row.User.IsActive)
        {
            return null;
        }

        // Sliding idle timeout, written at most once a minute per session to limit database writes.
        if (now - row.Session.LastSeenAtUtc >= LastSeenResolution)
        {
            var idleExpiry = Min(now + TimeSpan.FromMinutes(options.Value.SessionIdleMinutes), row.Session.AbsoluteExpiresAtUtc);
            await db.Sessions.Where(s => s.Id == row.Session.Id && s.RevokedAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastSeenAtUtc, now).SetProperty(s => s.IdleExpiresAtUtc, idleExpiry), cancellationToken)
                .ConfigureAwait(false);
        }

        var state = await ComputeStateAsync(row.User, row.Session.MfaSatisfied, cancellationToken).ConfigureAwait(false);
        return new AuthenticatedSession(row.Session.Id, row.User.Id, row.User.Username, state);
    }

    public async Task<bool> ValidateCsrfAsync(Guid sessionId, string? csrfToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(csrfToken) || csrfToken.Length > 100)
        {
            return false;
        }

        var expected = await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.CsrfTokenHash)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return expected is not null && SecretTokens.FixedTimeEquals(expected, SecretTokens.Hash(csrfToken));
    }

    /// <summary>
    /// Order matters: the second factor is checked before anything else, then a forced password change, then
    /// mandatory MFA enrolment. Only <see cref="SessionStates.Active"/> sessions may use the application.
    /// </summary>
    public async Task<string> ComputeStateAsync(User user, bool mfaSatisfied, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.MfaEnabled && !mfaSatisfied)
        {
            return SessionStates.MfaRequired;
        }

        if (user.MustChangePassword)
        {
            return SessionStates.PasswordChangeRequired;
        }

        if (!user.MfaEnabled && await IsMfaRequiredByPolicyAsync(user.Id, cancellationToken).ConfigureAwait(false))
        {
            return SessionStates.MfaEnrolmentRequired;
        }

        return SessionStates.Active;
    }

    /// <summary>True when the user holds a privileged role in any business that requires MFA for privileged users.</summary>
    public Task<bool> IsMfaRequiredByPolicyAsync(Guid userId, CancellationToken cancellationToken) =>
        db.RoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.RevokedAtUtc == null && PrivilegedRoles.Contains(a.RoleCode))
            .Join(db.Businesses.Where(b => b.IsActive && b.RequireMfaForPrivilegedUsers), a => a.BusinessId, b => b.Id, (a, _) => a)
            .AnyAsync(cancellationToken);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
