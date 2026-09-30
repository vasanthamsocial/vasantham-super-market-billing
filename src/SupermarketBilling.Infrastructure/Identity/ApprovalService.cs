using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Identity;

/// <summary>
/// Maker-checker queue. The decider must hold approvals.decide business-wide, must not be the requester or the
/// person who benefits, and must themselves hold every permission the change would grant.
/// </summary>
public sealed class ApprovalService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<ApprovalDto>> ListAsync(Guid businessId, string? status, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.ApprovalsView, businessId, null, cancellationToken).ConfigureAwait(false);
        var actorGrants = await AccessControl.LoadGrantsAsync(db, currentUser.UserId, cancellationToken).ConfigureAwait(false);
        var query = db.ApprovalRequests.AsNoTracking().Where(a => a.BusinessId == businessId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(a => a.Status == status);
        }

        var rows = await (
                from a in query
                join requester in db.Users.AsNoTracking() on a.RequestedByUserId equals requester.Id
                from decider in db.Users.AsNoTracking().Where(u => u.Id == a.DecidedByUserId).DefaultIfEmpty()
                orderby a.RequestedAtUtc descending
                select new { Request = a, RequestedBy = requester.DisplayName, DecidedBy = decider == null ? null : decider.DisplayName })
            .Take(200)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        return rows.Select(r =>
        {
            var a = r.Request;
            var effectiveStatus = a.Status == ApprovalStatus.Pending && now >= a.ExpiresAtUtc ? ApprovalStatus.Expired : a.Status;
            var canDecide = effectiveStatus == ApprovalStatus.Pending && CanDecide(actorGrants, a);
            return new ApprovalDto(
                a.Id, a.BusinessId, a.Type, a.Summary, a.Reason, effectiveStatus, a.RequestedByUserId, r.RequestedBy, a.RequestedAtUtc,
                a.ExpiresAtUtc, a.DecidedByUserId, r.DecidedBy, a.DecidedAtUtc, a.DecisionNote, canDecide);
        }).ToList();
    }

    public async Task ApproveAsync(Guid approvalId, string? note, CancellationToken cancellationToken)
    {
        var (request, _) = await LoadForDecisionAsync(approvalId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        request.Approve(currentUser.UserId, now, note);
        audit.Record("approval.approved", "approval_request", request.Id, request.BusinessId, details: new { request.Type, request.Summary, note });

        switch (request.Type)
        {
            case UserAdminService.RoleGrantApprovalType:
                await ApplyRoleGrantAsync(request, now, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw AppException.Validation("approval.unknown_type", $"No handler for approval type '{request.Type}'.");
        }

        // The decision and the change it authorises commit together, or not at all.
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectAsync(Guid approvalId, string? note, CancellationToken cancellationToken)
    {
        var (request, _) = await LoadForDecisionAsync(approvalId, cancellationToken).ConfigureAwait(false);
        request.Reject(currentUser.UserId, clock.GetUtcNow(), note);
        audit.Record("approval.rejected", "approval_request", request.Id, request.BusinessId, details: new { request.Type, request.Summary, note });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        var request = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.Id == approvalId && a.RequestedByUserId == currentUser.UserId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Approval request");
        request.Cancel(currentUser.UserId, clock.GetUtcNow());
        audit.Record("approval.cancelled", "approval_request", request.Id, request.BusinessId);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Is there anyone, other than the excluded people, who could approve this grant?</summary>
    internal static async Task<bool> AnyEligibleApproverAsync(
        SupermarketBillingDbContext db, Guid businessId, Guid? storeId, RoleDefinition role, IReadOnlyCollection<Guid> excludeUserIds, CancellationToken cancellationToken)
    {
        var rows = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.RevokedAtUtc == null && !excludeUserIds.Contains(a.UserId))
            .Join(db.Users.Where(u => u.IsActive), a => a.UserId, u => u.Id, (a, _) => new { a.UserId, a.Id, a.RoleCode, a.BusinessId, a.StoreId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.GroupBy(r => r.UserId)
            .Any(g => IsEligible(g.Select(r => new ActiveGrant(r.Id, r.RoleCode, r.BusinessId, r.StoreId)).ToList(), businessId, storeId, role));
    }

    private static bool IsEligible(IReadOnlyCollection<ActiveGrant> grants, Guid businessId, Guid? storeId, RoleDefinition role) =>
        AccessControl.Covers(grants, Permissions.ApprovalsDecide, businessId, null)
        && role.Permissions.IsSubsetOf(GrantPolicy.PermissionsAt(grants, businessId, storeId));

    private bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, ApprovalRequest request)
    {
        if (request.RequestedByUserId == currentUser.UserId)
        {
            return false;
        }

        if (request.Type == UserAdminService.RoleGrantApprovalType)
        {
            var payload = JsonSerializer.Deserialize<RoleGrantPayload>(request.PayloadJson, UserAdminService.Json)!;
            return payload.UserId != currentUser.UserId && IsEligible(actorGrants, request.BusinessId, payload.StoreId, Roles.Get(payload.RoleCode));
        }

        return AccessControl.Covers(actorGrants, Permissions.ApprovalsDecide, request.BusinessId, null);
    }

    private async Task<(ApprovalRequest Request, List<ActiveGrant> ActorGrants)> LoadForDecisionAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        var request = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            throw AppException.NotFound("Approval request");
        }

        await organisation.RequireAsync(Permissions.ApprovalsDecide, request.BusinessId, null, cancellationToken).ConfigureAwait(false);
        var actorGrants = await AccessControl.LoadGrantsAsync(db, currentUser.UserId, cancellationToken).ConfigureAwait(false);
        if (request.RequestedByUserId == currentUser.UserId)
        {
            throw AppException.Forbidden("You cannot approve or reject your own request. Another authorised person must decide.");
        }

        if (!CanDecide(actorGrants, request))
        {
            throw AppException.Forbidden("You are not allowed to decide this request (it would grant more than your own permissions, or it benefits you).");
        }

        return (request, actorGrants);
    }

    private async Task ApplyRoleGrantAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<RoleGrantPayload>(request.PayloadJson, UserAdminService.Json)
            ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == payload.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            throw AppException.Conflict("approval.user_inactive", "The user no longer exists or is disabled. Reject this request instead.");
        }

        var grant = RoleAssignment.Grant(payload.UserId, payload.RoleCode, payload.BusinessId, payload.StoreId, request.RequestedByUserId, request.Id, now);
        db.RoleAssignments.Add(grant);
        audit.Record("role.granted", "role_assignment", grant.Id, payload.BusinessId, payload.StoreId,
            details: new { user = user.Username, role = payload.RoleCode, approval = request.Id });
    }
}
