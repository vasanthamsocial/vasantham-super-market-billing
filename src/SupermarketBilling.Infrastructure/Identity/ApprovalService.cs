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
/// One kind of change that needs maker-checker approval: who may decide it, and what approval (or its absence) does.
/// The decision and the change it authorises are saved in the same transaction.
/// </summary>
public interface IApprovalHandler
{
    string Type { get; }

    /// <summary>Whether this person may decide the request (never the requester; checked separately as well).</summary>
    bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, Guid actorUserId, ApprovalRequest request);

    Task ApplyAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Called when the request is rejected or cancelled, so pending changes are closed off.</summary>
    Task ClosedWithoutApprovalAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Maker-checker queue. Each request type has a handler that decides eligibility and applies the change.</summary>
public sealed class ApprovalService
{
    private readonly SupermarketBillingDbContext _db;
    private readonly OrganisationService _organisation;
    private readonly AuditRecorder _audit;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, IApprovalHandler> _handlers;

    public ApprovalService(
        SupermarketBillingDbContext db, OrganisationService organisation, AuditRecorder audit, ICurrentUser currentUser, TimeProvider clock,
        IEnumerable<IApprovalHandler> handlers)
    {
        _db = db;
        _organisation = organisation;
        _audit = audit;
        _currentUser = currentUser;
        _clock = clock;
        _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<ApprovalDto>> ListAsync(Guid businessId, string? status, CancellationToken cancellationToken)
    {
        await _organisation.RequireAsync(Permissions.ApprovalsView, businessId, null, cancellationToken).ConfigureAwait(false);
        var actorGrants = await AccessControl.LoadGrantsAsync(_db, _currentUser.UserId, cancellationToken).ConfigureAwait(false);
        var query = _db.ApprovalRequests.AsNoTracking().Where(a => a.BusinessId == businessId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(a => a.Status == status);
        }

        var rows = await (
                from a in query
                join requester in _db.Users.AsNoTracking() on a.RequestedByUserId equals requester.Id
                from decider in _db.Users.AsNoTracking().Where(u => u.Id == a.DecidedByUserId).DefaultIfEmpty()
                orderby a.RequestedAtUtc descending
                select new { Request = a, RequestedBy = requester.DisplayName, DecidedBy = decider == null ? null : decider.DisplayName })
            .Take(200)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = _clock.GetUtcNow();
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
        // The decision and the change it authorises commit together, or not at all (a handler may save in steps).
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var request = await LoadForDecisionAsync(approvalId, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        request.Approve(_currentUser.UserId, now, note);
        _audit.Record("approval.approved", "approval_request", request.Id, request.BusinessId, details: new { request.Type, request.Summary, note });
        await Handler(request).ApplyAsync(request, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectAsync(Guid approvalId, string? note, CancellationToken cancellationToken)
    {
        var request = await LoadForDecisionAsync(approvalId, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        request.Reject(_currentUser.UserId, now, note);
        _audit.Record("approval.rejected", "approval_request", request.Id, request.BusinessId, details: new { request.Type, request.Summary, note });
        await Handler(request).ClosedWithoutApprovalAsync(request, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        var request = await _db.ApprovalRequests.FirstOrDefaultAsync(a => a.Id == approvalId && a.RequestedByUserId == _currentUser.UserId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Approval request");
        var now = _clock.GetUtcNow();
        request.Cancel(_currentUser.UserId, now);
        _audit.Record("approval.cancelled", "approval_request", request.Id, request.BusinessId);
        await Handler(request).ClosedWithoutApprovalAsync(request, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Is there anyone, other than the excluded people, who could approve a role grant?</summary>
    internal static async Task<bool> AnyEligibleApproverAsync(
        SupermarketBillingDbContext db, Guid businessId, Guid? storeId, RoleDefinition role, IReadOnlyCollection<Guid> excludeUserIds, CancellationToken cancellationToken) =>
        (await OtherUsersGrantsAsync(db, businessId, excludeUserIds, cancellationToken).ConfigureAwait(false))
            .Any(g => RoleGrantApprovalHandler.IsEligible(g, businessId, storeId, role));

    /// <summary>Active grants in the business, grouped by user, excluding the given people.</summary>
    internal static async Task<List<List<ActiveGrant>>> OtherUsersGrantsAsync(
        SupermarketBillingDbContext db, Guid businessId, IReadOnlyCollection<Guid> excludeUserIds, CancellationToken cancellationToken)
    {
        var rows = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.RevokedAtUtc == null && !excludeUserIds.Contains(a.UserId))
            .Join(db.Users.Where(u => u.IsActive), a => a.UserId, u => u.Id, (a, _) => new { a.UserId, a.Id, a.RoleCode, a.BusinessId, a.StoreId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(r => r.UserId).Select(g => g.Select(r => new ActiveGrant(r.Id, r.RoleCode, r.BusinessId, r.StoreId)).ToList()).ToList();
    }

    private IApprovalHandler Handler(ApprovalRequest request) =>
        _handlers.TryGetValue(request.Type, out var handler)
            ? handler
            : throw AppException.Validation("approval.unknown_type", $"No handler for approval type '{request.Type}'.");

    private bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, ApprovalRequest request) =>
        request.RequestedByUserId != _currentUser.UserId && Handler(request).CanDecide(actorGrants, _currentUser.UserId, request);

    private async Task<ApprovalRequest> LoadForDecisionAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        var request = await _db.ApprovalRequests.FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Approval request");

        await _organisation.RequireAsync(Permissions.ApprovalsDecide, request.BusinessId, null, cancellationToken).ConfigureAwait(false);
        var actorGrants = await AccessControl.LoadGrantsAsync(_db, _currentUser.UserId, cancellationToken).ConfigureAwait(false);
        if (request.RequestedByUserId == _currentUser.UserId)
        {
            throw AppException.Forbidden("You cannot approve or reject your own request. Another authorised person must decide.");
        }

        if (!CanDecide(actorGrants, request))
        {
            throw AppException.Forbidden("You are not allowed to decide this request (it needs a permission you do not hold, or it benefits you).");
        }

        return request;
    }
}

/// <summary>Granting a privileged role.</summary>
internal sealed class RoleGrantApprovalHandler(SupermarketBillingDbContext db, AuditRecorder audit) : IApprovalHandler
{
    public string Type => UserAdminService.RoleGrantApprovalType;

    /// <summary>The decider holds approvals.decide business-wide and every permission the role gives.</summary>
    internal static bool IsEligible(IReadOnlyCollection<ActiveGrant> grants, Guid businessId, Guid? storeId, RoleDefinition role) =>
        AccessControl.Covers(grants, Permissions.ApprovalsDecide, businessId, null)
        && role.Permissions.IsSubsetOf(GrantPolicy.PermissionsAt(grants, businessId, storeId));

    public bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, Guid actorUserId, ApprovalRequest request)
    {
        var payload = Payload(request);
        return payload.UserId != actorUserId && IsEligible(actorGrants, request.BusinessId, payload.StoreId, Roles.Get(payload.RoleCode));
    }

    public async Task ApplyAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = Payload(request);
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

    public Task ClosedWithoutApprovalAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;

    private static RoleGrantPayload Payload(ApprovalRequest request) =>
        JsonSerializer.Deserialize<RoleGrantPayload>(request.PayloadJson, UserAdminService.Json)
        ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");
}
