using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Auditing;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Tenancy;

/// <summary>
/// This installation's identity (single row). An edge installation records the one tenant it serves once
/// first-time setup has run; that is how it knows the tenant before anyone signs in. Not tenant-owned.
/// </summary>
public sealed class Installation
{
    public const short SingletonId = 1;

    private Installation()
    {
    }

    public short Id { get; private set; }

    /// <summary>Stable identity of this server, used later for cloud sync and licensing.</summary>
    public Guid InstallationId { get; private set; }

    public Guid? TenantId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public void AssignTenant(Guid tenantId)
    {
        if (TenantId is not null)
        {
            throw new InvalidOperationException("This installation already belongs to a tenant.");
        }

        TenantId = tenantId;
    }
}

internal sealed class InstallationConfiguration : IEntityTypeConfiguration<Installation>
{
    public void Configure(EntityTypeBuilder<Installation> builder)
    {
        builder.ToTable("installation", t => t.HasCheckConstraint("ck_installation_singleton", "id = 1"));
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants", t => t.HasCheckConstraint("ck_tenants_code", "code ~ '^[A-Z0-9]{3,20}$'"));
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Code).HasMaxLength(20).IsRequired();
        builder.HasIndex(t => t.Code).IsUnique();
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
    }
}

/// <summary>Resolves which tenant a request belongs to before anyone is signed in.</summary>
public sealed class TenantResolver(SupermarketBillingDbContext db, IOptions<DeploymentOptions> deployment)
{
    public DeploymentMode Mode => deployment.Value.Mode;

    /// <summary>Edge: the installation's tenant (null until setup). Cloud: always null.</summary>
    public async Task<Guid?> InstallationTenantAsync(CancellationToken cancellationToken) =>
        Mode == DeploymentMode.Cloud
            ? null
            : await db.Installation.AsNoTracking().Where(i => i.Id == Installation.SingletonId).Select(i => i.TenantId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Cloud sign-in: company code to tenant, through a SECURITY DEFINER function (tenants are protected by RLS).</summary>
    public async Task<Guid?> TenantByCodeAsync(string? code, CancellationToken cancellationToken)
    {
        string normalized;
        try
        {
            normalized = Tenant.NormalizeCode(code ?? string.Empty);
        }
        catch (Domain.Common.DomainException)
        {
            return null;
        }

        return await db.Database.SqlQuery<Guid?>($"SELECT sb_tenant_by_code({normalized}) AS \"Value\"")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tenant of a session token hash, through a SECURITY DEFINER function (sessions are protected by RLS).</summary>
    public async Task<Guid?> TenantBySessionAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<Guid?>($"SELECT sb_session_tenant({tokenHash}) AS \"Value\"")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
}

internal static class TenancyModelBuilder
{
    /// <summary>Tenant-owned entity types. Audit events may exist before a tenant is known (for example failed cloud sign-ins).</summary>
    private static readonly Type[] Required =
    [
        typeof(Business), typeof(Store), typeof(User), typeof(RoleAssignment), typeof(Session),
        typeof(PasswordResetToken), typeof(MfaRecoveryCode), typeof(ApprovalRequest),
        typeof(TaxRegistration), typeof(Domain.Catalog.Unit), typeof(Category), typeof(Brand), typeof(CustomerGroup),
        typeof(Product), typeof(ProductVariant), typeof(VariantUnit), typeof(VariantBarcode), typeof(VariantMrp), typeof(PriceRule),
        typeof(InventorySettings), typeof(NegativeStockRule), typeof(Batch), typeof(CostLayer), typeof(StockBalance), typeof(StockLedgerEntry),
        typeof(StockDocument), typeof(ReorderLevel), typeof(DocumentSequence),
        typeof(Domain.Sales.Counter), typeof(Domain.Sales.CounterDevice), typeof(Domain.Sales.SupervisorApproval), typeof(Domain.Sales.SalesInvoice),
        typeof(Domain.Sales.SalesInvoiceLine), typeof(Domain.Sales.SalesInvoicePayment), typeof(Domain.Sales.ParkedBill),
        typeof(Domain.Sales.SalesReturn), typeof(Domain.Sales.SalesReturnLine), typeof(Domain.Sales.SalesReturnRefund), typeof(Domain.Sales.CreditNoteRedemption),
        typeof(Domain.Sales.Shift), typeof(Domain.Sales.ShiftCount), typeof(Domain.Sales.CashMovement),
        typeof(Domain.Purchases.Supplier), typeof(Domain.Purchases.PurchaseSettings), typeof(Domain.Purchases.Grn), typeof(Domain.Purchases.GrnLine),
        typeof(Domain.Purchases.GrnExpense), typeof(Domain.Purchases.GrnAllocation),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        foreach (var type in Required)
        {
            var entity = modelBuilder.Entity(type);
            entity.Property<Guid>(TenancyModel.TenantIdProperty).IsRequired();
            entity.HasIndex(TenancyModel.TenantIdProperty);
            entity.HasOne(typeof(Tenant)).WithMany().HasForeignKey(TenancyModel.TenantIdProperty).OnDelete(DeleteBehavior.Restrict);
        }

        var audit = modelBuilder.Entity<AuditEvent>();
        audit.Property<Guid?>(TenancyModel.TenantIdProperty);
        audit.HasIndex(TenancyModel.TenantIdProperty, nameof(AuditEvent.Sequence));
        audit.HasOne<Tenant>().WithMany().HasForeignKey(TenancyModel.TenantIdProperty).OnDelete(DeleteBehavior.Restrict);

        // Principal keys that let children reference a parent *within the same tenant*.
        modelBuilder.Entity<Business>().HasAlternateKey(nameof(Business.Id), TenancyModel.TenantIdProperty);
        modelBuilder.Entity<User>().HasAlternateKey(nameof(User.Id), TenancyModel.TenantIdProperty);
    }
}
