using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API in memory against its own freshly created PostgreSQL database, using the least-privilege
/// runtime account. The clock is controllable and every log message is captured so tests can prove secrets are
/// never logged. By default the installation is set up with a business, a store and an owner.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string BusinessCode = "TEST";
    public const string OwnerUsername = "owner1";
    public const string OwnerPassword = "Owner-Password-2026!"; // sb-audit: test-fixture (throw-away test database)

    /// <summary>Fixed 256-bit key used only by tests.</summary>
    internal const string TestDataProtectionKey = "q83vEjRWeJq83vEjRWeJq83vEjRWeJq83vEjRWeJq8M=";

    private readonly bool _bootstrap;
    private readonly string _setupCodeFile = Path.Combine(Path.GetTempPath(), $"sb-setup-{Guid.NewGuid():N}.txt");
    private readonly string? _appConnectionStringOverride;
    private TestDatabase? _database;

    public ApiFactory()
        : this(bootstrap: true)
    {
    }

    protected ApiFactory(bool bootstrap)
    {
        _bootstrap = bootstrap;
    }

    /// <summary>For tests that need the API pointed at an unreachable database.</summary>
    internal ApiFactory(string appConnectionString)
    {
        _appConnectionStringOverride = appConnectionString;
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 4, 30, 0, TimeSpan.Zero));

    public ConcurrentQueue<string> LogMessages { get; } = new();

    public string AppConnectionString => _appConnectionStringOverride ?? _database!.AppConnectionString;

    public string DatabaseName => _database!.Name;

    public string SetupCodeFile => _setupCodeFile;

    /// <summary>The business created during setup.</summary>
    public Guid BusinessId { get; private set; }

    /// <summary>The tenant (company) created during setup.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>The store created during setup.</summary>
    public Guid MainStoreId { get; private set; }

    public async Task InitializeAsync()
    {
        if (_appConnectionStringOverride is not null)
        {
            return;
        }

        _database = await TestDatabase.CreateAsync();
        if (_bootstrap)
        {
            await RunSetupAsync();
            using var owner = await LoginAsync(OwnerUsername, OwnerPassword);
            var businesses = await owner.GetJsonAsync<List<BusinessDto>>("/api/v1/businesses");
            BusinessId = businesses.Single(b => b.Code == BusinessCode).Id;
            var stores = await owner.GetJsonAsync<List<StoreDto>>($"/api/v1/businesses/{BusinessId}/stores");
            MainStoreId = stores.Single().Id;
            await using (var admin = await TestDatabase.OpenAdminAsync(_database.Name))
            await using (var command = new Npgsql.NpgsqlCommand("SELECT tenant_id FROM installation WHERE id = 1", admin))
            {
                TenantId = (Guid)(await command.ExecuteScalarAsync())!;
            }

            // A business-wide manager who acts as the second person for maker-checker. Granted while the owner is
            // the only person who could approve, so this first grant is applied with a recorded waiver.
            var approver = await CreateSignedInUserAsync("manager", username: ApproverUsername);
            approver.Client.Dispose();
        }
    }

    public const string ApproverUsername = "approver1";
    public const string DefaultUserPassword = "Chosen-Password-002"; // sb-audit: test-fixture (throw-away test database)

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        File.Delete(_setupCodeFile);
    }

    /// <summary>
    /// A direct connection as the runtime account, inside this fixture's tenant, as the API itself would have.
    /// Without a tenant, row-level security makes every tenant table look empty.
    /// </summary>
    public async Task<Npgsql.NpgsqlConnection> OpenAppConnectionAsync(Guid? tenantId = null)
    {
        var connection = new Npgsql.NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT set_config('sb.tenant_id', @t, false)", connection);
        command.Parameters.AddWithValue("t", (tenantId ?? TenantId).ToString());
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    /// <summary>A service scope working inside this fixture's tenant, like a request from one of its users.</summary>
    public async Task<AsyncServiceScope> CreateTenantScopeAsync()
    {
        var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SupermarketBilling.Infrastructure.Tenancy.TenantContext>()
            .SetAsync(TenantId, scope.ServiceProvider.GetRequiredService<SupermarketBilling.Infrastructure.Persistence.SupermarketBillingDbContext>(), CancellationToken.None);
        return scope;
    }

    /// <summary>A browser-like client: keeps cookies and sends the CSRF header on state-changing requests.</summary>
    public TestClient CreateBrowserClient()
    {
        var cookies = new BrowserCookieHandler();
        return new TestClient(CreateDefaultClient(cookies), cookies);
    }

    public async Task<TestClient> LoginAsync(string username, string password)
    {
        var client = CreateBrowserClient();
        var response = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, password));
        await response.EnsureSuccessWithBodyAsync();
        return client;
    }

    /// <summary>
    /// Creates a user (via the owner) with a role, completes the forced password change, and signs them in.
    /// If the role is privileged and needs approval, the approver approves it first.
    /// </summary>
    public async Task<(TestClient Client, Guid UserId, string Username, string Password)> CreateSignedInUserAsync(
        string role, Guid? storeId = null, Guid? businessId = null, string? username = null)
    {
        username ??= $"u{Guid.NewGuid():N}"[..20];
        const string temporary = "Temporary-Pass-001";
        CreateUserResponseDto body;
        using (var owner = await LoginAsync(OwnerUsername, OwnerPassword))
        {
            var created = await owner.PostJsonAsync(
                $"/api/v1/businesses/{businessId ?? BusinessId}/users",
                new CreateUserRequest(username, "Test " + role, temporary, role, storeId));
            await created.EnsureSuccessWithBodyAsync();
            body = (await created.Content.ReadFromJsonAsync<CreateUserResponseDto>(TestClient.Json))!;
        }

        if (body.Role.Outcome == "pending_approval")
        {
            using var approver = await LoginAsync(ApproverUsername, DefaultUserPassword);
            var approved = await approver.PostJsonAsync($"/api/v1/approvals/{body.Role.ApprovalRequestId}/approve", new ApprovalDecisionRequest("test setup"));
            await approved.EnsureSuccessWithBodyAsync();
        }

        var client = await LoginAsync(username, temporary);
        var change = await client.PostJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest(temporary, DefaultUserPassword));
        await change.EnsureSuccessWithBodyAsync();
        return (client, body.User.Id, username, DefaultUserPassword);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.UseSetting($"ConnectionStrings:{DependencyInjection.MainConnectionStringName}", AppConnectionString);
        builder.UseSetting("ARCHIVE_WEB_ENABLED", "false");
        builder.UseSetting("Security:DataProtectionKey", TestDataProtectionKey);
        builder.UseSetting("Security:SetupCodeFile", _setupCodeFile);
        builder.UseSetting("Security:MaxBusinesses", "3");
        builder.UseSetting("RateLimiting:AuthPermitPerMinute", "100000");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
        });
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(new CapturingLoggerProvider(LogMessages));
        });
    }

    private async Task RunSetupAsync()
    {
        using var client = CreateBrowserClient();
        await client.GetAsync(new Uri("/api/v1/setup/status", UriKind.Relative)); // starts the host, which writes the code file
        var response = await client.PostJsonAsync("/api/v1/setup", NewSetupRequest(await File.ReadAllTextAsync(_setupCodeFile)));
        await response.EnsureSuccessWithBodyAsync();
    }

    public const string CompanyCode = "TESTCO";

    internal static SetupRequest NewSetupRequest(string setupCode, string? provisioningKey = null, string companyCode = CompanyCode, string businessCode = BusinessCode) => new(
        setupCode.Trim(),
        provisioningKey,
        companyCode,
        new CreateBusinessRequest(businessCode, "Test Traders Private Limited", "Test Supermarket", "33", null, "1 Main Road, Chennai"),
        new CreateStoreRequest("MAIN", "Main Store", "33", null, null),
        OwnerUsername,
        "Owner One",
        OwnerPassword);

    internal sealed record CreateUserResponseDto(UserDto User, GrantRoleResponse Role);
}

/// <summary>A factory whose database is empty: initial setup has not been run.</summary>
public sealed class EmptyApiFactory() : ApiFactory(bootstrap: false);

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

internal sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
