using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Approvals;

public static class ApprovalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

/// <summary>
/// Maker-checker request: one person asks, a different authorised person decides. The database also enforces
/// that the decider is not the requester. Approved requests are applied in the same transaction as the decision.
/// </summary>
public sealed class ApprovalRequest : ITenantOwned
{
    private ApprovalRequest()
    {
        Type = Summary = PayloadJson = Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    /// <summary>What is being approved, for example <c>role.grant</c>.</summary>
    public string Type { get; private set; }

    /// <summary>Human-readable description shown to the approver.</summary>
    public string Summary { get; private set; }

    /// <summary>Everything needed to apply the change once approved.</summary>
    public string PayloadJson { get; private set; }

    public string? Reason { get; private set; }

    public string Status { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    public string? DecisionNote { get; private set; }

    public uint RowVersion { get; private set; }

    public static ApprovalRequest Create(
        Guid businessId, string type, string summary, string payloadJson, string? reason, Guid requestedBy, DateTimeOffset now, TimeSpan lifetime)
    {
        return new ApprovalRequest
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Type = type,
            Summary = summary,
            PayloadJson = payloadJson,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Status = ApprovalStatus.Pending,
            RequestedByUserId = requestedBy,
            RequestedAtUtc = now,
            ExpiresAtUtc = now + lifetime,
        };
    }

    public void Approve(Guid decidedBy, DateTimeOffset now, string? note) => Decide(ApprovalStatus.Approved, decidedBy, now, note);

    public void Reject(Guid decidedBy, DateTimeOffset now, string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new DomainException("approval.reason_required", "A reason is required to reject a request.");
        }

        Decide(ApprovalStatus.Rejected, decidedBy, now, note);
    }

    public void Cancel(Guid cancelledBy, DateTimeOffset now)
    {
        if (cancelledBy != RequestedByUserId)
        {
            throw new DomainException("approval.cancel_not_requester", "Only the requester can cancel a request.");
        }

        EnsurePending(now);
        Status = ApprovalStatus.Cancelled;
        DecidedAtUtc = now;
    }

    private void Decide(string status, Guid decidedBy, DateTimeOffset now, string? note)
    {
        if (decidedBy == RequestedByUserId)
        {
            throw new DomainException("approval.self_decision", "You cannot approve or reject your own request. Another authorised person must decide.");
        }

        EnsurePending(now);
        Status = status;
        DecidedByUserId = decidedBy;
        DecidedAtUtc = now;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    private void EnsurePending(DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            throw new DomainException("approval.not_pending", $"This request is already {Status}.");
        }

        if (now >= ExpiresAtUtc)
        {
            throw new DomainException("approval.expired", "This request has expired. Submit a new one.");
        }
    }
}
