using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Auditing;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Persistence;

public sealed class SupermarketBillingDbContext(DbContextOptions<SupermarketBillingDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Business> Businesses => Set<Business>();

    public DbSet<Store> Stores => Set<Store>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();

    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TaxRegistration> TaxRegistrations => Set<TaxRegistration>();

    public DbSet<Domain.Catalog.Unit> Units => Set<Domain.Catalog.Unit>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<CustomerGroup> CustomerGroups => Set<CustomerGroup>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<VariantUnit> VariantUnits => Set<VariantUnit>();

    public DbSet<VariantBarcode> VariantBarcodes => Set<VariantBarcode>();

    public DbSet<VariantMrp> VariantMrps => Set<VariantMrp>();

    public DbSet<PriceRule> PriceRules => Set<PriceRule>();

    public DbSet<InventorySettings> InventorySettings => Set<InventorySettings>();

    public DbSet<NegativeStockRule> NegativeStockRules => Set<NegativeStockRule>();

    public DbSet<Batch> Batches => Set<Batch>();

    public DbSet<CostLayer> CostLayers => Set<CostLayer>();

    public DbSet<StockBalance> StockBalances => Set<StockBalance>();

    public DbSet<StockLedgerEntry> StockLedger => Set<StockLedgerEntry>();

    public DbSet<StockDocument> StockDocuments => Set<StockDocument>();

    public DbSet<ReorderLevel> ReorderLevels => Set<ReorderLevel>();

    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    public DbSet<Domain.Sales.Counter> Counters => Set<Domain.Sales.Counter>();

    public DbSet<Domain.Sales.CounterDevice> CounterDevices => Set<Domain.Sales.CounterDevice>();

    public DbSet<Domain.Sales.SupervisorApproval> SupervisorApprovals => Set<Domain.Sales.SupervisorApproval>();

    public DbSet<Domain.Sales.SalesInvoice> SalesInvoices => Set<Domain.Sales.SalesInvoice>();

    public DbSet<Domain.Sales.SalesInvoiceLine> SalesInvoiceLines => Set<Domain.Sales.SalesInvoiceLine>();

    public DbSet<Domain.Sales.SalesInvoicePayment> SalesInvoicePayments => Set<Domain.Sales.SalesInvoicePayment>();

    public DbSet<Domain.Sales.ParkedBill> ParkedBills => Set<Domain.Sales.ParkedBill>();

    public DbSet<Installation> Installation => Set<Installation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Tenant columns first, so configurations can build tenant-consistent composite keys on them.
        TenancyModelBuilder.Configure(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SupermarketBillingDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Money, quantities and rates are exact decimals. No floating point anywhere in the financial model.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }
}
