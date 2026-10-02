using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Purchases;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("suppliers", t =>
        {
            t.HasCheckConstraint("ck_suppliers_code", "code ~ '^[A-Z0-9-]{1,20}$'");
            t.HasCheckConstraint("ck_suppliers_gstin_state", "gstin IS NULL OR left(gstin, 2) = state_code");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasAlternateKey(s => new { s.Id, s.BusinessId });
        builder.Property(s => s.Code).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Gstin).HasMaxLength(15);
        builder.Property(s => s.StateCode).HasMaxLength(2).IsRequired();
        builder.Property(s => s.Address).HasMaxLength(500);
        builder.Property(s => s.Phone).HasMaxLength(20);
        builder.Property(s => s.RowVersion).IsRowVersion();
        builder.Ignore(s => s.IsGstRegistered);
        builder.HasIndex(s => new { s.BusinessId, s.Code }).IsUnique();
        builder.HasIndex(s => new { s.BusinessId, s.Gstin });
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class PurchaseSettingsConfiguration : IEntityTypeConfiguration<PurchaseSettings>
{
    public void Configure(EntityTypeBuilder<PurchaseSettings> builder)
    {
        builder.ToTable("purchase_settings", t => t.HasCheckConstraint("ck_purchase_settings_thresholds",
            "cost_reason_threshold_percent >= 0 AND cost_approval_threshold_percent >= cost_reason_threshold_percent"));
        builder.HasKey(s => s.BusinessId);
        builder.Property(s => s.CostReasonThresholdPercent).HasPrecision(7, 2);
        builder.Property(s => s.CostApprovalThresholdPercent).HasPrecision(7, 2);
        builder.Property(s => s.RowVersion).IsRowVersion();
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class GrnConfiguration : IEntityTypeConfiguration<Grn>
{
    public void Configure(EntityTypeBuilder<Grn> builder)
    {
        builder.ToTable("grns", t =>
        {
            t.HasCheckConstraint("ck_grns_status", "status IN ('PENDING_APPROVAL', 'POSTED', 'REJECTED')");
            t.HasCheckConstraint("ck_grns_classification",
                "classification IN ('GST_TAX_INVOICE', 'BILL_OF_SUPPLY', 'UNREGISTERED', 'IMPORT', 'REVERSE_CHARGE', 'PENDING_DOCUMENT', 'OTHER')");
            t.HasCheckConstraint("ck_grns_totals",
                "invoice_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 1 " +
                "AND taxable_total = gross_total - discount_total AND cgst_total = sgst_total");
            t.HasCheckConstraint("ck_grns_posted", "(status = 'POSTED') = (posted_at_utc IS NOT NULL)");
        });
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.HasAlternateKey(g => new { g.Id, g.BusinessId });
        builder.Property(g => g.Number).HasMaxLength(40).IsRequired();
        builder.Property(g => g.SupplierInvoiceNumber).HasMaxLength(30).IsRequired();
        builder.Property(g => g.Classification).HasMaxLength(20).IsRequired();
        builder.Property(g => g.PurchaseOrderReference).HasMaxLength(40);
        builder.HasIndex(g => new { g.PurchaseOrderId, g.BusinessId });
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(g => new { g.PurchaseOrderId, g.BusinessId })
            .HasPrincipalKey(o => new { o.Id, o.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.Property(g => g.Status).HasMaxLength(20).IsRequired();
        builder.Property(g => g.Notes).HasMaxLength(500);
        builder.Property(g => g.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(g => g.RequestHash).HasMaxLength(64).IsRequired();
        foreach (var money in new[]
                 {
                     nameof(Grn.GrossTotal), nameof(Grn.DiscountTotal), nameof(Grn.TaxableTotal), nameof(Grn.CgstTotal), nameof(Grn.SgstTotal), nameof(Grn.IgstTotal),
                     nameof(Grn.CessTotal), nameof(Grn.RoundOff), nameof(Grn.InvoiceTotal), nameof(Grn.ExpensesTotal), nameof(Grn.LandedTotal),
                 })
        {
            builder.Property<decimal>(money).HasPrecision(18, SalesKeys.Money);
        }

        builder.Property(g => g.RowVersion).IsRowVersion();

        // The same supplier invoice cannot be received twice (a rejected receipt does not count).
        builder.HasIndex(g => new { g.BusinessId, g.SupplierId, g.SupplierInvoiceNumber }).IsUnique().HasFilter("status <> 'REJECTED'")
            .HasDatabaseName("ux_grns_supplier_invoice");
        builder.HasIndex(g => new { g.BusinessId, g.IdempotencyKey }).IsUnique();
        builder.HasIndex(g => new { g.StoreId, g.SequenceNumber }).IsUnique();
        builder.HasIndex(g => new { g.StoreId, g.BusinessDate });
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasOne<Supplier>().WithMany().HasForeignKey(g => new { g.SupplierId, g.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GrnLineConfiguration : IEntityTypeConfiguration<GrnLine>
{
    public void Configure(EntityTypeBuilder<GrnLine> builder)
    {
        builder.ToTable("grn_lines", t =>
        {
            t.HasCheckConstraint("ck_grn_lines_amounts",
                "quantity >= 0 AND free_quantity >= 0 AND quantity + free_quantity > 0 AND factor_to_base > 0 AND rate >= 0 AND discount >= 0 " +
                "AND taxable = gross - discount AND total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)");
            t.HasCheckConstraint("ck_grn_lines_landed",
                "landed_total = taxable + non_recoverable_tax + expense_share AND base_quantity = (quantity + free_quantity) * factor_to_base AND landed_unit_cost >= 0");
        });
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Description).HasMaxLength(200).IsRequired();
        builder.Property(l => l.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(l => l.FactorToBase).HasPrecision(18, 6);
        foreach (var quantity in new[] { nameof(GrnLine.Quantity), nameof(GrnLine.FreeQuantity), nameof(GrnLine.BaseQuantity), nameof(GrnLine.Weight), nameof(GrnLine.Volume) })
        {
            builder.Property(quantity).HasPrecision(18, InventoryKeys.Quantity);
        }

        builder.Property(l => l.Rate).HasPrecision(18, InventoryKeys.Cost);
        builder.Property(l => l.LandedUnitCost).HasPrecision(18, InventoryKeys.Cost);
        builder.Property(l => l.PreviousUnitCost).HasPrecision(18, InventoryKeys.Cost);
        builder.Property(l => l.CostChangePercent).HasPrecision(9, 2);
        builder.Property(l => l.GstRatePercent).HasPrecision(5, 2);
        builder.Property(l => l.CessRatePercent).HasPrecision(5, 2);
        foreach (var money in new[]
                 {
                     nameof(GrnLine.Mrp), nameof(GrnLine.Discount), nameof(GrnLine.Gross), nameof(GrnLine.Taxable), nameof(GrnLine.Cgst), nameof(GrnLine.Sgst),
                     nameof(GrnLine.Igst), nameof(GrnLine.Cess), nameof(GrnLine.Total), nameof(GrnLine.ExpenseShare), nameof(GrnLine.NonRecoverableTax),
                     nameof(GrnLine.LandedTotal), nameof(GrnLine.SellingPrice),
                 })
        {
            builder.Property(money).HasPrecision(18, SalesKeys.Money);
        }

        builder.Property(l => l.BatchNumber).HasMaxLength(30);
        builder.Property(l => l.CostChangeReason).HasMaxLength(300);
        builder.Property(l => l.LossLeaderReason).HasMaxLength(300);
        builder.HasIndex(l => new { l.GrnId, l.LineNumber }).IsUnique();
        builder.HasIndex(l => new { l.VariantId, l.Mrp });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Grn>().WithMany().HasForeignKey(l => new { l.GrnId, l.BusinessId }).HasPrincipalKey(g => new { g.Id, g.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasVariantInBusiness();
        builder.HasOne<VariantUnit>().WithMany().HasForeignKey(l => l.VariantUnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GrnExpenseConfiguration : IEntityTypeConfiguration<GrnExpense>
{
    public void Configure(EntityTypeBuilder<GrnExpense> builder)
    {
        builder.ToTable("grn_expenses", t =>
        {
            t.HasCheckConstraint("ck_grn_expenses_kind", "kind IN ('FREIGHT', 'LOADING', 'INSURANCE', 'PACKING', 'HANDLING', 'TRANSPORT', 'CUSTOMS', 'OTHER')");
            t.HasCheckConstraint("ck_grn_expenses_method", "method IN ('QUANTITY', 'VALUE', 'WEIGHT', 'VOLUME', 'EQUAL', 'MANUAL')");
            t.HasCheckConstraint("ck_grn_expenses_amount", "amount > 0");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Kind).HasMaxLength(12).IsRequired();
        builder.Property(e => e.Method).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Amount).HasPrecision(18, SalesKeys.Money);
        builder.Property(e => e.Note).HasMaxLength(200);
        builder.HasIndex(e => new { e.GrnId, e.ExpenseOrder }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Grn>().WithMany().HasForeignKey(e => new { e.GrnId, e.BusinessId }).HasPrincipalKey(g => new { g.Id, g.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GrnAllocationConfiguration : IEntityTypeConfiguration<GrnAllocation>
{
    public void Configure(EntityTypeBuilder<GrnAllocation> builder)
    {
        builder.ToTable("grn_allocations", t => t.HasCheckConstraint("ck_grn_allocations_amount", "amount >= 0"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Amount).HasPrecision(18, SalesKeys.Money);
        builder.HasIndex(a => new { a.ExpenseId, a.LineId }).IsUnique();
        builder.HasIndex(a => a.LineId);
        builder.BelongsToBusinessInTenant();
        builder.HasOne<GrnExpense>().WithMany().HasForeignKey(a => a.ExpenseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GrnLine>().WithMany().HasForeignKey(a => a.LineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("purchase_orders", t =>
        {
            t.HasCheckConstraint("ck_purchase_orders_status", "status IN ('OPEN', 'CLOSED', 'CANCELLED')");
            t.HasCheckConstraint("ck_purchase_orders_dates", "expected_date IS NULL OR expected_date >= order_date");
            t.HasCheckConstraint("ck_purchase_orders_closed", "(status = 'OPEN') = (closed_at_utc IS NULL)");
        });
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.HasAlternateKey(o => new { o.Id, o.BusinessId });
        builder.Property(o => o.Number).HasMaxLength(40).IsRequired();
        builder.Property(o => o.Status).HasMaxLength(10).IsRequired();
        builder.Property(o => o.Notes).HasMaxLength(500);
        builder.Property(o => o.RowVersion).IsRowVersion();
        builder.HasIndex(o => new { o.StoreId, o.SequenceNumber }).IsUnique();
        builder.HasIndex(o => new { o.StoreId, o.Status });
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasOne<Supplier>().WithMany().HasForeignKey(o => new { o.SupplierId, o.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("purchase_order_lines", t => t.HasCheckConstraint("ck_purchase_order_lines_quantity", "quantity > 0 AND (rate IS NULL OR rate >= 0)"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Quantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(l => l.Rate).HasPrecision(18, InventoryKeys.Cost);
        builder.HasIndex(l => new { l.PurchaseOrderId, l.LineNumber }).IsUnique();
        builder.HasIndex(l => new { l.PurchaseOrderId, l.VariantUnitId }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(l => new { l.PurchaseOrderId, l.BusinessId })
            .HasPrincipalKey(o => new { o.Id, o.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasVariantInBusiness();
        builder.HasOne<VariantUnit>().WithMany().HasForeignKey(l => l.VariantUnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments", t =>
        {
            t.HasCheckConstraint("ck_attachments_type", "content_type IN ('application/pdf', 'image/jpeg', 'image/png')");
            t.HasCheckConstraint("ck_attachments_size", "size > 0 AND size <= 10485760 AND size = octet_length(content)");
            t.HasCheckConstraint("ck_attachments_owner", "owner_type IN ('GRN')");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.OwnerType).HasMaxLength(20).IsRequired();
        builder.Property(a => a.FileName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(40).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Content).IsRequired();
        builder.HasIndex(a => new { a.OwnerType, a.OwnerId });
        builder.BelongsToBusinessInTenant();
    }
}
