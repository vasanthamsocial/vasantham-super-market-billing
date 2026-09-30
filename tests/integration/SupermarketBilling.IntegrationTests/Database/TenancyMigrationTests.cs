using Npgsql;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Database;

/// <summary>An installation set up before multi-tenancy must keep all its data, now owned by one tenant.</summary>
public sealed class TenancyMigrationTests
{
    [Fact]
    public async Task Existing_single_tenant_data_is_moved_into_one_tenant_and_bound_to_the_installation()
    {
        await using var database = await TestDatabase.CreateAsync(targetMigration: "IdentityAndOrganisation");

        await using (var admin = await TestDatabase.OpenAdminAsync(database.Name))
        {
            await ExecuteAsync(admin, """
                INSERT INTO businesses (id, code, legal_name, trade_name, gstin, state_code, address, is_active, require_mfa_for_privileged_users, created_at_utc)
                VALUES ('01920000-0000-7000-8000-000000000001', 'AB', 'Old Traders', 'Old Traders', NULL, '33', NULL, true, false, now());
                INSERT INTO stores (id, business_id, code, name, state_code, gstin, address, time_zone, is_active, created_at_utc)
                VALUES ('01920000-0000-7000-8000-000000000002', '01920000-0000-7000-8000-000000000001', 'MAIN', 'Main', '33', NULL, NULL, 'Asia/Kolkata', true, now());
                INSERT INTO users (id, username, display_name, is_active, password_hash, password_changed_at_utc, must_change_password, failed_login_count, mfa_enabled, created_at_utc)
                VALUES ('01920000-0000-7000-8000-000000000003', 'oldowner', 'Old Owner', true, 'x', now(), false, 0, false, now());
                INSERT INTO role_assignments (id, user_id, role_code, business_id, store_id, granted_at_utc)
                VALUES ('01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-000000000003', 'owner', '01920000-0000-7000-8000-000000000001', NULL, now());
                INSERT INTO audit_events (id, occurred_at_utc, event_type, payload_json)
                VALUES ('01920000-0000-7000-8000-000000000005', now(), 'setup.completed', '{}');
                """);
        }

        await database.MigrateToLatestAsync();

        await using var check = await TestDatabase.OpenAdminAsync(database.Name);
        Assert.Equal(1L, await ScalarAsync<long>(check, "SELECT count(*) FROM tenants"));
        Assert.Equal("ABX", await ScalarAsync<string>(check, "SELECT code FROM tenants"));
        var tenantId = await ScalarAsync<Guid>(check, "SELECT id FROM tenants");
        foreach (var table in new[] { "businesses", "stores", "users", "role_assignments", "audit_events" })
        {
            Assert.Equal(0L, await ScalarAsync<long>(check, $"SELECT count(*) FROM {table} WHERE tenant_id IS DISTINCT FROM '{tenantId}'"));
        }

        Assert.Equal(tenantId, await ScalarAsync<Guid>(check, "SELECT tenant_id FROM installation WHERE id = 1"));

        // The audit trail is append-only again after the back-fill.
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(check, "UPDATE audit_events SET event_type = 'x'"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
    }

    [Fact]
    public async Task A_fresh_database_gets_an_unbound_installation_and_no_tenants()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var check = await TestDatabase.OpenAdminAsync(database.Name);

        Assert.Equal(0L, await ScalarAsync<long>(check, "SELECT count(*) FROM tenants"));
        Assert.Equal(1L, await ScalarAsync<long>(check, "SELECT count(*) FROM installation WHERE tenant_id IS NULL"));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
