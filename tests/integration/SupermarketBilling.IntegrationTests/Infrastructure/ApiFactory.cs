using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SupermarketBilling.Infrastructure;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API in memory against the PostgreSQL test database, using the least-privilege runtime account.
/// Migrations are applied once with the migrator account, exactly as in a real deployment.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _appConnectionString;

    public ApiFactory()
        : this(TestSettings.AppConnectionString)
    {
    }

    internal ApiFactory(string appConnectionString)
    {
        _appConnectionString = appConnectionString;
    }

    public async Task InitializeAsync()
    {
        await EnsureDatabaseReachableAsync();

        var options = new DbContextOptionsBuilder<SupermarketBillingDbContext>();
        DependencyInjection.ConfigureDbContext(options, TestSettings.MigratorConnectionString);
        await using var db = new SupermarketBillingDbContext(options.Options);
        await db.Database.MigrateAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>Fails fast with actionable guidance instead of a generic EF Core connection error.</summary>
    private static async Task EnsureDatabaseReachableAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(TestSettings.MigratorConnectionString) { Timeout = 5 };
        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Cannot connect to the test database '{builder.Database}' at {builder.Host}:{builder.Port} ({ex.Message}). " +
                "Start Docker Desktop, then run: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\db-up.ps1",
                ex);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting($"ConnectionStrings:{DependencyInjection.MainConnectionStringName}", _appConnectionString);
        builder.UseSetting("ARCHIVE_WEB_ENABLED", "false");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
