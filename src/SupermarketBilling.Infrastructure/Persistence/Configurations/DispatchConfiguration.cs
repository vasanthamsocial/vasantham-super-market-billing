using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Dispatch;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal sealed class TransporterConfiguration : IEntityTypeConfiguration<Transporter>
{
    public void Configure(EntityTypeBuilder<Transporter> builder)
    {
        builder.ToTable("transporters", t => t.HasCheckConstraint("ck_transporters_code", "code ~ '^[A-Z0-9-]{1,20}$'"));
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.HasAlternateKey(t => new { t.Id, t.BusinessId });
        builder.Property(t => t.Code).HasMaxLength(20).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Gstin).HasMaxLength(15).IsFixedLength();
        builder.Property(t => t.Phone).HasMaxLength(20);
        builder.Property(t => t.Address).HasMaxLength(300);
        builder.Property(t => t.RowVersion).IsRowVersion();
        builder.HasIndex(t => new { t.BusinessId, t.Code }).IsUnique();
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class TransporterBranchConfiguration : IEntityTypeConfiguration<TransporterBranch>
{
    public void Configure(EntityTypeBuilder<TransporterBranch> builder)
    {
        builder.ToTable("transporter_branches", t => t.HasCheckConstraint("ck_transporter_branches_role", "is_booking_office OR is_destination"));
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.HasAlternateKey(b => new { b.Id, b.TransporterId, b.BusinessId });
        builder.Property(b => b.Name).HasMaxLength(100).IsRequired();
        builder.Property(b => b.City).HasMaxLength(60).IsRequired();
        builder.Property(b => b.Address).HasMaxLength(300);
        builder.Property(b => b.Phone).HasMaxLength(20);
        builder.Property(b => b.RowVersion).IsRowVersion();
        builder.HasIndex(b => new { b.TransporterId, b.Name, b.City }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Transporter>().WithMany().HasForeignKey(b => new { b.TransporterId, b.BusinessId })
            .HasPrincipalKey(t => new { t.Id, t.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TransporterRouteConfiguration : IEntityTypeConfiguration<TransporterRoute>
{
    public void Configure(EntityTypeBuilder<TransporterRoute> builder)
    {
        builder.ToTable("transporter_routes", t =>
        {
            t.HasCheckConstraint("ck_transporter_routes_branches", "from_branch_id <> to_branch_id");
            t.HasCheckConstraint("ck_transporter_routes_transit", "transit_days BETWEEN 0 AND 60");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.RowVersion).IsRowVersion();
        builder.HasIndex(r => new { r.TransporterId, r.FromBranchId, r.ToBranchId }).IsUnique();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Transporter>().WithMany().HasForeignKey(r => new { r.TransporterId, r.BusinessId })
            .HasPrincipalKey(t => new { t.Id, t.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        // Both ends belong to the same transporter, checked by the keys.
        builder.HasOne<TransporterBranch>().WithMany().HasForeignKey(r => new { r.FromBranchId, r.TransporterId, r.BusinessId })
            .HasPrincipalKey(b => new { b.Id, b.TransporterId, b.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TransporterBranch>().WithMany().HasForeignKey(r => new { r.ToBranchId, r.TransporterId, r.BusinessId })
            .HasPrincipalKey(b => new { b.Id, b.TransporterId, b.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InvoiceFulfilmentConfiguration : IEntityTypeConfiguration<InvoiceFulfilment>
{
    public void Configure(EntityTypeBuilder<InvoiceFulfilment> builder)
    {
        builder.ToTable("invoice_fulfilments", t =>
        {
            t.HasCheckConstraint("ck_invoice_fulfilments_mode", "mode IN ('PICKUP', 'OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY')");
            t.HasCheckConstraint("ck_invoice_fulfilments_details",
                "(mode = 'PICKUP' OR delivery_address IS NOT NULL) AND ((mode = 'LORRY') = (transporter_id IS NOT NULL)) " +
                "AND (destination_branch_id IS NULL OR transporter_id IS NOT NULL)");
        });
        builder.HasKey(f => f.InvoiceId);
        builder.HasAlternateKey(f => new { f.InvoiceId, f.BusinessId });
        builder.Property(f => f.Mode).HasMaxLength(20).IsRequired();
        builder.Property(f => f.DeliveryAddress).HasMaxLength(500);
        builder.Property(f => f.ContactPhone).HasMaxLength(20);
        builder.Property(f => f.Note).HasMaxLength(300);
        builder.Property(f => f.RowVersion).IsRowVersion();
        builder.HasIndex(f => new { f.BusinessId, f.Mode });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<SalesInvoice>().WithOne().HasForeignKey<InvoiceFulfilment>(f => new { f.InvoiceId, f.BusinessId })
            .HasPrincipalKey<SalesInvoice>(i => new { i.Id, i.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Store>().WithMany().HasForeignKey(f => new { f.StoreId, f.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Transporter>().WithMany().HasForeignKey(f => new { f.TransporterId, f.BusinessId })
            .HasPrincipalKey(t => new { t.Id, t.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TransporterBranch>().WithMany().HasForeignKey(f => new { f.DestinationBranchId, f.TransporterId, f.BusinessId })
            .HasPrincipalKey(b => new { b.Id, b.TransporterId, b.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(f => f.ChosenByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ConsignmentConfiguration : IEntityTypeConfiguration<Consignment>
{
    public void Configure(EntityTypeBuilder<Consignment> builder)
    {
        builder.ToTable("consignments", t =>
        {
            t.HasCheckConstraint("ck_consignments_mode", "mode IN ('OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY') AND status IN ('DISPATCHED', 'CANCELLED')");
            t.HasCheckConstraint("ck_consignments_lorry",
                "(mode = 'LORRY') = (transporter_id IS NOT NULL) AND (mode <> 'LORRY' OR (booking_branch_id IS NOT NULL AND destination_branch_id IS NOT NULL " +
                "AND lr_number IS NOT NULL AND lr_date IS NOT NULL AND lr_date <= dispatch_date AND freight_terms IN ('PAID', 'TO_PAY'))) " +
                "AND (mode = 'LORRY' OR (lr_number IS NULL AND freight_terms IS NULL AND freight_amount = 0))");
            t.HasCheckConstraint("ck_consignments_trip", "(mode <> 'OWN_VEHICLE' OR vehicle_number IS NOT NULL) AND (mode <> 'LOCAL_DELIVERY' OR driver_name IS NOT NULL)");
            t.HasCheckConstraint("ck_consignments_values",
                "package_count BETWEEN 1 AND 9999 AND (weight_kg IS NULL OR weight_kg > 0) AND freight_amount >= 0 AND goods_value >= 0 " +
                "AND (expected_delivery_date IS NULL OR expected_delivery_date >= dispatch_date) AND (eway_bill_number IS NULL OR eway_bill_number ~ '^[0-9]{12}$')");
            t.HasCheckConstraint("ck_consignments_cancel",
                "(status = 'CANCELLED') = (cancel_reason IS NOT NULL AND cancelled_by_user_id IS NOT NULL AND cancelled_at_utc IS NOT NULL)");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.Id, c.BusinessId });
        builder.Ignore(c => c.InvoiceIds);
        builder.Ignore(c => c.EwayBillMissing);
        builder.Property(c => c.Number).HasMaxLength(40).IsRequired();
        builder.Property(c => c.Mode).HasMaxLength(20).IsRequired();
        builder.Property(c => c.PartyName).HasMaxLength(200).IsRequired();
        builder.Property(c => c.DeliveryAddress).HasMaxLength(500).IsRequired();
        builder.Property(c => c.TransporterName).HasMaxLength(100);
        builder.Property(c => c.TransporterGstin).HasMaxLength(15).IsFixedLength();
        builder.Property(c => c.BookingOffice).HasMaxLength(170);
        builder.Property(c => c.DestinationBranch).HasMaxLength(170);
        builder.Property(c => c.VehicleNumber).HasMaxLength(12);
        builder.Property(c => c.DriverName).HasMaxLength(100);
        builder.Property(c => c.DriverPhone).HasMaxLength(20);
        builder.Property(c => c.LrNumber).HasMaxLength(30);
        builder.Property(c => c.WeightKg).HasPrecision(18, 3);
        builder.Property(c => c.FreightTerms).HasMaxLength(10);
        builder.Property(c => c.FreightAmount).HasPrecision(18, 2);
        builder.Property(c => c.EwayBillNumber).HasMaxLength(12);
        builder.Property(c => c.GoodsValue).HasPrecision(18, 2);
        builder.Property(c => c.Status).HasMaxLength(20).IsRequired();
        builder.Property(c => c.CancelReason).HasMaxLength(300);
        builder.Property(c => c.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(c => c.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.RowVersion).IsRowVersion();
        builder.HasIndex(c => new { c.BusinessId, c.Number }).IsUnique();
        builder.HasIndex(c => new { c.BusinessId, c.IdempotencyKey }).IsUnique();
        // One LR/GR number per lorry service, among dispatches that stand (a cancelled one frees its number).
        builder.HasIndex(c => new { c.BusinessId, c.TransporterId, c.LrNumber }).IsUnique()
            .HasFilter("lr_number IS NOT NULL AND status = 'DISPATCHED'").HasDatabaseName("ux_consignments_lr");
        builder.HasIndex(c => new { c.BusinessId, c.DispatchDate });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Store>().WithMany().HasForeignKey(c => new { c.StoreId, c.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Transporter>().WithMany().HasForeignKey(c => new { c.TransporterId, c.BusinessId })
            .HasPrincipalKey(t => new { t.Id, t.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TransporterBranch>().WithMany().HasForeignKey(c => new { c.BookingBranchId, c.TransporterId, c.BusinessId })
            .HasPrincipalKey(b => new { b.Id, b.TransporterId, b.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TransporterBranch>().WithMany().HasForeignKey(c => new { c.DestinationBranchId, c.TransporterId, c.BusinessId })
            .HasPrincipalKey(b => new { b.Id, b.TransporterId, b.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(c => c.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Identity.User>().WithMany().HasForeignKey(c => c.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ConsignmentInvoiceConfiguration : IEntityTypeConfiguration<ConsignmentInvoice>
{
    public void Configure(EntityTypeBuilder<ConsignmentInvoice> builder)
    {
        builder.ToTable("consignment_invoices");
        builder.HasKey(c => new { c.ConsignmentId, c.InvoiceId });
        builder.HasIndex(c => c.InvoiceId);
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Consignment>().WithMany().HasForeignKey(c => new { c.ConsignmentId, c.BusinessId })
            .HasPrincipalKey(c => new { c.Id, c.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        // A bill is dispatched only once its delivery has been chosen.
        builder.HasOne<InvoiceFulfilment>().WithMany().HasForeignKey(c => new { c.InvoiceId, c.BusinessId })
            .HasPrincipalKey(f => new { f.InvoiceId, f.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeliveryPreferenceConfiguration : IEntityTypeConfiguration<DeliveryPreference>
{
    public void Configure(EntityTypeBuilder<DeliveryPreference> builder)
    {
        builder.ToTable("delivery_preferences", t =>
        {
            t.HasCheckConstraint("ck_delivery_preferences_mode", "mode IN ('PICKUP', 'OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY')");
            t.HasCheckConstraint("ck_delivery_preferences_lorry", "(mode = 'LORRY') = (transporter_id IS NOT NULL) AND (destination_branch_id IS NULL OR transporter_id IS NOT NULL)");
        });
        builder.HasKey(p => p.DebtorId);
        builder.Property(p => p.Mode).HasMaxLength(20).IsRequired();
        builder.Property(p => p.DeliveryAddress).HasMaxLength(500);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Debtor>().WithOne().HasForeignKey<DeliveryPreference>(p => new { p.DebtorId, p.BusinessId })
            .HasPrincipalKey<Debtor>(d => new { d.Id, d.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Transporter>().WithMany().HasForeignKey(p => new { p.TransporterId, p.BusinessId })
            .HasPrincipalKey(t => new { t.Id, t.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TransporterBranch>().WithMany().HasForeignKey(p => new { p.DestinationBranchId, p.TransporterId, p.BusinessId })
            .HasPrincipalKey(b => new { b.Id, b.TransporterId, b.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}
