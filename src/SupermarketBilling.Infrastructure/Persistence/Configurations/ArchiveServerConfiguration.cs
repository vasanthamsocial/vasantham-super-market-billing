using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Archiving;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

// The archive server's own tables (D-042). Archived businesses are not rows of "businesses" here: they come from packages.

internal sealed class ArchiveGrantConfiguration : IEntityTypeConfiguration<ArchiveGrant>
{
    public void Configure(EntityTypeBuilder<ArchiveGrant> builder)
    {
        builder.ToTable("archive_grants", t =>
        {
            t.HasCheckConstraint("ck_archive_grants_role", "role_code IN ('archive_owner', 'archive_manager', 'archive_accountant', 'archive_auditor', 'archive_report_user', 'archive_support')");
            t.HasCheckConstraint("ck_archive_grants_scope", "store_id IS NULL OR business_id IS NOT NULL");
            t.HasCheckConstraint("ck_archive_grants_revoked", "(revoked_at_utc IS NULL) = (revoked_by_user_id IS NULL)");
        });
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.Property(g => g.RoleCode).HasMaxLength(30).IsRequired();
        builder.Property(g => g.Reports).HasMaxLength(2000);
        builder.Ignore(g => g.IsActive);
        builder.HasIndex(g => g.UserId);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(g => g.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(g => g.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ArchiveSourceConfiguration : IEntityTypeConfiguration<ArchiveSource>
{
    public void Configure(EntityTypeBuilder<ArchiveSource> builder)
    {
        builder.ToTable("archive_sources");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.KeyId).HasMaxLength(16).IsRequired();
        builder.Property(s => s.PublicKeyPem).HasMaxLength(1000).IsRequired();
        builder.Ignore(s => s.IsActive);
        builder.HasIndex(s => s.KeyId).IsUnique();
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(s => s.RegisteredByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(s => s.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ArchiveImportConfiguration : IEntityTypeConfiguration<ArchiveImport>
{
    public void Configure(EntityTypeBuilder<ArchiveImport> builder)
    {
        builder.ToTable("archive_imports", t =>
        {
            t.HasCheckConstraint("ck_archive_imports_status", "status IN ('VERIFIED', 'ACCOUNTANT_APPROVED', 'APPROVED')");
            t.HasCheckConstraint("ck_archive_imports_month", "extract(day FROM month) = 1");
            t.HasCheckConstraint("ck_archive_imports_approvals",
                "(status = 'VERIFIED') = (accountant_approved_by_user_id IS NULL) " +
                "AND (status = 'APPROVED') = (owner_approved_by_user_id IS NOT NULL) " +
                "AND (owner_approved_by_user_id IS NULL OR owner_approved_by_user_id <> accountant_approved_by_user_id)");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.BusinessCode).HasMaxLength(12).IsRequired();
        builder.Property(i => i.BusinessName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.FileSha256).HasMaxLength(64).IsRequired();
        builder.Property(i => i.Manifest).HasColumnType("jsonb").IsRequired();
        builder.Property(i => i.Verification).HasColumnType("jsonb").IsRequired();
        builder.Property(i => i.Status).HasMaxLength(20).IsRequired();
        builder.Property(i => i.AccountantNote).HasMaxLength(300);
        builder.Property(i => i.OwnerNote).HasMaxLength(300);
        builder.Property(i => i.RowVersion).IsRowVersion();

        // One archived package per business and month (a re-sent identical month is recognised, not added).
        builder.HasIndex(i => new { i.BusinessId, i.Month }).IsUnique();
        builder.HasOne<ArchiveSource>().WithMany().HasForeignKey(i => i.SourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(i => i.ImportedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(i => i.AccountantApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(i => i.OwnerApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ArchiveRecordConfiguration : IEntityTypeConfiguration<ArchiveRecord>
{
    public void Configure(EntityTypeBuilder<ArchiveRecord> builder)
    {
        builder.ToTable("archive_records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Dataset).HasMaxLength(60).IsRequired();
        builder.Property(r => r.RecordId).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Data).HasColumnType("jsonb").IsRequired();

        // A store record is archived once, in its month.
        builder.HasIndex(r => new { r.BusinessId, r.Dataset, r.RecordId }).IsUnique();
        builder.HasIndex(r => new { r.BusinessId, r.Dataset, r.Month });
        builder.HasIndex(r => r.ImportId);
        builder.HasOne<ArchiveImport>().WithMany().HasForeignKey(r => r.ImportId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ArchiveMasterConfiguration : IEntityTypeConfiguration<ArchiveMaster>
{
    public void Configure(EntityTypeBuilder<ArchiveMaster> builder)
    {
        builder.ToTable("archive_masters");
        builder.HasKey(m => new { m.BusinessId, m.Dataset, m.RecordId });
        builder.Property(m => m.Dataset).HasMaxLength(60).IsRequired();
        builder.Property(m => m.RecordId).HasMaxLength(64).IsRequired();
        builder.Property(m => m.Data).HasColumnType("jsonb").IsRequired();
        builder.HasOne<ArchiveImport>().WithMany().HasForeignKey(m => m.ImportId).OnDelete(DeleteBehavior.Restrict);
    }
}
