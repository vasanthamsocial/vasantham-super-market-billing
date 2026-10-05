using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupermarketBilling.Application.Auditing;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Name of the least-privilege runtime connection string (DML only, no DDL).</summary>
    public const string MainConnectionStringName = "Main";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(MainConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{MainConnectionStringName}' is not configured. " +
                "Set ConnectionStrings__Main (see .env.example and scripts/setup-dev.ps1).");
        }

        services.AddOptions<DeploymentOptions>().Bind(configuration.GetSection(DeploymentOptions.SectionName));
        services.AddScoped<TenantContext>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddScoped<TenantStampingInterceptor>();
        services.AddScoped<TenantResolver>();
        services.AddDbContext<SupermarketBillingDbContext>((provider, options) =>
        {
            ConfigureDbContext(options, connectionString);
            options.AddInterceptors(
                provider.GetRequiredService<TenantConnectionInterceptor>(),
                provider.GetRequiredService<TenantStampingInterceptor>());
        });
        services.AddScoped<IAuditTrail, EfAuditTrail>();
        services.AddScoped<AuditRecorder>();
        services.AddScoped<AuditQueryService>();

        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.SectionName));
        services.AddSingleton<PasswordHashing>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<SetupCodeStore>();
        services.AddHostedService<SetupCodeInitializer>();

        services.AddScoped<IAccessControl, AccessControl>();
        services.AddScoped<SessionService>();
        services.AddScoped<AuthService>();
        services.AddScoped<SetupService>();
        services.AddScoped<OrganisationService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<IApprovalHandler, RoleGrantApprovalHandler>();
        services.AddScoped<IApprovalHandler, PriceApprovalHandler>();
        services.AddScoped<IApprovalHandler, TaxRegistrationApprovalHandler>();
        services.AddScoped<IApprovalHandler, Inventory.NegativeStockApprovalHandler>();
        services.AddScoped<CatalogService>();
        services.AddScoped<PricingService>();
        services.AddScoped<TaxRegistrationService>();
        services.AddScoped<Inventory.DocumentNumbers>();
        services.AddScoped<Inventory.StockEngine>();
        services.AddScoped<Inventory.StockPostingService>();
        services.AddScoped<Inventory.InventoryService>();
        services.AddScoped<Sales.CounterService>();
        services.AddScoped<Sales.BillingService>();
        services.AddScoped<Sales.ParkedBillService>();
        services.AddScoped<Sales.ReturnService>();
        services.AddScoped<Sales.ShiftService>();
        services.AddScoped<Purchases.SupplierService>();
        services.AddScoped<Purchases.GrnPoster>();
        services.AddScoped<Purchases.PurchaseOrderService>();
        services.AddScoped<Purchases.AttachmentService>();
        services.AddScoped<Purchases.PurchaseReturnService>();
        services.AddScoped<Accounts.PartyLedgerService>();
        services.AddScoped<Accounts.PartyAccountService>();
        services.AddScoped<Accounts.DebtorService>();
        services.AddScoped<Accounts.SupplierPaymentService>();
        services.AddScoped<Accounts.DebtorReceiptService>();
        services.AddScoped<Accounts.CollectionService>();
        services.AddScoped<Accounts.FieldCollectionService>();
        services.AddScoped<Accounts.OfflineCollectionService>();

        services.AddOptions<Messaging.MessagingOptions>().Bind(configuration.GetSection(Messaging.MessagingOptions.SectionName));
        services.AddSingleton<Messaging.SimulatedMessaging>();
        services.AddScoped<Messaging.SimulatedWhatsAppProvider>();
        services.AddScoped<Messaging.SimulatedSmsProvider>();
        services.AddHttpClient<Messaging.MetaWhatsAppProvider>(Messaging.MetaWhatsAppProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<Messaging.MessageProviders>();
        services.AddScoped<Messaging.MessageOutbox>();
        services.AddScoped<Messaging.MessageDispatcher>();
        services.AddScoped<Messaging.WhatsAppWebhook>();
        services.AddScoped<Messaging.MessagingService>();
        services.AddScoped<Dispatch.DispatchService>();
        services.AddScoped<Dispatch.PackingService>();
        services.AddScoped<Reporting.ReportService>();
        services.AddOptions<Reporting.BackupOptions>().Bind(configuration.GetSection(Reporting.BackupOptions.SectionName));
        services.AddScoped<Reporting.BackupStatusReader>();
        services.AddScoped<Reporting.DashboardService>();
        services.AddHostedService<Messaging.MessagingWorker>();
        services.AddScoped<IApprovalHandler, Accounts.ReceiptReversalHandler>();
        services.AddScoped<IApprovalHandler, Accounts.LedgerAdjustmentHandler>();
        services.AddScoped<Purchases.GrnService>();
        services.AddScoped<IApprovalHandler, Purchases.GrnApprovalHandler>();
        return services;
    }

    public static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
                npgsql.MigrationsAssembly(typeof(SupermarketBillingDbContext).Assembly.GetName().Name);
            })
            .UseSnakeCaseNamingConvention();
    }
}
