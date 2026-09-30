using System.Net;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Tenancy;

/// <summary>The cloud service: many companies in one database, isolated by row-level security.</summary>
public sealed class CloudApiFactory() : ApiFactory(bootstrap: false)
{
    public const string ProvisioningKey = "test-provisioning-key-0123456789abcdef"; // sb-audit: test-fixture (throw-away test database)

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Deployment:Mode", "Cloud");
        builder.UseSetting("Deployment:ProvisioningKey", ProvisioningKey);
    }
}

public sealed class CloudTenancyTests(CloudApiFactory factory) : IClassFixture<CloudApiFactory>, IAsyncLifetime
{
    private static readonly SemaphoreSlim Provisioned = new(1, 1);
    private static bool _done;

    public async Task InitializeAsync()
    {
        await Provisioned.WaitAsync();
        try
        {
            if (!_done)
            {
                using var client = factory.CreateBrowserClient();
                foreach (var company in new[] { "ALPHA", "BRAVO" })
                {
                    // Both companies deliberately use the same business code and the same owner username.
                    var response = await client.PostJsonAsync("/api/v1/setup", ApiFactory.NewSetupRequest(string.Empty, CloudApiFactory.ProvisioningKey, company));
                    await response.EnsureSuccessWithBodyAsync();
                }

                _done = true;
            }
        }
        finally
        {
            Provisioned.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Provisioning_needs_the_vendor_key_and_company_codes_are_unique()
    {
        using var client = factory.CreateBrowserClient();

        var status = await client.GetJsonAsync<SetupStatusResponse>("/api/v1/setup/status");
        Assert.Equal("cloud", status.DeploymentMode);
        Assert.False(status.SetupRequired);

        var wrongKey = await client.PostJsonAsync("/api/v1/setup", ApiFactory.NewSetupRequest(string.Empty, "not-the-key-0123456789abcdef0123456789", "CHARLIE"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongKey.StatusCode);

        var duplicate = await client.PostJsonAsync("/api/v1/setup", ApiFactory.NewSetupRequest(string.Empty, CloudApiFactory.ProvisioningKey, "ALPHA"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Same_username_in_two_companies_signs_in_to_its_own_company_only()
    {
        using var alpha = await LoginAsync("ALPHA");
        using var bravo = await LoginAsync("BRAVO");

        var alphaBusiness = Assert.Single(await alpha.GetJsonAsync<List<BusinessDto>>("/api/v1/businesses"));
        var bravoBusiness = Assert.Single(await bravo.GetJsonAsync<List<BusinessDto>>("/api/v1/businesses"));
        Assert.NotEqual(alphaBusiness.Id, bravoBusiness.Id);
        Assert.Equal(alphaBusiness.Code, bravoBusiness.Code);

        // Guessing or knowing another company's ids does not help: they are simply not there.
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"/api/v1/businesses/{bravoBusiness.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"/api/v1/businesses/{bravoBusiness.Id}/users")).StatusCode);
        var intrusion = await alpha.PostJsonAsync($"/api/v1/businesses/{bravoBusiness.Id}/stores", new CreateStoreRequest("X9", "Intruder", "33", null, null));
        Assert.Equal(HttpStatusCode.NotFound, intrusion.StatusCode);
    }

    [Fact]
    public async Task Sign_in_needs_a_valid_company_code()
    {
        using var client = factory.CreateBrowserClient();

        foreach (var code in new string?[] { null, "", "NOSUCHCO", "alpha-bad" })
        {
            var response = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword, code));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var lowerCase = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword, " alpha "));
        Assert.Equal(HttpStatusCode.OK, lowerCase.StatusCode);
    }

    [Fact]
    public async Task Database_blocks_cross_tenant_access_even_for_hand_written_sql()
    {
        var alphaId = await TenantIdAsync("ALPHA");
        var bravoId = await TenantIdAsync("BRAVO");
        var bravoBusiness = await ScalarAsAdminAsync<Guid>($"SELECT id FROM businesses WHERE tenant_id = '{bravoId}'");

        // No tenant set: every tenant table looks empty.
        await using (var none = new NpgsqlConnection(factory.AppConnectionString))
        {
            await none.OpenAsync();
            Assert.Equal(0L, await ScalarAsync<long>(none, "SELECT count(*) FROM businesses"));
            Assert.Equal(0L, await ScalarAsync<long>(none, "SELECT count(*) FROM users"));
            Assert.Equal(0L, await ScalarAsync<long>(none, "SELECT count(*) FROM tenants"));
        }

        await using var alpha = await factory.OpenAppConnectionAsync(alphaId);
        Assert.Equal(1L, await ScalarAsync<long>(alpha, "SELECT count(*) FROM businesses"));
        Assert.Equal(1L, await ScalarAsync<long>(alpha, "SELECT count(*) FROM tenants"));
        Assert.Equal(0L, await ScalarAsync<long>(alpha, $"SELECT count(*) FROM businesses WHERE id = '{bravoBusiness}'"));

        // Writing a row labelled as the other tenant is refused by the row-level security check.
        var foreignRow = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(alpha,
            $"INSERT INTO stores (id, business_id, tenant_id, code, name, state_code, time_zone, is_active, created_at_utc) " +
            $"VALUES (gen_random_uuid(), '{bravoBusiness}', '{bravoId}', 'X1', 'x', '33', 'Asia/Kolkata', true, now())"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, foreignRow.SqlState);

        // Labelling it as one's own tenant but pointing at the other tenant's business breaks the composite key.
        var crossReference = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(alpha,
            $"INSERT INTO stores (id, business_id, tenant_id, code, name, state_code, time_zone, is_active, created_at_utc) " +
            $"VALUES (gen_random_uuid(), '{bravoBusiness}', '{alphaId}', 'X2', 'x', '33', 'Asia/Kolkata', true, now())"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossReference.SqlState);

        // Updates and deletes cannot reach the other tenant's rows either.
        Assert.Equal(0, await ExecuteAsync(alpha, $"UPDATE businesses SET legal_name = 'hijacked' WHERE id = '{bravoBusiness}'"));
        Assert.Equal("Test Traders Private Limited", await ScalarAsAdminAsync<string>($"SELECT legal_name FROM businesses WHERE id = '{bravoBusiness}'"));
    }

    private async Task<TestClient> LoginAsync(string companyCode)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword, companyCode));
        await response.EnsureSuccessWithBodyAsync();
        return client;
    }

    private Task<Guid> TenantIdAsync(string code) => ScalarAsAdminAsync<Guid>($"SELECT id FROM tenants WHERE code = '{code}'");

    private async Task<T> ScalarAsAdminAsync<T>(string sql)
    {
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        return await ScalarAsync<T>(admin, sql);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }
}
