using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Identity;

public sealed record CreateUserResponse(UserDto User, GrantRoleResponse Role);

internal sealed record RoleGrantPayload(Guid UserId, string RoleCode, Guid BusinessId, Guid? StoreId);

/// <summary>User administration within a business, with privilege-escalation and maker-checker controls.</summary>
public sealed class UserAdminService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    AuthService auth,
    PasswordHashing passwords,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public const string RoleGrantApprovalType = "role.grant";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<RoleDto> ListRoles() =>
        Roles.All.Select(r => new RoleDto(r.Code, r.Name, r.IsPrivileged, r.BusinessWideOnly, r.Permissions.Order(StringComparer.Ordinal).ToList())).ToList();

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var actorGrants = await ActorGrantsAsync(cancellationToken).ConfigureAwait(false);
        var businessWide = AccessControl.Covers(actorGrants, Permissions.UsersView, businessId, null);
        var visibleStores = actorGrants.Where(g => g.BusinessId == businessId && g.StoreId is not null && Roles.Get(g.RoleCode).Permissions.Contains(Permissions.UsersView))
            .Select(g => g.StoreId!.Value).ToHashSet();
        if (!businessWide && visibleStores.Count == 0)
        {
            await organisation.RequireAsync(Permissions.UsersView, businessId, null, cancellationToken).ConfigureAwait(false);
        }

        var userIds = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.RevokedAtUtc == null && (businessWide || (a.StoreId != null && visibleStores.Contains(a.StoreId.Value))))
            .Select(a => a.UserId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);

        // People whose first (privileged) role is still awaiting approval have no active role yet, but they
        // belong on this list so managers can see them.
        var pending = (await PendingGrantsAsync(businessId, cancellationToken).ConfigureAwait(false))
            .Where(p => businessWide || (p.StoreId is { } s && visibleStores.Contains(s)))
            .ToList();
        userIds = [.. userIds.Union(pending.Select(p => p.UserId))];

        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).OrderBy(u => u.Username)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<UserDto>(users.Count);
        foreach (var user in users)
        {
            result.Add(await ToDtoAsync(user, businessId, cancellationToken, pending).ConfigureAwait(false));
        }

        return result;
    }

    private async Task<List<RoleGrantPayload>> PendingGrantsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var payloads = await db.ApprovalRequests.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.Type == RoleGrantApprovalType && a.Status == ApprovalStatus.Pending && a.ExpiresAtUtc > now)
            .Select(a => a.PayloadJson)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return payloads.Select(p => JsonSerializer.Deserialize<RoleGrantPayload>(p, Json)!).ToList();
    }

    public async Task<CreateUserResponse> CreateUserAsync(Guid businessId, CreateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var role = Roles.Get(request.RoleCode);
        var actorGrants = await ActorGrantsAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStoreInBusinessAsync(businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        if (!AccessControl.Covers(actorGrants, Permissions.UsersManage, businessId, request.StoreId)
            || !GrantPolicy.CanGrant(actorGrants, Permissions.RolesAssign, role, businessId, request.StoreId))
        {
            await organisation.RequireAsync(Permissions.UsersManage, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
            throw AppException.Forbidden($"You cannot create a user with the {role.Name} role.");
        }

        var username = User.NormalizeUsername(request.Username);
        AuthService.EnsurePasswordPolicy(request.TemporaryPassword, username);
        var now = clock.GetUtcNow();
        var user = User.Create(username, request.DisplayName, passwords.Hash(request.TemporaryPassword), now, mustChangePassword: true);
        db.Users.Add(user);
        audit.Record("user.created", "user", user.Id, businessId, request.StoreId, details: new { user.Username, user.DisplayName });

        var outcome = await GrantOrRequestAsync(user.Id, user.Username, role, businessId, request.StoreId, reason: "New user", now, cancellationToken)
            .ConfigureAwait(false);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new CreateUserResponse(await ToDtoAsync(user, businessId, cancellationToken).ConfigureAwait(false), outcome);
    }

    public async Task<GrantRoleResponse> GrantRoleAsync(Guid businessId, Guid userId, GrantRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var role = Roles.Get(request.RoleCode);
        var actorGrants = await ActorGrantsAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStoreInBusinessAsync(businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        if (!GrantPolicy.CanGrant(actorGrants, Permissions.RolesAssign, role, businessId, request.StoreId))
        {
            await organisation.RequireAsync(Permissions.RolesAssign, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
            throw AppException.Forbidden($"You cannot grant the {role.Name} role.");
        }

        if (userId == currentUser.UserId)
        {
            throw AppException.Forbidden("You cannot change your own roles.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("User");
        var outcome = await GrantOrRequestAsync(user.Id, user.Username, role, businessId, request.StoreId, request.Reason, clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    public async Task RevokeRoleAsync(Guid businessId, Guid userId, Guid assignmentId, CancellationToken cancellationToken)
    {
        var assignment = await db.RoleAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.UserId == userId && a.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        var actorGrants = await ActorGrantsAsync(cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            await organisation.RequireAsync(Permissions.RolesAssign, businessId, null, cancellationToken).ConfigureAwait(false);
            throw AppException.NotFound("Role assignment");
        }

        if (!GrantPolicy.CanGrant(actorGrants, Permissions.RolesAssign, Roles.Get(assignment.RoleCode), businessId, assignment.StoreId))
        {
            await organisation.RequireAsync(Permissions.RolesAssign, businessId, assignment.StoreId, cancellationToken).ConfigureAwait(false);
            throw AppException.Forbidden("You cannot revoke this role.");
        }

        if (userId == currentUser.UserId)
        {
            throw AppException.Forbidden("You cannot change your own roles.");
        }

        if (assignment.RoleCode == Roles.Owner)
        {
            await EnsureAnotherOwnerAsync(businessId, userId, cancellationToken).ConfigureAwait(false);
        }

        assignment.Revoke(currentUser.UserId, clock.GetUtcNow());
        audit.Record("role.revoked", "role_assignment", assignment.Id, businessId, assignment.StoreId, details: new { userId, role = assignment.RoleCode });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserDto> SetActiveAsync(Guid businessId, Guid userId, bool isActive, CancellationToken cancellationToken)
    {
        var user = await LoadManageableUserAsync(businessId, userId, Permissions.UsersManage, cancellationToken).ConfigureAwait(false);
        if (userId == currentUser.UserId)
        {
            throw AppException.Forbidden("You cannot disable your own account.");
        }

        var now = clock.GetUtcNow();
        if (!isActive)
        {
            foreach (var ownerBusiness in await db.RoleAssignments.Where(a => a.UserId == userId && a.RoleCode == Roles.Owner && a.RevokedAtUtc == null)
                         .Select(a => a.BusinessId).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                await EnsureAnotherOwnerAsync(ownerBusiness, userId, cancellationToken).ConfigureAwait(false);
            }

            await auth.RevokeSessionsAsync(userId, now, "user_disabled", null, cancellationToken).ConfigureAwait(false);
        }

        user.SetActive(isActive);
        audit.Record(isActive ? "user.enabled" : "user.disabled", "user", user.Id, businessId);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ToDtoAsync(user, businessId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserDto> UnlockAsync(Guid businessId, Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadManageableUserAsync(businessId, userId, Permissions.UsersUnlock, cancellationToken).ConfigureAwait(false);
        user.Unlock();
        audit.Record("user.unlocked", "user", user.Id, businessId);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ToDtoAsync(user, businessId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Issues a one-time reset code, shown once to the manager. Only its hash is stored.</summary>
    public async Task<PasswordResetIssuedResponse> IssuePasswordResetAsync(Guid businessId, Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadManageableUserAsync(businessId, userId, Permissions.UsersManage, cancellationToken).ConfigureAwait(false);
        if (userId == currentUser.UserId)
        {
            throw AppException.Validation("reset.self", "Use 'Change password' for your own account.");
        }

        var now = clock.GetUtcNow();
        foreach (var previous in await db.PasswordResetTokens.Where(t => t.UserId == userId && t.UsedAtUtc == null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            previous.MarkUsed(now);
        }

        var code = SecretTokens.NewHumanCode();
        var token = PasswordResetToken.Issue(userId, SecretTokens.HashHumanCode(code), currentUser.UserId, now, TimeSpan.FromMinutes(options.Value.PasswordResetMinutes));
        db.PasswordResetTokens.Add(token);
        audit.Record("user.password_reset_issued", "user", user.Id, businessId, details: new { expires = token.ExpiresAtUtc });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new PasswordResetIssuedResponse(code, token.ExpiresAtUtc);
    }

    /// <summary>
    /// Turns off two-step verification for someone who has lost their phone and recovery codes, and signs them out.
    /// If their role requires MFA they are made to enrol again at their next sign-in.
    /// </summary>
    public async Task<UserDto> ResetMfaAsync(Guid businessId, Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadManageableUserAsync(businessId, userId, Permissions.UsersManage, cancellationToken).ConfigureAwait(false);
        if (userId == currentUser.UserId)
        {
            throw AppException.Validation("mfa.reset_self", "Use 'My account' to change your own two-step verification.");
        }

        if (!user.MfaEnabled && user.MfaPendingSecretProtected is null)
        {
            throw AppException.Validation("mfa.not_enabled", "Two-step verification is not turned on for this user.");
        }

        var now = clock.GetUtcNow();
        user.DisableMfa();
        await db.MfaRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var revoked = await auth.RevokeSessionsAsync(userId, now, "mfa_reset", null, cancellationToken).ConfigureAwait(false);
        audit.Record("user.mfa_reset", "user", user.Id, businessId, details: new { sessionsRevoked = revoked });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ToDtoAsync(user, businessId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RevokeSessionsAsync(Guid businessId, Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadManageableUserAsync(businessId, userId, Permissions.UsersManage, cancellationToken).ConfigureAwait(false);
        var count = await auth.RevokeSessionsAsync(user.Id, clock.GetUtcNow(), "revoked_by_admin", null, cancellationToken).ConfigureAwait(false);
        audit.Record("user.sessions_revoked", "user", user.Id, businessId, details: new { count });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>
    /// Applies non-privileged grants immediately. Privileged grants become an approval request for a different
    /// authorised person, unless nobody else in the business could approve it (a single-owner shop), in which case
    /// the grant is applied and the waiver is recorded in the audit trail.
    /// </summary>
    private async Task<GrantRoleResponse> GrantOrRequestAsync(
        Guid userId, string username, RoleDefinition role, Guid businessId, Guid? storeId, string? reason,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (role.IsPrivileged)
        {
            var approverExists = await ApprovalService.AnyEligibleApproverAsync(db, businessId, storeId, role, excludeUserIds: [currentUser.UserId, userId], cancellationToken)
                .ConfigureAwait(false);
            if (approverExists)
            {
                var payload = JsonSerializer.Serialize(new RoleGrantPayload(userId, role.Code, businessId, storeId), Json);
                var request = ApprovalRequest.Create(
                    businessId, RoleGrantApprovalType, $"Grant {role.Name} to {username}", payload, reason, currentUser.UserId, now,
                    TimeSpan.FromDays(options.Value.ApprovalLifetimeDays));
                db.ApprovalRequests.Add(request);
                audit.Record("approval.requested", "approval_request", request.Id, businessId, storeId, details: new { request.Type, request.Summary, reason });
                return new GrantRoleResponse("pending_approval", null, request.Id, $"{role.Name} is a privileged role. The request is waiting for another authorised person to approve it.");
            }
        }

        var grant = RoleAssignment.Grant(userId, role.Code, businessId, storeId, currentUser.UserId, null, now);
        db.RoleAssignments.Add(grant);
        var waived = role.IsPrivileged;
        audit.Record("role.granted", "role_assignment", grant.Id, businessId, storeId,
            details: new { user = username, role = role.Code, approval = waived ? "waived_no_other_approver" : "not_required" });
        return new GrantRoleResponse("granted", grant.Id, null,
            waived ? $"{role.Name} granted. No other person could approve it, so this was recorded as a waived approval." : $"{role.Name} granted.");
    }

    private async Task<User> LoadManageableUserAsync(Guid businessId, Guid userId, string permission, CancellationToken cancellationToken)
    {
        var targetGrants = await AccessControl.LoadGrantsAsync(db, userId, cancellationToken).ConfigureAwait(false);
        var actorGrants = await ActorGrantsAsync(cancellationToken).ConfigureAwait(false);
        if (targetGrants.All(g => g.BusinessId != businessId))
        {
            await organisation.RequireAsync(Permissions.UsersView, businessId, null, cancellationToken).ConfigureAwait(false);
            throw AppException.NotFound("User");
        }

        if (!GrantPolicy.CanManage(actorGrants, targetGrants, permission))
        {
            await organisation.RequireAsync(Permissions.UsersView, businessId, null, cancellationToken).ConfigureAwait(false);
            throw AppException.Forbidden("You cannot manage this user: they hold roles beyond your own permissions or in places you do not manage.");
        }

        return await db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStoreInBusinessAsync(Guid businessId, Guid? storeId, CancellationToken cancellationToken)
    {
        if (storeId is { } id && !await db.Stores.AnyAsync(s => s.Id == id && s.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Store");
        }
    }

    private async Task EnsureAnotherOwnerAsync(Guid businessId, Guid excludingUserId, CancellationToken cancellationToken)
    {
        var others = await db.RoleAssignments
            .Where(a => a.BusinessId == businessId && a.RoleCode == Roles.Owner && a.RevokedAtUtc == null && a.UserId != excludingUserId)
            .Join(db.Users.Where(u => u.IsActive), a => a.UserId, u => u.Id, (a, _) => a)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (!others)
        {
            throw AppException.Conflict("owner.last", "This is the business's last active owner. Add another owner first.");
        }
    }

    private Task<List<ActiveGrant>> ActorGrantsAsync(CancellationToken cancellationToken) =>
        AccessControl.LoadGrantsAsync(db, currentUser.UserId, cancellationToken);

    private async Task<UserDto> ToDtoAsync(User user, Guid businessId, CancellationToken cancellationToken, IReadOnlyList<RoleGrantPayload>? pending = null)
    {
        pending ??= await PendingGrantsAsync(businessId, cancellationToken).ConfigureAwait(false);
        var pendingRoles = pending.Where(p => p.UserId == user.Id).Select(p => Roles.Get(p.RoleCode).Name).Distinct().Order(StringComparer.Ordinal).ToList();
        var roles = await (
                from a in db.RoleAssignments.AsNoTracking()
                from s in db.Stores.AsNoTracking().Where(s => s.Id == a.StoreId).DefaultIfEmpty()
                where a.UserId == user.Id && a.BusinessId == businessId && a.RevokedAtUtc == null
                orderby a.RoleCode
                select new { a.Id, a.RoleCode, a.StoreId, StoreName = s == null ? null : s.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new UserDto(
            user.Id, user.Username, user.DisplayName, user.IsActive, user.IsLockedOut(clock.GetUtcNow()), user.MfaEnabled, user.MustChangePassword,
            user.LastLoginAtUtc, roles.Select(r => new RoleGrantDto(r.Id, r.RoleCode, Roles.Get(r.RoleCode).Name, r.StoreId, r.StoreName)).ToList(),
            pendingRoles);
    }
}
