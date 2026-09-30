using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SupermarketBilling.Infrastructure;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.IntegrationTests.Infrastructure;

/// <summary>
/// A throw-away database for one test fixture, created with the same ownership and grants as the init script and
/// migrated with the migrator account. Its name always contains "_test_", and it is dropped when the fixture ends.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string name)
    {
        Name = name;
        AppConnectionString = WithDatabase(TestSettings.AppConnectionString, name);
        MigratorConnectionString = WithDatabase(TestSettings.MigratorConnectionString, name);
    }

    public string Name { get; }

    public string AppConnectionString { get; }

    public string MigratorConnectionString { get; }

    /// <param name="targetMigration">Migrate only up to this migration (for testing a later migration's data changes).</param>
    public static async Task<TestDatabase> CreateAsync(string? targetMigration = null)
    {
        var database = new TestDatabase($"supermarketbilling_test_it_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
        var migrator = TestSettings.Get("SB_DB_MIGRATOR_USER");
        var app = TestSettings.Get("SB_DB_APP_USER");

        await using (var admin = await OpenAdminAsync("postgres"))
        {
            await ExecuteAsync(admin, $"CREATE DATABASE \"{database.Name}\" OWNER \"{migrator}\" ENCODING 'UTF8' TEMPLATE template0");
        }

        await using (var admin = await OpenAdminAsync(database.Name))
        {
            await ExecuteAsync(admin, $"""
                REVOKE ALL ON DATABASE "{database.Name}" FROM PUBLIC;
                GRANT CONNECT, TEMPORARY ON DATABASE "{database.Name}" TO "{migrator}";
                GRANT CONNECT ON DATABASE "{database.Name}" TO "{app}";
                REVOKE ALL ON SCHEMA public FROM PUBLIC;
                ALTER SCHEMA public OWNER TO "{migrator}";
                GRANT USAGE ON SCHEMA public TO "{app}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{migrator}" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "{app}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{migrator}" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO "{app}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{migrator}" IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO "{app}";
                """);
        }

        var options = new DbContextOptionsBuilder<SupermarketBillingDbContext>();
        DependencyInjection.ConfigureDbContext(options, database.MigratorConnectionString);
        await using var db = new SupermarketBillingDbContext(options.Options);
        await db.Database.MigrateAsync(targetMigration);
        return database;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = await OpenAdminAsync("postgres");
        await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");
    }

    public static async Task<NpgsqlConnection> OpenAdminAsync(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(TestSettings.MigratorConnectionString)
        {
            Username = TestSettings.Get("SB_DB_SUPERUSER"),
            Password = TestSettings.Get("SB_DB_SUPERUSER_PASSWORD"),
            Database = database,
            Pooling = false,
            Timeout = 5,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                $"Cannot connect to PostgreSQL at {builder.Host}:{builder.Port} ({ex.Message}). " +
                "Start Docker Desktop, then run: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\db-up.ps1",
                ex);
        }

        return connection;
    }

    public async Task MigrateToLatestAsync()
    {
        var options = new DbContextOptionsBuilder<SupermarketBillingDbContext>();
        DependencyInjection.ConfigureDbContext(options, MigratorConnectionString);
        await using var db = new SupermarketBillingDbContext(options.Options);
        await db.Database.MigrateAsync();
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
