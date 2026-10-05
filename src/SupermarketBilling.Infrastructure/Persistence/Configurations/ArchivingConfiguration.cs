using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Archiving;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal sealed class MonthLockConfiguration : IEntityTypeConfiguration<MonthLock>
{
    public void Configure(EntityTypeBuilder<MonthLock> builder)
    {
        builder.ToTable("month_locks", t => t.HasCheckConstraint("ck_month_locks_first_day", "extract(day FROM month) = 1"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Note).HasMaxLength(300);
        builder.Property(l => l.Checks).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(l => new { l.BusinessId, l.Month }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(l => l.LockedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MonthPackageConfiguration : IEntityTypeConfiguration<MonthPackage>
{
    public void Configure(EntityTypeBuilder<MonthPackage> builder)
    {
        builder.ToTable("month_packages", t => t.HasCheckConstraint("ck_month_packages_values", "file_size > 0 AND char_length(file_sha256) = 64"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.FileSha256).HasMaxLength(64).IsRequired();
        builder.Property(p => p.SignerKeyId).HasMaxLength(16).IsRequired();
        builder.Property(p => p.RecipientKeyId).HasMaxLength(16).IsRequired();
        builder.Property(p => p.Manifest).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(p => new { p.BusinessId, p.Month });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        // A package is only ever made for a locked month.
        builder.HasOne<MonthLock>().WithMany().HasForeignKey(p => new { p.BusinessId, p.Month })
            .HasPrincipalKey(l => new { l.BusinessId, l.Month }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ArchiveRecipientConfiguration : IEntityTypeConfiguration<ArchiveRecipient>
{
    public void Configure(EntityTypeBuilder<ArchiveRecipient> builder)
    {
        builder.ToTable("archive_recipients");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.PublicKeyPem).HasMaxLength(1000).IsRequired();
        builder.Property(r => r.KeyId).HasMaxLength(16).IsRequired();
        builder.Property(r => r.RowVersion).IsRowVersion();
        builder.HasIndex(r => r.BusinessId).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(r => r.SetByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
