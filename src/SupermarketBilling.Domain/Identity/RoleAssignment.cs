using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.Domain.Identity;

/// <summary>
/// Grants a role to a user within a business, optionally limited to one store. Assignments are never deleted:
/// revoking records who and when, preserving the history of who could do what.
/// </summary>
public sealed class RoleAssignment
{
    private RoleAssignment()
    {
        RoleCode = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string RoleCode { get; private set; }

    public Guid BusinessId { get; private set; }

    /// <summary>Null means the role applies to every store in the business.</summary>
    public Guid? StoreId { get; private set; }

    public Guid? GrantedByUserId { get; private set; }

    public DateTimeOffset GrantedAtUtc { get; private set; }

    /// <summary>The approval request that authorised this grant, when maker-checker applied.</summary>
    public Guid? ApprovalRequestId { get; private set; }

    public Guid? RevokedByUserId { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static RoleAssignment Grant(
        Guid userId, string roleCode, Guid businessId, Guid? storeId, Guid? grantedBy, Guid? approvalRequestId, DateTimeOffset now)
    {
        var role = Roles.Get(roleCode);
        if (role.BusinessWideOnly && storeId is not null)
        {
            throw new DomainException("role.business_wide_only", $"The {role.Name} role applies to the whole business and cannot be limited to one store.");
        }

        return new RoleAssignment
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            RoleCode = role.Code,
            BusinessId = businessId,
            StoreId = storeId,
            GrantedByUserId = grantedBy,
            GrantedAtUtc = now,
            ApprovalRequestId = approvalRequestId,
        };
    }

    public void Revoke(Guid revokedBy, DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new DomainException("role.already_revoked", "This role has already been revoked.");
        }

        RevokedByUserId = revokedBy;
        RevokedAtUtc = now;
    }
}
