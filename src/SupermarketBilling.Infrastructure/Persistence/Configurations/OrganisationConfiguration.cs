using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Organisation;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal sealed class BusinessConfiguration : IEntityTypeConfiguration<Business>
{
    public void Configure(EntityTypeBuilder<Business> builder)
    {
        builder.ToTable("businesses", t =>
        {
            t.HasCheckConstraint("ck_businesses_code", "code ~ '^[A-Z0-9]{2,12}$'");
            t.HasCheckConstraint("ck_businesses_state_code", "state_code ~ '^[0-9]{2}$' AND state_code <> '00'");
            t.HasCheckConstraint("ck_businesses_gstin_state", "gstin IS NULL OR left(gstin, 2) = state_code");
        });
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Code).HasMaxLength(12).IsRequired();
        builder.HasIndex(b => b.Code).IsUnique();
        builder.Property(b => b.LegalName).HasMaxLength(200).IsRequired();
        builder.Property(b => b.TradeName).HasMaxLength(200).IsRequired();
        builder.Property(b => b.Gstin).HasMaxLength(15).IsFixedLength();
        builder.HasIndex(b => b.Gstin).IsUnique().HasFilter("gstin IS NOT NULL");
        builder.Property(b => b.StateCode).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(b => b.Address).HasMaxLength(500);
        builder.Property(b => b.RowVersion).IsRowVersion();
    }
}

internal sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> builder)
    {
        builder.ToTable("stores", t =>
        {
            t.HasCheckConstraint("ck_stores_code", "code ~ '^[A-Z0-9][A-Z0-9-]{0,11}$'");
            t.HasCheckConstraint("ck_stores_state_code", "state_code ~ '^[0-9]{2}$' AND state_code <> '00'");
            t.HasCheckConstraint("ck_stores_gstin_state", "gstin IS NULL OR left(gstin, 2) = state_code");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        // (id, business_id) lets child rows reference a store *within a specific business*, so the database
        // itself rejects a role assignment that pairs business A with a store of business B.
        builder.HasAlternateKey(s => new { s.Id, s.BusinessId });
        builder.HasOne<Business>().WithMany().HasForeignKey(s => s.BusinessId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.Code).HasMaxLength(12).IsRequired();
        builder.HasIndex(s => new { s.BusinessId, s.Code }).IsUnique();
        builder.Property(s => s.Name).HasMaxLength(120).IsRequired();
        builder.Property(s => s.StateCode).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(s => s.Gstin).HasMaxLength(15).IsFixedLength();
        builder.Property(s => s.Address).HasMaxLength(500);
        builder.Property(s => s.TimeZone).HasMaxLength(64).IsRequired();
        builder.Property(s => s.RowVersion).IsRowVersion();
    }
}
