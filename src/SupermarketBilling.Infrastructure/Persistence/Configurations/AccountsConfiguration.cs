using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Purchases;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal static class PartyColumns
{
    /// <summary>The contact columns suppliers and debtors share.</summary>
    public static void Contacts<T>(EntityTypeBuilder<T> builder)
        where T : class
    {
        builder.Property<string?>("TradeName").HasMaxLength(200);
        builder.Property<string?>("ContactPerson").HasMaxLength(100);
        builder.Property<string?>("Email").HasMaxLength(200);
        builder.Property<string?>("WhatsAppNumber").HasColumnName("whatsapp_number").HasMaxLength(13);
        builder.Property<string?>("SmsNumber").HasMaxLength(13);
        builder.Property<bool>("WhatsAppConsent").HasColumnName("whatsapp_consent");
    }

    public static void Ledger<T>(EntityTypeBuilder<T> builder, string table, string partyColumn, string types)
        where T : PartyLedgerEntry
    {
        builder.ToTable(table, t =>
        {
            t.HasCheckConstraint($"ck_{table}_type", $"entry_type IN ({types})");
            t.HasCheckConstraint($"ck_{table}_amount", "amount <> 0 AND sequence > 0");
            t.HasCheckConstraint($"ck_{table}_sign",
                "(entry_type NOT IN ('GRN', 'INVOICE') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)");
            t.HasCheckConstraint($"ck_{table}_due", "(amount > 0) = (due_date IS NOT NULL)");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.PartyId).HasColumnName(partyColumn);
        builder.HasAlternateKey(e => new { e.Id, e.PartyId });
        builder.Property(e => e.EntryType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.DocumentNumber).HasMaxLength(40);
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.BalanceAfter).HasPrecision(18, 2);
        builder.Property(e => e.Narration).HasMaxLength(300).IsRequired();
        builder.Ignore(e => e.IsCharge);
        builder.HasIndex(e => new { e.PartyId, e.Sequence }).IsUnique();
        builder.HasIndex(e => new { e.DocumentId, e.EntryType });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Domain.Organisation.Store>().WithMany().HasForeignKey(e => new { e.StoreId, e.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }

    public static void Settlement<TSettlement, TEntry>(EntityTypeBuilder<TSettlement> builder, string table, string partyColumn)
        where TSettlement : PartySettlement
        where TEntry : PartyLedgerEntry
    {
        builder.ToTable(table, t => t.HasCheckConstraint($"ck_{table}_amount", "amount > 0 AND charge_entry_id <> payment_entry_id"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.PartyId).HasColumnName(partyColumn);
        builder.Property(s => s.Amount).HasPrecision(18, 2);
        builder.HasIndex(s => s.ChargeEntryId);
        builder.HasIndex(s => s.PaymentEntryId);
        builder.BelongsToBusinessInTenant();

        // Both entries belong to the same account as the settlement.
        builder.HasOne<TEntry>().WithMany().HasForeignKey(s => new { s.ChargeEntryId, s.PartyId })
            .HasPrincipalKey(e => new { e.Id, e.PartyId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TEntry>().WithMany().HasForeignKey(s => new { s.PaymentEntryId, s.PartyId })
            .HasPrincipalKey(e => new { e.Id, e.PartyId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DebtorConfiguration : IEntityTypeConfiguration<Debtor>
{
    public void Configure(EntityTypeBuilder<Debtor> builder)
    {
        builder.ToTable("debtors", t =>
        {
            t.HasCheckConstraint("ck_debtors_code", "code ~ '^[A-Z0-9-]{1,20}$'");
            t.HasCheckConstraint("ck_debtors_gstin_state", "gstin IS NULL OR left(gstin, 2) = state_code");
            t.HasCheckConstraint("ck_debtors_status", "status IN ('ACTIVE', 'ON_HOLD', 'CLOSED')");
            t.HasCheckConstraint("ck_debtors_credit", "credit_limit >= 0 AND credit_period_days BETWEEN 0 AND 365");
            t.HasCheckConstraint("ck_debtors_consent", "(NOT whatsapp_consent OR whatsapp_number IS NOT NULL) AND (NOT sms_consent OR sms_number IS NOT NULL)");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.HasAlternateKey(d => new { d.Id, d.BusinessId });
        builder.Property(d => d.Code).HasMaxLength(20).IsRequired();
        builder.Property(d => d.LegalName).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Gstin).HasMaxLength(15);
        builder.Property(d => d.StateCode).HasMaxLength(2).IsRequired();
        builder.Property(d => d.Address).HasMaxLength(500);
        builder.Property(d => d.Phone).HasMaxLength(20);
        builder.Property(d => d.CreditLimit).HasPrecision(18, 2);
        builder.Property(d => d.Status).HasMaxLength(10).IsRequired();
        builder.Property(d => d.RowVersion).IsRowVersion();
        builder.Ignore(d => d.DisplayName);
        PartyColumns.Contacts(builder);
        builder.HasIndex(d => new { d.BusinessId, d.Code }).IsUnique();
        builder.HasIndex(d => new { d.BusinessId, d.Gstin });
        builder.BelongsToBusinessInTenant();
        builder.HasOne<CustomerGroup>().WithMany().HasForeignKey(d => new { d.CustomerGroupId, d.BusinessId })
            .HasPrincipalKey(g => new { g.Id, g.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SupplierLedgerConfiguration : IEntityTypeConfiguration<SupplierLedgerEntry>
{
    public void Configure(EntityTypeBuilder<SupplierLedgerEntry> builder)
    {
        PartyColumns.Ledger(builder, "supplier_ledger", "supplier_id", "'OPENING', 'GRN', 'PAYMENT', 'DEBIT_NOTE', 'ADJUSTMENT'");
        builder.HasOne<Supplier>().WithMany().HasForeignKey(e => new { e.PartyId, e.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DebtorLedgerConfiguration : IEntityTypeConfiguration<DebtorLedgerEntry>
{
    public void Configure(EntityTypeBuilder<DebtorLedgerEntry> builder)
    {
        PartyColumns.Ledger(builder, "debtor_ledger", "debtor_id", "'OPENING', 'INVOICE', 'RECEIPT', 'CREDIT_NOTE', 'ADJUSTMENT'");
        builder.HasOne<Debtor>().WithMany().HasForeignKey(e => new { e.PartyId, e.BusinessId })
            .HasPrincipalKey(d => new { d.Id, d.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SupplierSettlementConfiguration : IEntityTypeConfiguration<SupplierSettlement>
{
    public void Configure(EntityTypeBuilder<SupplierSettlement> builder) =>
        PartyColumns.Settlement<SupplierSettlement, SupplierLedgerEntry>(builder, "supplier_settlements", "supplier_id");
}

internal sealed class DebtorSettlementConfiguration : IEntityTypeConfiguration<DebtorSettlement>
{
    public void Configure(EntityTypeBuilder<DebtorSettlement> builder) =>
        PartyColumns.Settlement<DebtorSettlement, DebtorLedgerEntry>(builder, "debtor_settlements", "debtor_id");
}

internal sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> builder)
    {
        builder.ToTable("supplier_payments", t =>
        {
            t.HasCheckConstraint("ck_supplier_payments_amount", "amount > 0");
            t.HasCheckConstraint("ck_supplier_payments_method", "method IN ('CASH', 'BANK_TRANSFER', 'UPI', 'CHEQUE')");
            t.HasCheckConstraint("ck_supplier_payments_cheque", "method <> 'CHEQUE' OR reference IS NOT NULL");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Number).HasMaxLength(40).IsRequired();
        builder.Property(p => p.Method).HasMaxLength(20).IsRequired();
        builder.Property(p => p.Reference).HasMaxLength(40);
        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.Note).HasMaxLength(300).IsRequired();
        builder.Property(p => p.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(p => p.RequestHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(p => new { p.StoreId, p.SequenceNumber }).IsUnique();
        builder.HasIndex(p => new { p.BusinessId, p.IdempotencyKey }).IsUnique();
        builder.HasIndex(p => new { p.SupplierId, p.PaymentDate });
        builder.BelongsToBusinessInTenant();
        builder.HasStoreInBusiness();
        builder.HasOne<Supplier>().WithMany().HasForeignKey(p => new { p.SupplierId, p.BusinessId })
            .HasPrincipalKey(s => new { s.Id, s.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}
