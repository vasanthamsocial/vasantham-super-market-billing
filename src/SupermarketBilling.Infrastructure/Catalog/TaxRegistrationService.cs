using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Catalog;

internal sealed record TaxChangePayload(Guid BusinessId, string Mode, string? Gstin, DateOnly EffectiveFrom, string Reason, string? EvidenceReference);

/// <summary>
/// The legal tax-registration history of a business. A change is prepared by an accountant and approved by a
/// different person holding tax approval; it can only take effect today or later, and history is append-only.
/// </summary>
public sealed class TaxRegistrationService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public const string ApprovalType = "tax_registration.change";

    public async Task<IReadOnlyList<TaxRegistrationDto>> HistoryAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.StoresView, businessId, null, cancellationToken).ConfigureAwait(false);
        var rows = await (
                from r in db.TaxRegistrations.AsNoTracking()
                join u in db.Users.AsNoTracking() on r.RecordedByUserId equals u.Id
                where r.BusinessId == businessId
                orderby r.EffectiveFrom descending
                select new { Registration = r, RecordedBy = u.DisplayName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var current = TaxRegistration.InForce(rows.Select(r => r.Registration), BusinessCalendar.Today(clock));
        return rows.Select(r => ToDto(r.Registration, r.RecordedBy, r.Registration.Id == current?.Id)).ToList();
    }

    public async Task<TaxRegistrationChangeResponse> RequestChangeAsync(Guid businessId, TaxRegistrationChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.TaxReview, businessId, null, cancellationToken).ConfigureAwait(false);
        if (!request.BackupConfirmed)
        {
            throw AppException.Validation("tax_mode.backup_required", "Take and verify a backup before requesting a tax-registration change.");
        }

        var history = await db.TaxRegistrations.AsNoTracking().Where(r => r.BusinessId == businessId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var latest = history.MaxBy(r => r.EffectiveFrom)
            ?? throw AppException.Conflict("tax_mode.missing", "This business has no tax registration yet.");
        var today = BusinessCalendar.Today(clock);
        TaxRegistration.ValidateChange(latest, request.Mode, request.EffectiveFrom, today, request.Reason);
        if (request.Mode == latest.Mode && string.Equals(request.Gstin?.Trim(), latest.Gstin, StringComparison.OrdinalIgnoreCase))
        {
            throw AppException.Validation("tax_mode.no_change", "This is the same as the current registration.");
        }

        if (await db.ApprovalRequests.AnyAsync(a => a.BusinessId == businessId && a.Type == ApprovalType && a.Status == ApprovalStatus.Pending, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("tax_mode.pending", "A tax-registration change is already waiting for approval.");
        }

        // Validate the GSTIN rules now, so a request that could never be applied is refused up front.
        TaxRegistration.Initial(businessId, request.Mode, request.Gstin, request.EffectiveFrom, currentUser.UserId, clock.GetUtcNow());

        var payload = new TaxChangePayload(businessId, request.Mode, request.Gstin, request.EffectiveFrom, request.Reason.Trim(), request.EvidenceReference);
        var approval = ApprovalRequest.Create(
            businessId, ApprovalType, $"Change tax registration to {request.Mode} from {request.EffectiveFrom:dd-MMM-yyyy}",
            JsonSerializer.Serialize(payload, UserAdminService.Json), request.Reason, currentUser.UserId, clock.GetUtcNow(),
            TimeSpan.FromDays(options.Value.ApprovalLifetimeDays));
        db.ApprovalRequests.Add(approval);
        audit.Record("approval.requested", "approval_request", approval.Id, businessId,
            details: new { approval.Type, approval.Summary, request.EvidenceReference, backupConfirmed = true });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new TaxRegistrationChangeResponse(approval.Id, "The change is waiting for independent approval by an owner or manager.");
    }

    internal static TaxRegistrationDto ToDto(TaxRegistration r, string recordedBy, bool isCurrent) =>
        new(r.Id, r.Mode, r.EffectiveFrom, r.Gstin, r.Reason, r.EvidenceReference, recordedBy, r.ApprovalRequestId, r.RecordedAtUtc, isCurrent);
}

/// <summary>Independent approval of a tax-registration change: needs tax approval and approvals, business-wide.</summary>
internal sealed class TaxRegistrationApprovalHandler(SupermarketBillingDbContext db, AuditRecorder audit, TimeProvider clock) : IApprovalHandler
{
    public string Type => TaxRegistrationService.ApprovalType;

    public bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, Guid actorUserId, ApprovalRequest request) =>
        AccessControl.Covers(actorGrants, Permissions.ApprovalsDecide, request.BusinessId, null)
        && AccessControl.Covers(actorGrants, Permissions.TaxApprove, request.BusinessId, null);

    public async Task ApplyAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<TaxChangePayload>(request.PayloadJson, UserAdminService.Json)
            ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");
        var history = await db.TaxRegistrations.Where(r => r.BusinessId == payload.BusinessId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var latest = history.MaxBy(r => r.EffectiveFrom)!;
        var today = BusinessCalendar.Today(clock);
        if (payload.EffectiveFrom < today)
        {
            throw AppException.Conflict("tax_mode.effective_date_passed",
                "The requested effective date has already passed, so approving it now would change history. Reject it and submit a new request.");
        }

        var change = TaxRegistration.Change(latest, payload.Mode, payload.Gstin, payload.EffectiveFrom, today, payload.Reason, payload.EvidenceReference,
            request.RequestedByUserId, request.Id, now);
        db.TaxRegistrations.Add(change);
        audit.Record("tax_registration.changed", "tax_registration", change.Id, payload.BusinessId,
            details: new { from = latest.Mode, to = change.Mode, change.EffectiveFrom, change.Gstin, approval = request.Id });
    }

    public Task ClosedWithoutApprovalAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
}
