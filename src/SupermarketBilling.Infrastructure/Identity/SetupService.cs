using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Domain.Tenancy;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Security;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Identity;

/// <summary>
/// Holds the one-time initial setup code in a local file. Only someone with access to the server's disk can read
/// it, so the first person to open the web page cannot simply claim the installation.
/// </summary>
public sealed class SetupCodeStore(IOptions<SecurityOptions> options, IHostEnvironment environment)
{
    private readonly Lock _gate = new();

    public string FilePath => Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.SetupCodeFile));

    public void EnsureExists()
    {
        lock (_gate)
        {
            if (File.Exists(FilePath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, SecretTokens.NewHumanCode(groups: 4) + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public bool Matches(string? code)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(code) || !File.Exists(FilePath))
            {
                return false;
            }

            var expected = SecretTokens.HashHumanCode(File.ReadAllText(FilePath));
            return CryptographicOperations.FixedTimeEquals(expected, SecretTokens.HashHumanCode(code));
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
    }
}

/// <summary>
/// At startup of an in-store server that has not been set up, creates the setup code file and logs where it is
/// (never the code). Cloud installations are provisioned by the vendor instead.
/// </summary>
public sealed partial class SetupCodeInitializer(IServiceProvider services, SetupCodeStore store, ILogger<SetupCodeInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
            if ((await setup.GetStatusAsync(cancellationToken).ConfigureAwait(false)).SetupRequired)
            {
                store.EnsureExists();
                LogSetupRequired(logger, store.FilePath);
            }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            // The database may not be ready yet; readiness health checks report that separately.
            LogSetupCheckFailed(logger, ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning,
        Message = "Initial setup required. Open the Billing Web and enter the setup code stored in {SetupCodeFile}.")]
    private static partial void LogSetupRequired(ILogger logger, string setupCodeFile);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Could not check whether initial setup is needed ({Error}).")]
    private static partial void LogSetupCheckFailed(ILogger logger, string error);
}

/// <summary>
/// Creates a company (tenant) with its first business, store and owner.
/// In-store server: exactly once, authorised by the local setup code, and the installation is bound to the company.
/// Cloud: once per new customer, authorised by the vendor's provisioning key.
/// </summary>
public sealed class SetupService(
    SupermarketBillingDbContext db,
    TenantContext tenant,
    TenantResolver tenants,
    SetupCodeStore setupCode,
    PasswordHashing passwords,
    AuditRecorder audit,
    IOptions<DeploymentOptions> deployment,
    TimeProvider clock)
{
    private const long SetupLockKey = 7_311_2026_0001;

    private bool IsCloud => deployment.Value.Mode == DeploymentMode.Cloud;

    public async Task<SetupStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var mode = IsCloud ? "cloud" : "edge";
        if (IsCloud)
        {
            return new SetupStatusResponse(SetupRequired: false, mode);
        }

        return new SetupStatusResponse(await tenants.InstallationTenantAsync(cancellationToken).ConfigureAwait(false) is null, mode);
    }

    public async Task RunAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Serialises concurrent setup attempts so an in-store server can be set up exactly once.
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({SetupLockKey})", cancellationToken).ConfigureAwait(false);
        Installation? installation = null;
        if (IsCloud)
        {
            if (!ProvisioningKeyMatches(request.ProvisioningKey))
            {
                throw new AppException(ErrorKind.Forbidden, "setup.provisioning_key_invalid", "The provisioning key is not correct.");
            }
        }
        else
        {
            installation = await db.Installation.FirstAsync(i => i.Id == Installation.SingletonId, cancellationToken).ConfigureAwait(false);
            if (installation.TenantId is not null)
            {
                throw AppException.Conflict("setup.already_completed", "Initial setup has already been completed.");
            }

            if (!setupCode.Matches(request.SetupCode))
            {
                throw new AppException(ErrorKind.Forbidden, "setup.code_invalid", "The setup code is not correct. It is in the setup-code file on the server.");
            }
        }

        var username = User.NormalizeUsername(request.OwnerUsername);
        AuthService.EnsurePasswordPolicy(request.OwnerPassword, username);

        var b = request.Business;
        var company = Tenant.Create(request.CompanyCode, b.LegalName, now);
        var business = Business.Create(b.Code, b.LegalName, b.TradeName, b.StateCode, b.Gstin, b.Address, now);
        var s = request.Store;
        var store = Store.Create(business.Id, s.Code, s.Name, s.StateCode, s.Gstin, s.Address, now);

        // Everything below is created inside the new company; row-level security checks every insert against it.
        await tenant.SetAsync(company.Id, db, cancellationToken).ConfigureAwait(false);
        db.Tenants.Add(company);
        installation?.AssignTenant(company.Id);

        var owner = User.Create(username, request.OwnerDisplayName, passwords.Hash(request.OwnerPassword), now, mustChangePassword: false);
        db.Businesses.Add(business);
        db.Stores.Add(store);
        db.Users.Add(owner);
        var grant = RoleAssignment.Grant(owner.Id, Roles.Owner, business.Id, storeId: null, grantedBy: null, approvalRequestId: null, now);
        db.RoleAssignments.Add(grant);
        CatalogService.SeedDefaultUnits(db, business.Id, now);
        var taxRegistration = TaxRegistration.Initial(business.Id, TaxModeFor(b), business.Gstin, BusinessCalendar.Today(clock), owner.Id, now);
        db.TaxRegistrations.Add(taxRegistration);
        db.InventorySettings.Add(Domain.Inventory.InventorySettings.Create(business.Id, Domain.Inventory.ValuationMethods.Fifo));

        audit.Record("setup.completed", "tenant", company.Id, business.Id,
            details: new { company = company.Code, business = business.Code, store = store.Code, owner = owner.Username, mode = IsCloud ? "cloud" : "edge", taxRegistration = taxRegistration.Mode },
            actorUserId: owner.Id);
        audit.Record("role.granted", "role_assignment", grant.Id, business.Id, details: new { user = owner.Username, role = Roles.Owner, via = "initial_setup" }, actorUserId: owner.Id);

        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCloud)
        {
            setupCode.Delete();
        }
    }

    /// <summary>The mode chosen at setup; without one, a GSTIN implies regular GST and no GSTIN means not registered.</summary>
    internal static string TaxModeFor(CreateBusinessRequest business) =>
        business.TaxRegistrationMode ?? (string.IsNullOrWhiteSpace(business.Gstin) ? TaxRegistrationModes.NotGstRegistered : TaxRegistrationModes.GstRegular);

    private bool ProvisioningKeyMatches(string? supplied)
    {
        var configured = deployment.Value.ProvisioningKey;
        if (string.IsNullOrWhiteSpace(configured) || configured.Length < 32 || string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(SecretTokens.Hash(configured), SecretTokens.Hash(supplied));
    }
}