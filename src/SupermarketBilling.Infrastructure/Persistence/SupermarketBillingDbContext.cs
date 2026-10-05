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

    public DbSet<Domain.Sales.OfflineBillRecord> OfflineBills => Set<Domain.Sales.OfflineBillRecord>();

    public DbSet<Domain.Sales.SupervisorApproval> SupervisorApprovals => Set<Domain.Sales.SupervisorApproval>();

    public DbSet<Domain.Sales.SalesInvoice> SalesInvoices => Set<Domain.Sales.SalesInvoice>();

    public DbSet<Domain.Sales.SalesInvoiceLine> SalesInvoiceLines => Set<Domain.Sales.SalesInvoiceLine>();

    public DbSet<Domain.Sales.SalesInvoicePayment> SalesInvoicePayments => Set<Domain.Sales.SalesInvoicePayment>();

    public DbSet<Domain.Sales.ParkedBill> ParkedBills => Set<Domain.Sales.ParkedBill>();

    public DbSet<Domain.Sales.SalesReturn> SalesReturns => Set<Domain.Sales.SalesReturn>();

    public DbSet<Domain.Sales.SalesReturnLine> SalesReturnLines => Set<Domain.Sales.SalesReturnLine>();

    public DbSet<Domain.Sales.SalesReturnRefund> SalesReturnRefunds => Set<Domain.Sales.SalesReturnRefund>();

    public DbSet<Domain.Sales.CreditNoteRedemption> CreditNoteRedemptions => Set<Domain.Sales.CreditNoteRedemption>();

    public DbSet<Domain.Sales.Shift> Shifts => Set<Domain.Sales.Shift>();

    public DbSet<Domain.Sales.ShiftCount> ShiftCounts => Set<Domain.Sales.ShiftCount>();

    public DbSet<Domain.Sales.CashMovement> CashMovements => Set<Domain.Sales.CashMovement>();

    public DbSet<Domain.Purchases.Supplier> Suppliers => Set<Domain.Purchases.Supplier>();

    public DbSet<Domain.Purchases.PurchaseSettings> PurchaseSettings => Set<Domain.Purchases.PurchaseSettings>();

    public DbSet<Domain.Purchases.Grn> Grns => Set<Domain.Purchases.Grn>();

    public DbSet<Domain.Purchases.GrnLine> GrnLines => Set<Domain.Purchases.GrnLine>();

    public DbSet<Domain.Purchases.GrnExpense> GrnExpenses => Set<Domain.Purchases.GrnExpense>();

    public DbSet<Domain.Purchases.GrnAllocation> GrnAllocations => Set<Domain.Purchases.GrnAllocation>();

    public DbSet<Domain.Purchases.PurchaseOrder> PurchaseOrders => Set<Domain.Purchases.PurchaseOrder>();

    public DbSet<Domain.Purchases.PurchaseOrderLine> PurchaseOrderLines => Set<Domain.Purchases.PurchaseOrderLine>();

    public DbSet<Domain.Purchases.Attachment> Attachments => Set<Domain.Purchases.Attachment>();

    public DbSet<Domain.Accounts.Debtor> Debtors => Set<Domain.Accounts.Debtor>();

    public DbSet<Domain.Accounts.SupplierLedgerEntry> SupplierLedger => Set<Domain.Accounts.SupplierLedgerEntry>();

    public DbSet<Domain.Accounts.DebtorLedgerEntry> DebtorLedger => Set<Domain.Accounts.DebtorLedgerEntry>();

    public DbSet<Domain.Accounts.SupplierSettlement> SupplierSettlements => Set<Domain.Accounts.SupplierSettlement>();

    public DbSet<Domain.Accounts.DebtorSettlement> DebtorSettlements => Set<Domain.Accounts.DebtorSettlement>();

    public DbSet<Domain.Accounts.SupplierPayment> SupplierPayments => Set<Domain.Accounts.SupplierPayment>();

    public DbSet<Domain.Purchases.PurchaseReturn> PurchaseReturns => Set<Domain.Purchases.PurchaseReturn>();

    public DbSet<Domain.Purchases.PurchaseReturnLine> PurchaseReturnLines => Set<Domain.Purchases.PurchaseReturnLine>();

    public DbSet<Domain.Accounts.DebtorReceipt> DebtorReceipts => Set<Domain.Accounts.DebtorReceipt>();

    public DbSet<Domain.Accounts.Route> Routes => Set<Domain.Accounts.Route>();

    public DbSet<Domain.Accounts.CollectionPlan> CollectionPlans => Set<Domain.Accounts.CollectionPlan>();

    public DbSet<Domain.Accounts.CollectionVisit> CollectionVisits => Set<Domain.Accounts.CollectionVisit>();

    public DbSet<Domain.Accounts.PaymentPromise> PaymentPromises => Set<Domain.Accounts.PaymentPromise>();

    public DbSet<Domain.Accounts.CollectorAbsence> CollectorAbsences => Set<Domain.Accounts.CollectorAbsence>();

    public DbSet<Domain.Accounts.CollectorSession> CollectorSessions => Set<Domain.Accounts.CollectorSession>();

    public DbSet<Domain.Accounts.CollectorSessionCount> CollectorSessionCounts => Set<Domain.Accounts.CollectorSessionCount>();

    public DbSet<Domain.Accounts.Cheque> Cheques => Set<Domain.Accounts.Cheque>();

    public DbSet<Domain.Accounts.ChequeEvent> ChequeEvents => Set<Domain.Accounts.ChequeEvent>();

    public DbSet<Domain.Accounts.ReceiptReversal> ReceiptReversals => Set<Domain.Accounts.ReceiptReversal>();

    public DbSet<Domain.Accounts.VisitOutcome> VisitOutcomes => Set<Domain.Accounts.VisitOutcome>();

    public DbSet<Domain.Messaging.MessageTemplate> MessageTemplates => Set<Domain.Messaging.MessageTemplate>();

    public DbSet<Domain.Messaging.OutboundMessage> OutboundMessages => Set<Domain.Messaging.OutboundMessage>();

    public DbSet<Domain.Messaging.MessageEvent> MessageEvents => Set<Domain.Messaging.MessageEvent>();

    public DbSet<Domain.Messaging.MessagingSettings> MessagingSettings => Set<Domain.Messaging.MessagingSettings>();

    public DbSet<Configurations.ProviderMessageRef> ProviderMessageRefs => Set<Configurations.ProviderMessageRef>();

    public DbSet<Domain.Dispatch.Transporter> Transporters => Set<Domain.Dispatch.Transporter>();

    public DbSet<Domain.Dispatch.TransporterBranch> TransporterBranches => Set<Domain.Dispatch.TransporterBranch>();

    public DbSet<Domain.Dispatch.TransporterRoute> TransporterRoutes => Set<Domain.Dispatch.TransporterRoute>();

    public DbSet<Domain.Dispatch.InvoiceFulfilment> InvoiceFulfilments => Set<Domain.Dispatch.InvoiceFulfilment>();

    public DbSet<Domain.Dispatch.Consignment> Consignments => Set<Domain.Dispatch.Consignment>();

    public DbSet<Domain.Dispatch.ConsignmentInvoice> ConsignmentInvoices => Set<Domain.Dispatch.ConsignmentInvoice>();

    public DbSet<Domain.Dispatch.DeliveryPreference> DeliveryPreferences => Set<Domain.Dispatch.DeliveryPreference>();

    public DbSet<Domain.Dispatch.PackingChallan> PackingChallans => Set<Domain.Dispatch.PackingChallan>();

    public DbSet<Domain.Dispatch.PackingChallanLine> PackingChallanLines => Set<Domain.Dispatch.PackingChallanLine>();

    public DbSet<Domain.Dispatch.PackingEvent> PackingEvents => Set<Domain.Dispatch.PackingEvent>();

    public DbSet<Domain.Dispatch.ConsignmentLine> ConsignmentLines => Set<Domain.Dispatch.ConsignmentLine>();

    public DbSet<Domain.Accounts.CollectionDevice> CollectionDevices => Set<Domain.Accounts.CollectionDevice>();

    public DbSet<Domain.Accounts.OfflineSubmission> OfflineSubmissions => Set<Domain.Accounts.OfflineSubmission>();

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
