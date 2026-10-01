using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal static class SalesKeys
{
    /// <summary>Invoice amounts are rupees and paise.</summary>
    public const int Money = 2;

    public static void HasCounterInBusiness<T>(this EntityTypeBuilder<T> builder)
        where T : class =>
        builder.HasOne<Counter>().WithMany().HasForeignKey("CounterId", "BusinessId")
            .HasPrincipalKey(nameof(Counter.Id), nameof(Counter.BusinessId)).OnDelete(DeleteBehavior.Restrict);

    public static void HasInvoiceInBusiness<T>(this EntityTypeBuilder<T> builder)
        where T : class =>
        builder.HasOne<SalesInvoice>().WithMany().HasForeignKey("InvoiceId", "BusinessId")
            .HasPrincipalKey(nameof(SalesInvoice.Id), nameof(SalesInvoice.BusinessId)).OnDelete(DeleteBehavior.Restrict);
}

internal sealed class CounterConfiguration : IEntityTypeConfiguration<Counter>
{
    public void Configure(EntityTypeBuilder<Counter> builder)
    {
        builder.ToTable("counters", t => t.HasCheckConstraint("ck_counters_code", "code ~ '^[A-Z0-9]{1,6}$'"));
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.Id, c.BusinessId });
        builder.Property(c => c.Code).HasMaxLength(6).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(60).IsRequired();
        builder.Property(c => c.RowVersion).IsRowVersion();

        // Invoice numbers must be unique for the GSTIN; a business never shares its GSTINs, so unique per business suffices.
        builder.HasIndex(c => new { c.BusinessId, c.Code }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
    }
}

internal sealed class CounterDeviceConfiguration : IEntityTypeConfiguration<CounterDevice>
{
    public void Configure(EntityTypeBuilder<CounterDevice> builder)
    {
        builder.ToTable("counter_devices");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Name).HasMaxLength(60).IsRequired();
        builder.Property(d => d.TokenHash).IsRequired();
        builder.HasIndex(d => d.TokenHash).IsUnique();
        builder.Ignore(d => d.IsActive);
        builder.BelongsToBusinessInTenant();
        builder.HasCounterInBusiness();
    }
}

internal sealed class SupervisorApprovalConfiguration : IEntityTypeConfiguration<SupervisorApproval>
{
    public void Configure(EntityTypeBuilder<SupervisorApproval> builder)
    {
        builder.ToTable("supervisor_approvals", t =>
        {
            t.HasCheckConstraint("ck_supervisor_approvals_kind",
                "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_discount IS NULL) OR " +
                "(kind = 'DISCOUNT' AND variant_unit_id IS NULL AND approved_price IS NULL AND max_discount > 0)");
            t.HasCheckConstraint("ck_supervisor_approvals_two_people", "approved_by_user_id <> requested_by_user_id");
            t.HasCheckConstraint("ck_supervisor_approvals_use", "(used_at_utc IS NULL) = (used_invoice_id IS NULL)");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Kind).HasMaxLength(20).IsRequired();
        builder.Property(a => a.ApprovedPrice).HasPrecision(18, SalesKeys.Money);
        builder.Property(a => a.MaxDiscount).HasPrecision(18, SalesKeys.Money);
        builder.Property(a => a.Reason).HasMaxLength(200).IsRequired();
        builder.Property(a => a.TokenHash).IsRequired();
        builder.HasIndex(a => a.TokenHash).IsUnique();
        builder.HasIndex(a => a.UsedInvoiceId);
        builder.BelongsToBusinessInTenant();
        builder.HasCounterInBusiness();
    }
}

internal sealed class SalesInvoiceConfiguration : IEntityTypeConfiguration<SalesInvoice>
{
    public void Configure(EntityTypeBuilder<SalesInvoice> builder)
    {
        builder.ToTable("sales_invoices", t =>
        {
            t.HasCheckConstraint("ck_sales_invoices_kind", "kind IN ('TAX_INVOICE', 'BILL_OF_SUPPLY', 'INVOICE')");
            t.HasCheckConstraint("ck_sales_invoices_tax_mode", "tax_mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')");
            t.HasCheckConstraint("ck_sales_invoices_channel", "channel IN ('RETAIL', 'WHOLESALE')");
            t.HasCheckConstraint("ck_sales_invoices_number",
                "char_length(number) <= 16 AND number_prefix ~ '^[A-Z0-9]{1,7}$' AND sequence_number > 0 " +
                "AND number = number_prefix || '-' || CASE WHEN sequence_number < 1000000 THEN lpad(sequence_number::text, 6, '0') ELSE sequence_number::text END");
            t.HasCheckConstraint("ck_sales_invoices_total",
                "grand_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 0.5 AND grand_total = round(grand_total)");
            t.HasCheckConstraint("ck_sales_invoices_gst_split",
                "cgst_total = sgst_total AND (CASE WHEN is_inter_state THEN cgst_total = 0 ELSE igst_total = 0 END)");
            t.HasCheckConstraint("ck_sales_invoices_only_regular_collects_tax",
                "tax_mode = 'GST_REGULAR' OR (cgst_total = 0 AND sgst_total = 0 AND igst_total = 0 AND cess_total = 0)");
            t.HasCheckConstraint("ck_sales_invoices_kind_mode",
                "(tax_mode = 'GST_REGULAR' AND kind IN ('TAX_INVOICE', 'BILL_OF_SUPPLY')) OR (tax_mode = 'GST_COMPOSITION' AND kind = 'BILL_OF_SUPPLY') " +
                "OR (tax_mode = 'NOT_GST_REGISTERED' AND kind = 'INVOICE')");
            t.HasCheckConstraint("ck_sales_invoices_composition_intra_state", "tax_mode <> 'GST_COMPOSITION' OR NOT is_inter_state");
            t.HasCheckConstraint("ck_sales_invoices_paid", "change_due >= 0 AND paid_total - change_due = grand_total");
            t.HasCheckConstraint("ck_sales_invoices_amounts",
                "gross_total >= 0 AND discount_total >= 0 AND taxable_total >= 0 AND cgst_total >= 0 AND igst_total >= 0 AND cess_total >= 0");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.HasAlternateKey(i => new { i.Id, i.BusinessId });
        builder.Property(i => i.Number).HasMaxLength(16).IsRequired();
        builder.Property(i => i.NumberPrefix).HasMaxLength(7).IsRequired();
        builder.Property(i => i.Kind).HasMaxLength(20).IsRequired();
        builder.Property(i => i.TaxMode).HasMaxLength(20).IsRequired();
        builder.Property(i => i.Channel).HasMaxLength(10).IsRequired();
        builder.Property(i => i.SellerName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.SellerGstin).HasMaxLength(15);
        builder.Property(i => i.SellerAddress).HasMaxLength(500).IsRequired();
        builder.Property(i => i.SellerStateCode).HasMaxLength(2).IsRequired();
        builder.Property(i => i.BuyerName).HasMaxLength(100);
        builder.Property(i => i.BuyerGstin).HasMaxLength(15);
        builder.Property(i => i.BuyerPhone).HasMaxLength(20);
        builder.Property(i => i.BuyerAddress).HasMaxLength(300);
        builder.Property(i => i.PlaceOfSupplyStateCode).HasMaxLength(2).IsRequired();
        builder.Property(i => i.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(i => i.RequestHash).HasMaxLength(64).IsRequired();
        foreach (var money in new[]
                 {
                     nameof(SalesInvoice.GrossTotal), nameof(SalesInvoice.DiscountTotal), nameof(SalesInvoice.TaxableTotal), nameof(SalesInvoice.CgstTotal),
                     nameof(SalesInvoice.SgstTotal), nameof(SalesInvoice.IgstTotal), nameof(SalesInvoice.CessTotal), nameof(SalesInvoice.RoundOff),
                     nameof(SalesInvoice.GrandTotal), nameof(SalesInvoice.PaidTotal), nameof(SalesInvoice.ChangeDue),
                 })
        {
            builder.Property<decimal>(money).HasPrecision(18, SalesKeys.Money);
        }

        builder.HasIndex(i => new { i.BusinessId, i.IdempotencyKey }).IsUnique();
        builder.HasIndex(i => new { i.CounterId, i.NumberPrefix, i.SequenceNumber }).IsUnique();
        builder.HasIndex(i => new { i.BusinessId, i.Number }).IsUnique();
        builder.HasIndex(i => new { i.StoreId, i.BusinessDate });
        builder.HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).HasPrincipalKey(i => i.Id).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(i => i.Payments).WithOne().HasForeignKey(p => p.InvoiceId).HasPrincipalKey(i => i.Id).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(i => i.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasCounterInBusiness();
    }
}

internal sealed class SalesInvoiceLineConfiguration : IEntityTypeConfiguration<SalesInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLine> builder)
    {
        builder.ToTable("sales_invoice_lines", t =>
        {
            t.HasCheckConstraint("ck_sales_invoice_lines_total", "total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)");
            t.HasCheckConstraint("ck_sales_invoice_lines_net",
                "gross - item_discount - bill_discount = CASE WHEN tax_inclusive OR cgst + sgst + igst + cess = 0 THEN total ELSE taxable END");
            t.HasCheckConstraint("ck_sales_invoice_lines_amounts",
                "quantity > 0 AND base_quantity > 0 AND unit_price >= 0 AND item_discount >= 0 AND bill_discount >= 0 AND taxable >= 0 AND cgst >= 0 AND igst >= 0 AND cess >= 0");
            t.HasCheckConstraint("ck_sales_invoice_lines_supply_type", "supply_type IN ('TAXABLE', 'EXEMPT', 'NIL_RATED', 'NON_GST')");
            t.HasCheckConstraint("ck_sales_invoice_lines_untaxed", "supply_type = 'TAXABLE' OR (cgst = 0 AND igst = 0 AND cess = 0)");
            // Every price is explained: a price rule, a supervisor's approval, or an override by someone allowed to override.
            t.HasCheckConstraint("ck_sales_invoice_lines_price_source",
                "(rate_type = 'OVERRIDE' AND price_override_approval_id IS NOT NULL AND price_rule_id IS NULL) OR " +
                "(rate_type = 'OVERRIDE_SELF' AND price_override_approval_id IS NULL AND price_rule_id IS NULL) OR " +
                "(rate_type NOT IN ('OVERRIDE', 'OVERRIDE_SELF') AND price_rule_id IS NOT NULL AND price_override_approval_id IS NULL)");
        });
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Description).HasMaxLength(200).IsRequired();
        builder.Property(l => l.HsnSac).HasMaxLength(8).IsRequired();
        builder.Property(l => l.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(l => l.Quantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(l => l.BaseQuantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(l => l.Mrp).HasPrecision(18, SalesKeys.Money);
        builder.Property(l => l.RateType).HasMaxLength(20).IsRequired();
        builder.Property(l => l.UnitPrice).HasPrecision(18, SalesKeys.Money);
        builder.Property(l => l.SupplyType).HasMaxLength(12).IsRequired();
        builder.Property(l => l.GstRatePercent).HasPrecision(5, 2);
        builder.Property(l => l.CessRatePercent).HasPrecision(5, 2);
        foreach (var money in new[]
                 {
                     nameof(SalesInvoiceLine.Gross), nameof(SalesInvoiceLine.ItemDiscount), nameof(SalesInvoiceLine.BillDiscount), nameof(SalesInvoiceLine.Taxable),
                     nameof(SalesInvoiceLine.Cgst), nameof(SalesInvoiceLine.Sgst), nameof(SalesInvoiceLine.Igst), nameof(SalesInvoiceLine.Cess), nameof(SalesInvoiceLine.Total),
                 })
        {
            builder.Property<decimal>(money).HasPrecision(18, SalesKeys.Money);
        }

        builder.Property(l => l.CostOfGoods).HasPrecision(18, InventoryKeys.Cost);
        builder.HasIndex(l => new { l.InvoiceId, l.LineNumber }).IsUnique();
        builder.HasIndex(l => new { l.VariantId, l.InvoiceId });
        builder.BelongsToBusinessInTenant();
        builder.HasInvoiceInBusiness();
        builder.HasVariantInBusiness();
        builder.HasOne<VariantUnit>().WithMany().HasForeignKey(l => l.VariantUnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalesInvoicePaymentConfiguration : IEntityTypeConfiguration<SalesInvoicePayment>
{
    public void Configure(EntityTypeBuilder<SalesInvoicePayment> builder)
    {
        builder.ToTable("sales_invoice_payments", t =>
        {
            t.HasCheckConstraint("ck_sales_invoice_payments_method", "method IN ('CASH', 'CARD', 'UPI', 'WALLET')");
            t.HasCheckConstraint("ck_sales_invoice_payments_amount", "amount > 0");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Method).HasMaxLength(10).IsRequired();
        builder.Property(p => p.Amount).HasPrecision(18, SalesKeys.Money);
        builder.Property(p => p.Reference).HasMaxLength(60);
        builder.HasIndex(p => new { p.InvoiceId, p.PaymentOrder }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasInvoiceInBusiness();
    }
}
