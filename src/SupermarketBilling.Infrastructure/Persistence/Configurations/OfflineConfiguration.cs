using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Accounts;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal sealed class CollectionDeviceConfiguration : IEntityTypeConfiguration<CollectionDevice>
{
    public void Configure(EntityTypeBuilder<CollectionDevice> builder)
    {
        builder.ToTable("collection_devices", t =>
        {
            t.HasCheckConstraint("ck_collection_devices_limits", "offline_limit >= 0 AND max_offline_hours BETWEEN 1 AND 168 AND last_sequence >= 0");
            t.HasCheckConstraint("ck_collection_devices_revoked", "(revoked_at_utc IS NULL) = (revoked_by_user_id IS NULL)");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.HasAlternateKey(d => new { d.Id, d.BusinessId });
        builder.Ignore(d => d.IsActive);
        builder.Property(d => d.Name).HasMaxLength(60).IsRequired();
        builder.Property(d => d.TokenHash).HasMaxLength(32).IsRequired();
        builder.Property(d => d.OfflineLimit).HasPrecision(18, 2);
        builder.Property(d => d.RowVersion).IsRowVersion();
        builder.HasIndex(d => d.TokenHash).IsUnique();
        builder.HasIndex(d => d.CollectorUserId);
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(d => d.CollectorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(d => d.EnrolledByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(d => d.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OfflineSubmissionConfiguration : IEntityTypeConfiguration<OfflineSubmission>
{
    public void Configure(EntityTypeBuilder<OfflineSubmission> builder)
    {
        builder.ToTable("offline_submissions", t =>
        {
            t.HasCheckConstraint("ck_offline_submissions_status", "status IN ('ACCEPTED', 'QUARANTINED', 'RESOLVED_ACCEPTED', 'RESOLVED_REJECTED')");
            t.HasCheckConstraint("ck_offline_submissions_outcome",
                "(status IN ('ACCEPTED', 'RESOLVED_ACCEPTED')) = (receipt_id IS NOT NULL) AND (status = 'ACCEPTED' OR reason IS NOT NULL) " +
                "AND (status LIKE 'RESOLVED%') = (resolved_by_user_id IS NOT NULL AND resolved_at_utc IS NOT NULL AND resolution_note IS NOT NULL) " +
                "AND (resolved_by_user_id IS NULL OR resolved_by_user_id <> collector_user_id)");
            t.HasCheckConstraint("ck_offline_submissions_values", "amount > 0 AND sequence > 0");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Method).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Amount).HasPrecision(18, 2);
        builder.Property(s => s.Reference).HasMaxLength(40);
        builder.Property(s => s.Note).HasMaxLength(200);
        builder.Property(s => s.BankName).HasMaxLength(60);
        builder.Property(s => s.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Status).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Reason).HasMaxLength(300);
        builder.Property(s => s.ResolutionNote).HasMaxLength(300);
        builder.Property(s => s.RowVersion).IsRowVersion();
        builder.HasIndex(s => new { s.DeviceId, s.Sequence }).IsUnique();
        builder.HasIndex(s => new { s.BusinessId, s.Status });
        builder.HasIndex(s => s.ReceiptId).IsUnique().HasFilter("receipt_id IS NOT NULL");
        builder.BelongsToBusinessInTenant();
        builder.HasOne<CollectionDevice>().WithMany().HasForeignKey(s => new { s.DeviceId, s.BusinessId })
            .HasPrincipalKey(d => new { d.Id, d.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Debtor>().WithMany().HasForeignKey(s => new { s.DebtorId, s.BusinessId })
            .HasPrincipalKey(d => new { d.Id, d.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DebtorReceipt>().WithMany().HasForeignKey(s => s.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(s => s.CollectorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(s => s.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
