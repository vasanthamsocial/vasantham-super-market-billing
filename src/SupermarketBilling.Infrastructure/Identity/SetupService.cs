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
using SupermarketBilling.Infrastructure.Security;

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

/// <summary>At startup, creates the setup code file when no users exist yet and says where it is (never the code).</summary>
public sealed partial class SetupCodeInitializer(IServiceProvider services, SetupCodeStore store, ILogger<SetupCodeInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SupermarketBillingDbContext>();
            if (!await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
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

/// <summary>Creates the first business, store and owner. Runs exactly once per installation.</summary>
public sealed class SetupService(
    SupermarketBillingDbContext db,
    SetupCodeStore setupCode,
    PasswordHashing passwords,
    AuditRecorder audit,
    TimeProvider clock)
{
    private const long SetupLockKey = 7_311_2026_0001;

    public async Task<SetupStatusResponse> GetStatusAsync(CancellationToken cancellationToken) =>
        new(!await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false));

    public async Task RunAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Serialises concurrent setup attempts so exactly one can succeed.
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({SetupLockKey})", cancellationToken).ConfigureAwait(false);
        if (await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("setup.already_completed", "Initial setup has already been completed.");
        }

        if (!setupCode.Matches(request.SetupCode))
        {
            throw new AppException(ErrorKind.Forbidden, "setup.code_invalid", "The setup code is not correct. It is in the setup-code file on the server.");
        }

        var username = User.NormalizeUsername(request.OwnerUsername);
        AuthService.EnsurePasswordPolicy(request.OwnerPassword, username);

        Business business;
        Store store;
        try
        {
            var b = request.Business;
            business = Business.Create(b.Code, b.LegalName, b.TradeName, b.StateCode, b.Gstin, b.Address, now);
            var s = request.Store;
            store = Store.Create(business.Id, s.Code, s.Name, s.StateCode, s.Gstin, s.Address, now);
        }
        catch (DomainException ex)
        {
            throw AppException.Validation(ex.Code, ex.Message);
        }

        var owner = User.Create(username, request.OwnerDisplayName, passwords.Hash(request.OwnerPassword), now, mustChangePassword: false);
        db.Businesses.Add(business);
        db.Stores.Add(store);
        db.Users.Add(owner);
        var grant = RoleAssignment.Grant(owner.Id, Roles.Owner, business.Id, storeId: null, grantedBy: null, approvalRequestId: null, now);
        db.RoleAssignments.Add(grant);

        audit.Record("setup.completed", "business", business.Id, business.Id, details: new { business = business.Code, store = store.Code, owner = owner.Username }, actorUserId: owner.Id);
        audit.Record("role.granted", "role_assignment", grant.Id, business.Id, details: new { user = owner.Username, role = Roles.Owner, via = "initial_setup" }, actorUserId: owner.Id);

        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        setupCode.Delete();
    }
}
