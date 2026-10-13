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
        builder.Property(r => r.ReceiptKeyPem).HasMaxLength(1000);
        builder.Property(r => r.ReceiptKeyId).HasMaxLength(16);
        builder.Property(r => r.RowVersion).IsRowVersion();
        builder.HasIndex(r => r.BusinessId).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(r => r.SetByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MonthRemovalReceiptConfiguration : IEntityTypeConfiguration<MonthRemovalReceipt>
{
    public void Configure(EntityTypeBuilder<MonthRemovalReceipt> builder)
    {
        builder.ToTable("month_removal_receipts", t => t.HasCheckConstraint("ck_month_removal_receipts_values",
            "extract(day FROM month) = 1 AND char_length(file_sha256) = 64"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.FileSha256).HasMaxLength(64).IsRequired();
        builder.Property(r => r.KeyId).HasMaxLength(16).IsRequired();
        builder.Property(r => r.Receipt).HasMaxLength(16384).IsRequired();
        builder.HasIndex(r => new { r.BusinessId, r.Month }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(r => r.RegisteredByUserId).OnDelete(DeleteBehavior.Restrict);

        // A receipt is for a package this server made (of a locked month).
        builder.HasOne<MonthPackage>().WithMany().HasForeignKey(r => r.PackageId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MonthRemovalConfiguration : IEntityTypeConfiguration<MonthRemoval>
{
    public void Configure(EntityTypeBuilder<MonthRemoval> builder)
    {
        builder.ToTable("month_removals", t => t.HasCheckConstraint("ck_month_removals_first_day", "extract(day FROM month) = 1"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Counts).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(r => new { r.BusinessId, r.Month }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(r => r.RemovedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MonthRemovalReceipt>().WithMany().HasForeignKey(r => r.ReceiptId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MonthRemovedNumbersConfiguration : IEntityTypeConfiguration<MonthRemovedNumbers>
{
    public void Configure(EntityTypeBuilder<MonthRemovedNumbers> builder)
    {
        builder.ToTable("month_removed_numbers", t => t.HasCheckConstraint("ck_month_removed_numbers_values", "count > 0 AND last_number > 0"));
        builder.HasKey(n => new { n.RemovalId, n.StoreId, n.Series });
        builder.Property(n => n.Series).HasMaxLength(40).IsRequired();
        builder.HasIndex(n => new { n.StoreId, n.Series });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<MonthRemoval>().WithMany().HasForeignKey(n => n.RemovalId).OnDelete(DeleteBehavior.Restrict);
    }
}
