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
                "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR " +
                "(kind IN ('DISCOUNT', 'RETURN', 'PAY_OUT') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0)");
            t.HasCheckConstraint("ck_supervisor_approvals_two_people", "approved_by_user_id <> requested_by_user_id");
            t.HasCheckConstraint("ck_supervisor_approvals_use", "(used_at_utc IS NULL) = (used_document_id IS NULL)");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Kind).HasMaxLength(20).IsRequired();
        builder.Property(a => a.ApprovedPrice).HasPrecision(18, SalesKeys.Money);
        builder.Property(a => a.MaxAmount).HasPrecision(18, SalesKeys.Money);
        builder.Property(a => a.Reason).HasMaxLength(200).IsRequired();
        builder.Property(a => a.TokenHash).IsRequired();
        builder.HasIndex(a => a.TokenHash).IsUnique();
        builder.HasIndex(a => a.UsedDocumentId);
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
        builder.HasIndex(i => i.ShiftId);
        builder.HasOne<Shift>().WithMany().HasForeignKey(i => i.ShiftId).OnDelete(DeleteBehavior.Restrict);
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
            t.HasCheckConstraint("ck_sales_invoice_payments_method", "method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'CREDIT_NOTE')");
            t.HasCheckConstraint("ck_sales_invoice_payments_credit_note", "method <> 'CREDIT_NOTE' OR reference IS NOT NULL");
            t.HasCheckConstraint("ck_sales_invoice_payments_amount", "amount > 0");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Method).HasMaxLength(12).IsRequired();
        builder.Property(p => p.Amount).HasPrecision(18, SalesKeys.Money);
        builder.Property(p => p.Reference).HasMaxLength(60);
        builder.HasIndex(p => new { p.InvoiceId, p.PaymentOrder }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasInvoiceInBusiness();
    }
}

internal sealed class ParkedBillConfiguration : IEntityTypeConfiguration<ParkedBill>
{
    public void Configure(EntityTypeBuilder<ParkedBill> builder)
    {
        builder.ToTable("parked_bills", t => t.HasCheckConstraint("ck_parked_bills_items", "item_count BETWEEN 1 AND 300"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Label).HasMaxLength(40);
        builder.Property(p => p.CartJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(p => new { p.CounterId, p.ParkedAtUtc });
        builder.BelongsToBusinessInTenant();
        builder.HasCounterInBusiness();
    }
}

internal sealed class SalesReturnConfiguration : IEntityTypeConfiguration<SalesReturn>
{
    public void Configure(EntityTypeBuilder<SalesReturn> builder)
    {
        builder.ToTable("sales_returns", t =>
        {
            t.HasCheckConstraint("ck_sales_returns_number",
                "char_length(number) <= 16 AND number_prefix ~ '^[A-Z0-9]{1,7}$' AND sequence_number > 0 AND number = number_prefix || '/CN' || " +
                "CASE WHEN sequence_number < 1000000 THEN lpad(sequence_number::text, 6, '0') ELSE sequence_number::text END");
            t.HasCheckConstraint("ck_sales_returns_total",
                "grand_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 0.5 AND grand_total = round(grand_total) AND grand_total >= 0");
            t.HasCheckConstraint("ck_sales_returns_gst_split", "cgst_total = sgst_total AND (CASE WHEN is_inter_state THEN cgst_total = 0 ELSE igst_total = 0 END)");
            t.HasCheckConstraint("ck_sales_returns_store_credit", "store_credit >= 0 AND store_credit <= grand_total");
            t.HasCheckConstraint("ck_sales_returns_tax_mode", "tax_mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasAlternateKey(r => new { r.Id, r.BusinessId });
        builder.Property(r => r.Number).HasMaxLength(16).IsRequired();
        builder.Property(r => r.NumberPrefix).HasMaxLength(7).IsRequired();
        builder.Property(r => r.OriginalInvoiceNumber).HasMaxLength(16).IsRequired();
        builder.Property(r => r.TaxMode).HasMaxLength(20).IsRequired();
        builder.Property(r => r.PlaceOfSupplyStateCode).HasMaxLength(2).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(200).IsRequired();
        builder.Property(r => r.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
        foreach (var money in new[]
                 {
                     nameof(SalesReturn.TaxableTotal), nameof(SalesReturn.CgstTotal), nameof(SalesReturn.SgstTotal), nameof(SalesReturn.IgstTotal),
                     nameof(SalesReturn.CessTotal), nameof(SalesReturn.RoundOff), nameof(SalesReturn.GrandTotal), nameof(SalesReturn.StoreCredit),
                 })
        {
            builder.Property<decimal>(money).HasPrecision(18, SalesKeys.Money);
        }

        builder.HasIndex(r => new { r.BusinessId, r.IdempotencyKey }).IsUnique();
        builder.HasIndex(r => new { r.BusinessId, r.Number }).IsUnique();
        builder.HasIndex(r => new { r.CounterId, r.NumberPrefix, r.SequenceNumber }).IsUnique();
        builder.HasIndex(r => r.OriginalInvoiceId);
        builder.HasIndex(r => r.ShiftId);
        builder.HasOne<Shift>().WithMany().HasForeignKey(r => r.ShiftId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.ReturnId).HasPrincipalKey(r => r.Id).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(r => r.Refunds).WithOne().HasForeignKey(p => p.ReturnId).HasPrincipalKey(r => r.Id).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(r => r.Refunds).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasCounterInBusiness();
        builder.HasOne<SalesInvoice>().WithMany().HasForeignKey(r => new { r.OriginalInvoiceId, r.BusinessId })
            .HasPrincipalKey(i => new { i.Id, i.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalesReturnLineConfiguration : IEntityTypeConfiguration<SalesReturnLine>
{
    public void Configure(EntityTypeBuilder<SalesReturnLine> builder)
    {
        builder.ToTable("sales_return_lines", t =>
        {
            t.HasCheckConstraint("ck_sales_return_lines_total", "total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)");
            t.HasCheckConstraint("ck_sales_return_lines_amounts",
                "quantity > 0 AND base_quantity > 0 AND taxable >= 0 AND cgst >= 0 AND igst >= 0 AND cess >= 0 AND cost_returned >= 0");
            t.HasCheckConstraint("ck_sales_return_lines_restock", "restocked OR cost_returned = 0");
        });
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Quantity).HasPrecision(18, InventoryKeys.Quantity);
        builder.Property(l => l.BaseQuantity).HasPrecision(18, InventoryKeys.Quantity);
        foreach (var money in new[]
                 {
                     nameof(SalesReturnLine.Gross), nameof(SalesReturnLine.ItemDiscount), nameof(SalesReturnLine.BillDiscount), nameof(SalesReturnLine.Taxable),
                     nameof(SalesReturnLine.Cgst), nameof(SalesReturnLine.Sgst), nameof(SalesReturnLine.Igst), nameof(SalesReturnLine.Cess), nameof(SalesReturnLine.Total),
                 })
        {
            builder.Property<decimal>(money).HasPrecision(18, SalesKeys.Money);
        }

        builder.Property(l => l.CostReturned).HasPrecision(18, InventoryKeys.Cost);
        builder.HasIndex(l => new { l.ReturnId, l.LineNumber }).IsUnique();
        builder.HasIndex(l => l.OriginalLineId);
        builder.BelongsToBusinessInTenant();
        builder.HasOne<SalesReturn>().WithMany().HasForeignKey(l => new { l.ReturnId, l.BusinessId })
            .HasPrincipalKey(r => new { r.Id, r.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SalesInvoiceLine>().WithMany().HasForeignKey(l => l.OriginalLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasVariantInBusiness();
    }
}

internal sealed class SalesReturnRefundConfiguration : IEntityTypeConfiguration<SalesReturnRefund>
{
    public void Configure(EntityTypeBuilder<SalesReturnRefund> builder)
    {
        builder.ToTable("sales_return_refunds", t =>
        {
            t.HasCheckConstraint("ck_sales_return_refunds_method", "method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'STORE_CREDIT')");
            t.HasCheckConstraint("ck_sales_return_refunds_amount", "amount > 0");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Method).HasMaxLength(12).IsRequired();
        builder.Property(p => p.Amount).HasPrecision(18, SalesKeys.Money);
        builder.Property(p => p.Reference).HasMaxLength(60);
        builder.HasIndex(p => new { p.ReturnId, p.RefundOrder }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<SalesReturn>().WithMany().HasForeignKey(p => new { p.ReturnId, p.BusinessId })
            .HasPrincipalKey(r => new { r.Id, r.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CreditNoteRedemptionConfiguration : IEntityTypeConfiguration<CreditNoteRedemption>
{
    public void Configure(EntityTypeBuilder<CreditNoteRedemption> builder)
    {
        builder.ToTable("credit_note_redemptions", t => t.HasCheckConstraint("ck_credit_note_redemptions_amount", "amount > 0"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Amount).HasPrecision(18, SalesKeys.Money);
        builder.HasIndex(r => r.ReturnId);
        builder.HasIndex(r => r.InvoiceId);
        builder.BelongsToBusinessInTenant();
        builder.HasOne<SalesReturn>().WithMany().HasForeignKey(r => new { r.ReturnId, r.BusinessId })
            .HasPrincipalKey(x => new { x.Id, x.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasInvoiceInBusiness();
    }
}

internal sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        builder.ToTable("shifts", t =>
        {
            t.HasCheckConstraint("ck_shifts_status", "status IN ('OPEN', 'CLOSED')");
            t.HasCheckConstraint("ck_shifts_float", "opening_float >= 0");
            t.HasCheckConstraint("ck_shifts_close",
                "(status = 'OPEN' AND closed_at_utc IS NULL AND expected_cash IS NULL AND counted_cash IS NULL AND difference IS NULL) OR " +
                "(status = 'CLOSED' AND closed_at_utc IS NOT NULL AND expected_cash IS NOT NULL AND counted_cash >= 0 AND difference = counted_cash - expected_cash)");
            t.HasCheckConstraint("ck_shifts_note", "difference IS NULL OR difference = 0 OR close_note IS NOT NULL");
            t.HasCheckConstraint("ck_shifts_review",
                "(reviewed_by_user_id IS NULL) = (reviewed_at_utc IS NULL) AND (reviewed_by_user_id IS NULL OR (reviewed_by_user_id <> cashier_user_id AND reviewed_by_user_id <> closed_by_user_id))");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Status).HasMaxLength(10).IsRequired();
        foreach (var money in new[] { nameof(Shift.OpeningFloat), nameof(Shift.ExpectedCash), nameof(Shift.CountedCash), nameof(Shift.Difference) })
        {
            builder.Property(money).HasPrecision(18, SalesKeys.Money);
        }

        builder.Property(s => s.CloseNote).HasMaxLength(300);
        builder.Property(s => s.ReviewNote).HasMaxLength(300);
        builder.Property(s => s.RowVersion).IsRowVersion();
        builder.Ignore(s => s.NeedsReview);

        // One open shift per counter, and one per cashier.
        builder.HasIndex(s => s.CounterId).IsUnique().HasFilter("status = 'OPEN'").HasDatabaseName("ux_shifts_open_per_counter");
        builder.HasIndex(s => s.CashierUserId).IsUnique().HasFilter("status = 'OPEN'").HasDatabaseName("ux_shifts_open_per_cashier");
        builder.HasIndex(s => new { s.StoreId, s.BusinessDate });
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasCounterInBusiness();
    }
}

internal sealed class ShiftCountConfiguration : IEntityTypeConfiguration<ShiftCount>
{
    public void Configure(EntityTypeBuilder<ShiftCount> builder)
    {
        builder.ToTable("shift_counts", t =>
        {
            t.HasCheckConstraint("ck_shift_counts_kind", "kind IN ('OPENING', 'CLOSING')");
            t.HasCheckConstraint("ck_shift_counts_values", "denomination IN (2000, 500, 200, 100, 50, 20, 10, 5, 2, 1) AND count > 0");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Kind).HasMaxLength(10).IsRequired();
        builder.Property(c => c.Denomination).HasPrecision(18, SalesKeys.Money);
        builder.HasIndex(c => new { c.ShiftId, c.Kind, c.Denomination }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Shift>().WithMany().HasForeignKey(c => c.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> builder)
    {
        builder.ToTable("cash_movements", t =>
        {
            t.HasCheckConstraint("ck_cash_movements_kind", "kind IN ('PAY_IN', 'PAY_OUT', 'DROP')");
            t.HasCheckConstraint("ck_cash_movements_amount", "amount > 0");
        });
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Kind).HasMaxLength(10).IsRequired();
        builder.Property(m => m.Amount).HasPrecision(18, SalesKeys.Money);
        builder.Property(m => m.Reason).HasMaxLength(200).IsRequired();
        builder.HasIndex(m => m.ShiftId);
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Shift>().WithMany().HasForeignKey(m => m.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}
