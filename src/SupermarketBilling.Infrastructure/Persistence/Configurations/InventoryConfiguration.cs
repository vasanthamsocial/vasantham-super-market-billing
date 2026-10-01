using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Organisation;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal static class InventoryKeys
{
    public const int Quantity = 3;
    public const int Cost = 4;

    public static void HasStoreInBusiness<T>(this EntityTypeBuilder<T> builder, string storeIdProperty = "StoreId")
        where T : class =>
        builder.HasOne<Store>().WithMany().HasForeignKey(storeIdProperty, "BusinessId")
            .HasPrincipalKey(nameof(Store.Id), nameof(Store.BusinessId)).OnDelete(DeleteBehavior.Restrict);

    public static void HasVariantInBusiness<T>(this EntityTypeBuilder<T> builder)
        where T : class =>
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey("VariantId", "BusinessId")
            .HasPrincipalKey(nameof(ProductVariant.Id), nameof(ProductVariant.BusinessId)).OnDelete(DeleteBehavior.Restrict);
}

internal sealed class InventorySettingsConfiguration : IEntityTypeConfiguration<InventorySettings>
{
    public void Configure(EntityTypeBuilder<InventorySettings> builder)
    {
        builder.ToTable("inventory_settings", t => t.HasCheckConstraint("ck_inventory_settings_method", "valuation_method IN ('FIFO', 'FEFO', 'WEIGHTED_AVERAGE')"));
        builder.HasKey(s => s.BusinessId);
        builder.Property(s => s.ValuationMethod).HasMaxLength(20).IsRequired();
        builder.Property(s => s.RowVersion).IsRowVersion();
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class NegativeStockRuleConfiguration : IEntityTypeConfiguration<NegativeStockRule>
{
    public void Configure(EntityTypeBuilder<NegativeStockRule> builder)
    {
        builder.ToTable("negative_stock_rules", t =>
        {
            t.HasCheckConstraint("ck_negative_stock_rules_mode", "mode IN ('DISABLED', 'WARN_OVERRIDE', 'ENABLED_WITH_LIMIT')");
            t.HasCheckConstraint("ck_negative_stock_rules_limit", "(mode = 'ENABLED_WITH_LIMIT') = (limit_quantity IS NOT NULL AND limit_quantity > 0)");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Mode).HasMaxLength(20).IsRequired();
        builder.Property(r => r.LimitQuantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(r => r.Reason).HasMaxLength(300).IsRequired();
        builder.Ignore(r => r.Specificity);

        // One active rule per scope (business, store, product, or product in store).
        builder.HasIndex(r => new { r.BusinessId, r.StoreId, r.ProductId }).IsUnique().AreNullsDistinct(false)
            .HasFilter("is_active").HasDatabaseName("ux_negative_stock_rules_active_scope");
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasOne<Product>().WithMany().HasForeignKey(r => new { r.ProductId, r.BusinessId })
            .HasPrincipalKey(p => new { p.Id, p.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BatchConfiguration : IEntityTypeConfiguration<Batch>
{
    public void Configure(EntityTypeBuilder<Batch> builder)
    {
        builder.ToTable("batches", t => t.HasCheckConstraint("ck_batches_dates", "manufactured_on IS NULL OR expires_on IS NULL OR expires_on > manufactured_on"));
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.HasAlternateKey(b => new { b.Id, b.VariantId });
        builder.Property(b => b.BatchNumber).HasMaxLength(30).IsRequired();
        builder.HasIndex(b => new { b.VariantId, b.BatchNumber }).IsUnique();
        builder.HasIndex(b => new { b.BusinessId, b.ExpiresOn });
        builder.BelongsToBusinessInTenant();
        builder.HasVariantInBusiness();
    }
}

internal sealed class CostLayerConfiguration : IEntityTypeConfiguration<CostLayer>
{
    public void Configure(EntityTypeBuilder<CostLayer> builder)
    {
        builder.ToTable("cost_layers", t =>
        {
            t.HasCheckConstraint("ck_cost_layers_quantities",
                "original_quantity > 0 AND remaining_quantity >= 0 AND settled_shortfall >= 0 AND remaining_quantity + settled_shortfall <= original_quantity");
            t.HasCheckConstraint("ck_cost_layers_cost", "unit_cost >= 0");
        });
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Sequence).UseIdentityAlwaysColumn();
        builder.HasIndex(l => l.Sequence).IsUnique();
        builder.Property(l => l.UnitCost).HasPrecision(18, InventoryKeys.Cost);
        builder.Property(l => l.OriginalQuantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(l => l.RemainingQuantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(l => l.SettledShortfall).HasPrecision(18, InventoryKeys.Quantity);
        builder.HasIndex(l => new { l.StoreId, l.VariantId, l.Sequence }).HasFilter("remaining_quantity > 0").HasDatabaseName("ix_cost_layers_open");
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasVariantInBusiness();
        builder.HasOne<Batch>().WithMany().HasForeignKey(l => new { l.BatchId, l.VariantId })
            .HasPrincipalKey(b => new { b.Id, b.VariantId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> builder)
    {
        builder.ToTable("stock_balances", t => t.HasCheckConstraint("ck_stock_balances_costs", "average_cost >= 0 AND last_cost >= 0"));
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Quantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(b => b.AverageCost).HasPrecision(18, InventoryKeys.Cost);
        builder.Property(b => b.LastCost).HasPrecision(18, InventoryKeys.Cost);
        builder.HasIndex(b => new { b.StoreId, b.VariantId }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasVariantInBusiness();
    }
}

internal sealed class StockLedgerEntryConfiguration : IEntityTypeConfiguration<StockLedgerEntry>
{
    public void Configure(EntityTypeBuilder<StockLedgerEntry> builder)
    {
        builder.ToTable("stock_ledger", t =>
        {
            t.HasCheckConstraint("ck_stock_ledger_quantity", "quantity <> 0");
            t.HasCheckConstraint("ck_stock_ledger_value", "value = round(quantity * unit_cost, 4)");
            t.HasCheckConstraint("ck_stock_ledger_cost", "unit_cost >= 0");
            t.HasCheckConstraint("ck_stock_ledger_direction",
                "(movement_type IN ('OPENING', 'ADJUSTMENT_IN', 'TRANSFER_IN', 'COUNT_GAIN', 'RECEIPT', 'SALE_RETURN') AND quantity > 0) OR " +
                "(movement_type IN ('ADJUSTMENT_OUT', 'DAMAGE', 'WASTAGE', 'TRANSFER_OUT', 'COUNT_LOSS', 'PURCHASE_RETURN', 'SALE') AND quantity < 0)");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Sequence).UseIdentityAlwaysColumn();
        builder.HasIndex(e => e.Sequence).IsUnique();
        builder.Property(e => e.MovementType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.DocumentType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Quantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(e => e.UnitCost).HasPrecision(18, InventoryKeys.Cost);
        builder.Property(e => e.Value).HasPrecision(20, InventoryKeys.Cost);
        builder.Property(e => e.BalanceAfter).HasPrecision(18, InventoryKeys.Quantity);
        builder.HasIndex(e => new { e.StoreId, e.VariantId, e.Sequence });
        builder.HasIndex(e => e.LayerId);
        builder.HasIndex(e => new { e.DocumentType, e.DocumentId });
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasVariantInBusiness();
        builder.HasOne<CostLayer>().WithMany().HasForeignKey(e => e.LayerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Batch>().WithMany().HasForeignKey(e => new { e.BatchId, e.VariantId })
            .HasPrincipalKey(b => new { b.Id, b.VariantId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockDocumentConfiguration : IEntityTypeConfiguration<StockDocument>
{
    public void Configure(EntityTypeBuilder<StockDocument> builder)
    {
        builder.ToTable("stock_documents", t =>
        {
            t.HasCheckConstraint("ck_stock_documents_type", "type IN ('OPENING', 'ADJUSTMENT', 'DAMAGE', 'WASTAGE', 'TRANSFER', 'COUNT')");
            t.HasCheckConstraint("ck_stock_documents_transfer", "(type = 'TRANSFER') = (target_store_id IS NOT NULL) AND (target_store_id IS NULL OR target_store_id <> store_id)");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Type).HasMaxLength(20).IsRequired();
        builder.Property(d => d.Number).HasMaxLength(40).IsRequired();
        builder.Property(d => d.Reason).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Note).HasMaxLength(500);
        builder.Property(d => d.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(d => d.RequestHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(d => new { d.BusinessId, d.IdempotencyKey }).IsUnique();
        builder.HasIndex(d => new { d.StoreId, d.Number }).IsUnique();
        builder.HasIndex(d => new { d.StoreId, d.PostedAtUtc });
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasStoreInBusiness(nameof(StockDocument.TargetStoreId));
    }
}

internal sealed class ReorderLevelConfiguration : IEntityTypeConfiguration<ReorderLevel>
{
    public void Configure(EntityTypeBuilder<ReorderLevel> builder)
    {
        builder.ToTable("reorder_levels", t => t.HasCheckConstraint("ck_reorder_levels_positive", "minimum_quantity >= 0 AND reorder_quantity >= 0"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.MinimumQuantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(r => r.ReorderQuantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.HasIndex(r => new { r.StoreId, r.VariantId }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasVariantInBusiness();
    }
}

internal sealed class DocumentSequenceConfiguration : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> builder)
    {
        builder.ToTable("document_sequences", t => t.HasCheckConstraint("ck_document_sequences_positive", "next_number > 0"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Series).HasMaxLength(20).IsRequired();
        builder.HasIndex(s => new { s.StoreId, s.Series }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
    }
}
