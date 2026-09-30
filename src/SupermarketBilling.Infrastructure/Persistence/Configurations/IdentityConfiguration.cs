using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Organisation;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", t =>
        {
            t.HasCheckConstraint("ck_users_username", "username ~ '^[a-z0-9][a-z0-9._-]{2,49}$'");
            t.HasCheckConstraint("ck_users_failed_login_count", "failed_login_count >= 0");
            t.HasCheckConstraint("ck_users_mfa_secret", "NOT mfa_enabled OR mfa_secret_protected IS NOT NULL");
        });
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.Username).HasMaxLength(50).IsRequired();
        builder.HasIndex(u => u.Username).IsUnique();
        builder.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();
        builder.Property(u => u.MfaSecretProtected).HasMaxLength(200);
        builder.Property(u => u.MfaPendingSecretProtected).HasMaxLength(200);
        builder.Property(u => u.RowVersion).IsRowVersion();
    }
}

internal sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        var roleList = string.Join(", ", Roles.Codes.Order(StringComparer.Ordinal).Select(c => $"'{c}'"));
        var businessWideRoles = string.Join(", ", Roles.All.Where(r => r.BusinessWideOnly).Select(r => $"'{r.Code}'").Order(StringComparer.Ordinal));
        builder.ToTable("role_assignments", t =>
        {
            t.HasCheckConstraint("ck_role_assignments_role_code", $"role_code IN ({roleList})");
            t.HasCheckConstraint("ck_role_assignments_business_wide", $"role_code NOT IN ({businessWideRoles}) OR store_id IS NULL");
            t.HasCheckConstraint("ck_role_assignments_revocation", "(revoked_at_utc IS NULL) = (revoked_by_user_id IS NULL)");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.RoleCode).HasMaxLength(40).IsRequired();
        builder.Ignore(a => a.IsActive);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Business>().WithMany().HasForeignKey(a => a.BusinessId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Store>().WithMany()
            .HasForeignKey(a => new { a.StoreId, a.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApprovalRequest>().WithMany().HasForeignKey(a => a.ApprovalRequestId).OnDelete(DeleteBehavior.Restrict);

        // At most one active grant of the same role at the same scope.
        builder.HasIndex(a => new { a.UserId, a.RoleCode, a.BusinessId, a.StoreId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("revoked_at_utc IS NULL")
            .HasDatabaseName("ux_role_assignments_active");
        builder.HasIndex(a => a.BusinessId);
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions", t =>
        {
            t.HasCheckConstraint("ck_sessions_token_hash", "octet_length(token_hash) = 32 AND octet_length(csrf_token_hash) = 32");
            t.HasCheckConstraint("ck_sessions_expiry", "idle_expires_at_utc > created_at_utc AND absolute_expires_at_utc > created_at_utc");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasIndex(s => s.TokenHash).IsUnique();
        builder.HasIndex(s => s.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.IpAddress).HasMaxLength(64);
        builder.Property(s => s.UserAgent).HasMaxLength(300);
        builder.Property(s => s.RevokedReason).HasMaxLength(50);
    }
}

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("password_reset_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.IssuedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MfaRecoveryCodeConfiguration : IEntityTypeConfiguration<MfaRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MfaRecoveryCode> builder)
    {
        builder.ToTable("mfa_recovery_codes");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => new { c.UserId, c.CodeHash }).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.ToTable("approval_requests", t =>
        {
            t.HasCheckConstraint("ck_approval_requests_status", "status IN ('pending', 'approved', 'rejected', 'cancelled', 'expired')");

            // Maker-checker enforced by the database: nobody can decide their own request.
            t.HasCheckConstraint("ck_approval_requests_maker_checker", "decided_by_user_id IS NULL OR decided_by_user_id <> requested_by_user_id");
            t.HasCheckConstraint(
                "ck_approval_requests_decision",
                "(status IN ('approved', 'rejected')) = (decided_by_user_id IS NOT NULL AND decided_at_utc IS NOT NULL)");
            t.HasCheckConstraint("ck_approval_requests_payload", "jsonb_typeof(payload_json) = 'object'");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Type).HasMaxLength(50).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(500).IsRequired();
        builder.Property(a => a.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(500);
        builder.Property(a => a.Status).HasMaxLength(20).IsRequired();
        builder.Property(a => a.DecisionNote).HasMaxLength(500);
        builder.Property(a => a.RowVersion).IsRowVersion();
        builder.HasOne<Business>().WithMany().HasForeignKey(a => a.BusinessId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.BusinessId, a.Status });
    }
}
