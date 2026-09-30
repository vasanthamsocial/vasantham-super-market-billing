using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal static class BusinessOwnedExtensions
{
    /// <summary>
    /// Links the row to its business through (business_id, tenant_id), so it can never belong to a business of
    /// another tenant, even though foreign-key checks are not subject to row-level security.
    /// </summary>
    public static void BelongsToBusinessInTenant<T>(this EntityTypeBuilder<T> builder, string businessIdProperty = "BusinessId")
        where T : class =>
        builder.HasOne<Business>().WithMany()
            .HasForeignKey(businessIdProperty, TenancyModel.TenantIdProperty)
            .HasPrincipalKey(nameof(Business.Id), TenancyModel.TenantIdProperty)
            .OnDelete(DeleteBehavior.Restrict);
}

internal sealed class TaxRegistrationConfiguration : IEntityTypeConfiguration<TaxRegistration>
{
    public void Configure(EntityTypeBuilder<TaxRegistration> builder)
    {
        builder.ToTable("tax_registrations", t =>
        {
            t.HasCheckConstraint("ck_tax_registrations_mode", "mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')");
            t.HasCheckConstraint("ck_tax_registrations_gstin", "(mode = 'NOT_GST_REGISTERED') = (gstin IS NULL)");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Mode).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Gstin).HasMaxLength(15).IsFixedLength();
        builder.Property(r => r.Reason).HasMaxLength(500).IsRequired();
        builder.Property(r => r.EvidenceReference).HasMaxLength(200);
        builder.HasIndex(r => new { r.BusinessId, r.EffectiveFrom }).IsUnique();
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.ToTable("units", t => t.HasCheckConstraint("ck_units_decimal_places", "decimal_places BETWEEN 0 AND 3"));
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.HasAlternateKey(u => new { u.Id, u.BusinessId });
        builder.Property(u => u.Code).HasMaxLength(10).IsRequired();
        builder.Property(u => u.Name).HasMaxLength(50).IsRequired();
        builder.HasIndex(u => new { u.BusinessId, u.Code }).IsUnique();
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.Id, c.BusinessId });
        builder.Property(c => c.Name).HasMaxLength(80).IsRequired();
        builder.HasIndex(c => new { c.BusinessId, c.ParentId, c.Name }).IsUnique().AreNullsDistinct(false);
        builder.HasOne<Category>().WithMany().HasForeignKey(c => new { c.ParentId, c.BusinessId })
            .HasPrincipalKey(c => new { c.Id, c.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.ToTable("brands");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.HasAlternateKey(b => new { b.Id, b.BusinessId });
        builder.Property(b => b.Name).HasMaxLength(80).IsRequired();
        builder.HasIndex(b => new { b.BusinessId, b.Name }).IsUnique();
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class CustomerGroupConfiguration : IEntityTypeConfiguration<CustomerGroup>
{
    public void Configure(EntityTypeBuilder<CustomerGroup> builder)
    {
        builder.ToTable("customer_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.HasAlternateKey(g => new { g.Id, g.BusinessId });
        builder.Property(g => g.Code).HasMaxLength(20).IsRequired();
        builder.Property(g => g.Name).HasMaxLength(80).IsRequired();
        builder.HasIndex(g => new { g.BusinessId, g.Code }).IsUnique();
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", t =>
        {
            t.HasCheckConstraint("ck_products_supply_type", "supply_type IN ('TAXABLE', 'EXEMPT', 'NIL_RATED', 'NON_GST')");
            t.HasCheckConstraint(
                "ck_products_rates",
                "(supply_type = 'TAXABLE' AND gst_rate_percent > 0 AND gst_rate_percent <= 100 AND cess_rate_percent >= 0) " +
                "OR (supply_type <> 'TAXABLE' AND gst_rate_percent = 0 AND cess_rate_percent = 0)");
            t.HasCheckConstraint("ck_products_hsn", "hsn_sac ~ '^([0-9]{4}|[0-9]{6}|[0-9]{8})$'");
            t.HasCheckConstraint("ck_products_expiry_needs_batches", "NOT tracks_expiry OR tracks_batches");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasAlternateKey(p => new { p.Id, p.BusinessId });
        builder.Property(p => p.Code).HasMaxLength(30).IsRequired();
        builder.HasIndex(p => new { p.BusinessId, p.Code }).IsUnique();
        builder.Property(p => p.Name).HasMaxLength(150).IsRequired();
        builder.Property(p => p.PrintName).HasMaxLength(40).IsRequired();
        builder.Property(p => p.HsnSac).HasMaxLength(8).IsRequired();
        builder.Property(p => p.SupplyType).HasMaxLength(12).IsRequired();
        builder.Property(p => p.GstRatePercent).HasPrecision(7, 3);
        builder.Property(p => p.CessRatePercent).HasPrecision(7, 3);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.HasIndex(p => new { p.BusinessId, p.Name });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Unit>().WithMany().HasForeignKey(p => new { p.BaseUnitId, p.BusinessId })
            .HasPrincipalKey(u => new { u.Id, u.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Category>().WithMany().HasForeignKey(p => new { p.CategoryId, p.BusinessId })
            .HasPrincipalKey(c => new { c.Id, c.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Brand>().WithMany().HasForeignKey(p => new { p.BrandId, p.BusinessId })
            .HasPrincipalKey(b => new { b.Id, b.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("product_variants");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.HasAlternateKey(v => new { v.Id, v.BusinessId });
        builder.Property(v => v.Code).HasMaxLength(30).IsRequired();
        builder.HasIndex(v => new { v.BusinessId, v.Code }).IsUnique();
        builder.Property(v => v.Name).HasMaxLength(150).IsRequired();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Product>().WithMany().HasForeignKey(v => new { v.ProductId, v.BusinessId })
            .HasPrincipalKey(p => new { p.Id, p.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VariantUnitConfiguration : IEntityTypeConfiguration<VariantUnit>
{
    public void Configure(EntityTypeBuilder<VariantUnit> builder)
    {
        builder.ToTable("variant_units", t =>
        {
            t.HasCheckConstraint("ck_variant_units_factor", "factor_to_base > 0 AND (NOT is_base OR factor_to_base = 1)");
        });
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.HasAlternateKey(u => new { u.Id, u.VariantId });
        builder.Property(u => u.FactorToBase).HasPrecision(18, VariantUnit.FactorScale);
        builder.HasIndex(u => new { u.VariantId, u.UnitId }).IsUnique();
        builder.HasIndex(u => u.VariantId).IsUnique().HasFilter("is_base").HasDatabaseName("ux_variant_units_one_base");
        builder.BelongsToBusinessInTenant();
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(u => new { u.VariantId, u.BusinessId })
            .HasPrincipalKey(v => new { v.Id, v.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Unit>().WithMany().HasForeignKey(u => new { u.UnitId, u.BusinessId })
            .HasPrincipalKey(u => new { u.Id, u.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VariantBarcodeConfiguration : IEntityTypeConfiguration<VariantBarcode>
{
    public void Configure(EntityTypeBuilder<VariantBarcode> builder)
    {
        builder.ToTable("variant_barcodes", t => t.HasCheckConstraint("ck_variant_barcodes_type", "type IN ('GS1', 'INTERNAL')"));
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Code).HasMaxLength(20).IsRequired();
        builder.Property(b => b.Type).HasMaxLength(10).IsRequired();

        // A barcode identifies one pack in the business; manufacturers do reuse retired codes, so only active ones must be unique.
        builder.HasIndex(b => new { b.BusinessId, b.Code }).IsUnique().HasFilter("is_active").HasDatabaseName("ux_variant_barcodes_active_code");
        builder.BelongsToBusinessInTenant();
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(b => new { b.VariantId, b.BusinessId })
            .HasPrincipalKey(v => new { v.Id, v.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<VariantUnit>().WithMany().HasForeignKey(b => new { b.VariantUnitId, b.VariantId })
            .HasPrincipalKey(u => new { u.Id, u.VariantId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VariantMrpConfiguration : IEntityTypeConfiguration<VariantMrp>
{
    public void Configure(EntityTypeBuilder<VariantMrp> builder)
    {
        builder.ToTable("variant_mrps", t => t.HasCheckConstraint("ck_variant_mrps_positive", "mrp > 0"));
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Mrp).HasPrecision(18, 2);
        builder.HasIndex(m => new { m.VariantUnitId, m.Mrp }).IsUnique().HasFilter("is_active").HasDatabaseName("ux_variant_mrps_active");
        builder.BelongsToBusinessInTenant();
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(m => new { m.VariantId, m.BusinessId })
            .HasPrincipalKey(v => new { v.Id, v.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<VariantUnit>().WithMany().HasForeignKey(m => new { m.VariantUnitId, m.VariantId })
            .HasPrincipalKey(u => new { u.Id, u.VariantId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PriceRuleConfiguration : IEntityTypeConfiguration<PriceRule>
{
    public void Configure(EntityTypeBuilder<PriceRule> builder)
    {
        builder.ToTable("price_rules", t =>
        {
            t.HasCheckConstraint("ck_price_rules_rate_type", "rate_type IN ('STANDARD', 'STORE', 'QUANTITY_SLAB', 'MEMBER', 'CUSTOMER_GROUP', 'PROMOTIONAL', 'MINIMUM')");
            t.HasCheckConstraint("ck_price_rules_channel", "channel IN ('RETAIL', 'WHOLESALE', 'ANY')");
            t.HasCheckConstraint("ck_price_rules_status", "status IN ('PENDING_APPROVAL', 'ACTIVE', 'REJECTED', 'RETIRED')");
            t.HasCheckConstraint("ck_price_rules_price", "price > 0 AND (mrp IS NULL OR mrp > 0)");
            t.HasCheckConstraint("ck_price_rules_quantity", "min_quantity >= 0 AND (max_quantity IS NULL OR max_quantity > min_quantity)");
            t.HasCheckConstraint("ck_price_rules_validity", "valid_to_utc IS NULL OR valid_to_utc > valid_from_utc");
            t.HasCheckConstraint("ck_price_rules_retirement", "(status = 'RETIRED') = (retired_at_utc IS NOT NULL)");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.RateType).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Channel).HasMaxLength(10).IsRequired();
        builder.Property(r => r.Status).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Mrp).HasPrecision(18, 2);
        builder.Property(r => r.MinQuantity).HasPrecision(18, 3);
        builder.Property(r => r.MaxQuantity).HasPrecision(18, 3);
        builder.Property(r => r.Note).HasMaxLength(300);
        builder.Property(r => r.RowVersion).IsRowVersion();
        builder.Ignore(r => r.IsMinimum);
        builder.Ignore(r => r.Specificity);
        builder.HasIndex(r => new { r.VariantUnitId, r.Status });
        builder.HasIndex(r => new { r.BusinessId, r.Status });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(r => new { r.VariantId, r.BusinessId })
            .HasPrincipalKey(v => new { v.Id, v.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<VariantUnit>().WithMany().HasForeignKey(r => new { r.VariantUnitId, r.VariantId })
            .HasPrincipalKey(u => new { u.Id, u.VariantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Store>().WithMany().HasForeignKey(r => new { r.StoreId, r.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustomerGroup>().WithMany().HasForeignKey(r => new { r.CustomerGroupId, r.BusinessId })
            .HasPrincipalKey(g => new { g.Id, g.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}
