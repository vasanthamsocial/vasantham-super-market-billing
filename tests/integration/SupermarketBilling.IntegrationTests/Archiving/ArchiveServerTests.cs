using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Archiving;
using SupermarketBilling.IntegrationTests.Accounts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Archiving;

/// <summary>
/// A store server and an archive server side by side (each its own installation and database): the store closes and
/// packages months; the archive trusts it, imports them, verifies them and has them approved.
/// </summary>
public sealed class ArchivePair : IAsyncLifetime
{
    public ApiFactory Store { get; } = new();

    public ArchiveApiFactory Archive { get; } = new();

    public const string OwnerUsername = "archive.owner";
    public const string OwnerPassword = "Archive-Owner-Pass-1"; // sb-audit: test-fixture (throw-away test database)

    /// <summary>The store's packages of September (an opening balance) and October (a bill), as files.</summary>
    public byte[] September { get; private set; } = [];

    public byte[] October { get; private set; } = [];

    public Guid InvoiceId { get; private set; }

    /// <summary>The archive installation's tenant (row-level security shows archive rows only within it).</summary>
    public Guid ArchiveTenantId { get; private set; }

    public async Task InitializeAsync()
    {
        await ((IAsyncLifetime)Store).InitializeAsync();
        await ((IAsyncLifetime)Archive).InitializeAsync();

        // The archive's first setup: its owner administrator.
        using (var anonymous = Archive.CreateBrowserClient())
        {
            await anonymous.GetAsync("/api/v1/setup/status"); // starts the host, which writes the setup code file
            var setup = await anonymous.PostJsonAsync("/api/v1/archive/setup", new ArchiveSetupRequest(
                (await File.ReadAllTextAsync(Archive.SetupCodeFile)).Trim(), "ARCHIVE", "Test Traders Archive", OwnerUsername, "Archive Owner", OwnerPassword));
            await setup.EnsureSuccessWithBodyAsync();
        }

        await using (var admin = await TestDatabase.OpenAdminAsync(Archive.DatabaseName))
        await using (var command = new NpgsqlCommand("SELECT tenant_id FROM installation WHERE id = 1", admin))
        {
            ArchiveTenantId = (Guid)(await command.ExecuteScalarAsync())!;
        }

        // The store: September has an opening balance, October a bill in a shift; on 2 December both are locked.
        var business = $"/api/v1/businesses/{Store.BusinessId}";
        using (var owner = await Store.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword))
        {
            await Ledgers.DebtorAsync(owner, Store.BusinessId, opening: 500m);
            var (_, pack) = await Pos.StockedProductAsync(owner, Store.BusinessId, Store.MainStoreId, price: 40m);
            var session = await Pos.CounterBrowserAsync(Store, Store.BusinessId, Store.MainStoreId);
            using (session.Browser)
            {
                InvoiceId = (await Pos.IssueAsync(session.Browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)), 80m, new PaymentRequest("UPI", 80m, "UTR-9")))).Id;
            }
        }

        Store.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 2, 5, 0, 0, TimeSpan.Zero));
        using var manager = await Store.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var open = (await manager.GetJsonAsync<List<ShiftSummaryDto>>($"{business}/shifts?storeId={Store.MainStoreId}")).Single(s => s.Status == "OPEN");
        await (await manager.PostJsonAsync($"{business}/shifts/{open.Id}/close", new CloseShiftRequest([], "Month end"))).EnsureSuccessWithBodyAsync();
        foreach (var month in new[] { "2026-09", "2026-10" })
        {
            await (await manager.PostJsonAsync($"{business}/months/{month}/lock", new LockMonthRequest(null))).EnsureSuccessWithBodyAsync();
        }

        // The keys: the store encrypts for the archive's key; the archive trusts the store's signing key.
        using var archiveOwner = await Archive.LoginAsync(OwnerUsername, OwnerPassword);
        var archiveKeys = await archiveOwner.GetJsonAsync<ArchiveServerKeysDto>("/api/v1/archive/keys");
        await (await manager.PutJsonAsync($"{business}/archive/recipient", new SetArchiveRecipientRequest(archiveKeys.PublicKeyPem, null))).EnsureSuccessWithBodyAsync();
        var storeKeys = await manager.GetJsonAsync<ArchiveKeysDto>($"{business}/archive/keys");
        await (await archiveOwner.PostJsonAsync("/api/v1/archive/sources", new RegisterArchiveSourceRequest("Main store server", storeKeys.SignerPublicKeyPem)))
            .EnsureSuccessWithBodyAsync();

        September = await PackageAsync(manager, $"{business}/months/2026-09/package");
        October = await PackageAsync(manager, $"{business}/months/2026-10/package");
    }

    private static async Task<byte[]> PackageAsync(TestClient client, string path)
    {
        var response = await client.PostJsonAsync(path, new { });
        await response.EnsureSuccessWithBodyAsync();
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)Archive).DisposeAsync();
        await ((IAsyncLifetime)Store).DisposeAsync();
    }
}

public sealed class ArchiveServerTests(ArchivePair pair) : IClassFixture<ArchivePair>
{
    private ArchiveApiFactory Archive => pair.Archive;

    private static async Task<HttpResponseMessage> UploadAsync(TestClient client, byte[] package, string name = "month.sbarc")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        return await client.Http.PostAsync(new Uri("/api/v1/archive/imports", UriKind.Relative), form);
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    private async Task<(TestClient Client, ArchiveUserDto User)> ArchiveUserAsync(TestClient owner, ArchiveGrantRequest grant)
    {
        var username = $"a{Guid.NewGuid():N}"[..16];
        var user = await Ok<ArchiveUserDto>(owner.PostJsonAsync("/api/v1/archive/users", new CreateArchiveUserRequest(username, "Archive " + grant.RoleCode,
            "Temporary-Pass-001", grant)));
        var client = await Archive.LoginAsync(username, "Temporary-Pass-001");
        await (await client.PostJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest("Temporary-Pass-001", "Archive-User-Pass-9"))).EnsureSuccessWithBodyAsync();
        return (client, user);
    }

    [Fact]
    public async Task Months_from_a_trusted_store_server_are_imported_once_verified_and_approved_by_the_accountant_then_the_owner()
    {
        using var owner = await Archive.LoginAsync(ArchivePair.OwnerUsername, ArchivePair.OwnerPassword);
        var me = await owner.GetJsonAsync<ArchiveMeDto>("/api/v1/archive/me");
        Assert.Equal("archive_owner", Assert.Single(me.Grants).RoleCode);

        // Imported, with every month dataset stored and verified again.
        // (Another test may have imported it first: then it is recognised.)
        var october = await UploadAsync(owner, pair.October);
        Assert.True(october.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await october.Content.ReadAsStringAsync());
        var imported = (await october.Content.ReadFromJsonAsync<ArchiveImportDto>(TestClient.Json))!;
        Assert.Equal((october.StatusCode == HttpStatusCode.OK), imported.AlreadyImported);
        Assert.Equal(("2026-10", "VERIFIED", "Main store server"), (imported.Month, imported.Status, imported.Source));
        Assert.Equal(80m, imported.Datasets.Single(d => d.Name == "sales_invoices").Totals["grand_total"]);
        Assert.True(imported.Datasets.Single(d => d.Name == "stores").Master);

        // The same package again, or an identical month, adds nothing.
        var again = await UploadAsync(owner, pair.October);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await again.Content.ReadFromJsonAsync<ArchiveImportDto>(TestClient.Json))!.AlreadyImported);
        await Ok<ArchiveImportDto>(UploadAsync(owner, pair.September));

        var businesses = await owner.GetJsonAsync<List<ArchiveBusinessDto>>("/api/v1/archive/businesses");
        var business = Assert.Single(businesses);
        Assert.Equal(pair.Store.BusinessId, business.Id);
        Assert.Equal(["2026-09", "2026-10"], business.Months);
        Assert.Equal("MAIN", Assert.Single(business.Stores).Code);

        // Approved by the accountant first, then the owner, two different people.
        var (accountant, _) = await ArchiveUserAsync(owner, new ArchiveGrantRequest("archive_accountant"));
        using (accountant)
        {
            Assert.Equal("archive_import.not_waiting_for_owner",
                await (await owner.PostJsonAsync($"/api/v1/archive/imports/{imported.Id}/approve-owner", new ArchiveApprovalRequest(null, imported.RowVersion))).ProblemCodeAsync());
            Assert.Equal(HttpStatusCode.Forbidden,
                (await accountant.PostJsonAsync($"/api/v1/archive/imports/{imported.Id}/approve-owner", new ArchiveApprovalRequest(null, imported.RowVersion))).StatusCode);
            imported = await Ok<ArchiveImportDto>(accountant.PostJsonAsync($"/api/v1/archive/imports/{imported.Id}/approve-accounts",
                new ArchiveApprovalRequest("Sales and GST agree with the GSTR-1", imported.RowVersion)));
            Assert.Equal("ACCOUNTANT_APPROVED", imported.Status);
        }

        imported = await Ok<ArchiveImportDto>(owner.PostJsonAsync($"/api/v1/archive/imports/{imported.Id}/approve-owner", new ArchiveApprovalRequest("OK", imported.RowVersion)));
        Assert.Equal(("APPROVED", "Archive archive_accountant", "Archive Owner"), (imported.Status, imported.AccountantApprovedBy, imported.OwnerApprovedBy));

        // The owner alone cannot approve both steps of another month.
        var september = (await owner.GetJsonAsync<List<ArchiveImportDto>>("/api/v1/archive/imports")).Single(i => i.Month == "2026-09");
        september = await Ok<ArchiveImportDto>(owner.PostJsonAsync($"/api/v1/archive/imports/{september.Id}/approve-accounts", new ArchiveApprovalRequest(null, september.RowVersion)));
        Assert.Equal("archive_import.same_approver",
            await (await owner.PostJsonAsync($"/api/v1/archive/imports/{september.Id}/approve-owner", new ArchiveApprovalRequest(null, september.RowVersion))).ProblemCodeAsync());
    }

    [Fact]
    public async Task Only_trusted_unchanged_packages_are_imported_and_only_by_those_allowed()
    {
        using var owner = await Archive.LoginAsync(ArchivePair.OwnerUsername, ArchivePair.OwnerPassword);

        // A changed byte anywhere is refused; so is something that is not a package.
        var tampered = pair.October.ToArray();
        tampered[^20] ^= 0x01;
        Assert.Equal("archive.package_invalid", await (await UploadAsync(owner, tampered)).ProblemCodeAsync());
        Assert.Equal("archive.package_invalid", await (await UploadAsync(owner, "not a package"u8.ToArray())).ProblemCodeAsync());

        // A report user cannot import; a manager limited to another business cannot either.
        var (reportUser, _) = await ArchiveUserAsync(owner, new ArchiveGrantRequest("archive_report_user"));
        using (reportUser)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(reportUser, pair.October)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reportUser.GetAsync("/api/v1/archive/users")).StatusCode);
        }

        // A key that is not a store server's is refused; an unknown store server's packages are refused.
        Assert.Equal("archive.key_invalid", await (await owner.PostJsonAsync("/api/v1/archive/sources", new RegisterArchiveSourceRequest("Bad", "nope"))).ProblemCodeAsync());
        var keys = await owner.GetJsonAsync<ArchiveServerKeysDto>("/api/v1/archive/keys");
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var archiveKey = ECDiffieHellman.Create();
        archiveKey.ImportFromPem(keys.PublicKeyPem);
        var unknown = ArchivePackage.Write(new ArchiveManifest(0, Guid.CreateVersion7(), pair.Store.BusinessId, "SMKT", "Test", "2026-08", DateTimeOffset.UtcNow,
            Guid.NewGuid(), []), [], stranger, archiveKey, out _);
        Assert.Equal("archive.source_unknown", await (await UploadAsync(owner, unknown)).ProblemCodeAsync());

        // A store server can be registered and stopped; it is then no longer trusted.
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var registered = await Ok<ArchiveSourceDto>(owner.PostJsonAsync("/api/v1/archive/sources", new RegisterArchiveSourceRequest("Second store", second.ExportSubjectPublicKeyInfoPem())));
        Assert.Equal("archive_source.exists",
            await (await owner.PostJsonAsync("/api/v1/archive/sources", new RegisterArchiveSourceRequest("Again", second.ExportSubjectPublicKeyInfoPem()))).ProblemCodeAsync());
        await (await owner.PostJsonAsync($"/api/v1/archive/sources/{registered.Id}/revoke", new { })).EnsureSuccessWithBodyAsync();
        Assert.False((await owner.GetJsonAsync<List<ArchiveSourceDto>>("/api/v1/archive/sources")).Single(x => x.Id == registered.Id).IsActive);

        // Roles: nobody changes their own; the last owner administrator stays.
        var self = await owner.GetJsonAsync<ArchiveMeDto>("/api/v1/archive/me");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutJsonAsync($"/api/v1/archive/users/{self.UserId}/active", new SetUserActiveRequest(false))).StatusCode);
        Assert.Equal("archive_role.unknown",
            await (await owner.PostJsonAsync("/api/v1/archive/users", new CreateArchiveUserRequest("someone1", "Someone", "Temporary-Pass-001", new ArchiveGrantRequest("owner"))))
                .ProblemCodeAsync());
    }

    [Fact]
    public async Task The_archive_serves_only_the_archive_and_the_store_server_no_archive()
    {
        using var owner = await Archive.LoginAsync(ArchivePair.OwnerUsername, ArchivePair.OwnerPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/pos/context")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/businesses")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostJsonAsync("/api/v1/setup", new { })).StatusCode);

        using var storeOwner = await pair.Store.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await storeOwner.GetAsync("/api/v1/archive/imports")).StatusCode);
    }

    [Fact]
    public async Task The_archive_database_keeps_imported_months_as_they_were()
    {
        using var owner = await Archive.LoginAsync(ArchivePair.OwnerUsername, ArchivePair.OwnerPassword);
        var import = await UploadAsync(owner, pair.October);
        Assert.True(import.IsSuccessStatusCode, await import.Content.ReadAsStringAsync());

        await using var db = await Archive.OpenAppConnectionAsync(pair.ArchiveTenantId);
        foreach (var sql in new[]
                 {
                     "UPDATE archive_records SET data = '{}'::jsonb WHERE dataset = 'sales_invoices'",
                     "DELETE FROM archive_records WHERE dataset = 'sales_invoices'",
                     "UPDATE archive_imports SET file_sha256 = repeat('0', 64)",
                     "UPDATE archive_imports SET status = 'APPROVED', owner_approved_by_user_id = imported_by_user_id, owner_approved_at_utc = now() WHERE status = 'VERIFIED'",
                     "DELETE FROM archive_imports",
                     "DELETE FROM archive_masters",
                     "DELETE FROM archive_sources",
                 })
        {
            await using var command = new NpgsqlCommand(sql, db);
            var error = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
            Assert.True(error is PostgresException { SqlState: PostgresErrorCodes.RestrictViolation }, $"Not refused: {sql} ({error?.Message})");
        }

        await using var admin = await TestDatabase.OpenAdminAsync(Archive.DatabaseName);
        await using var verify = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(Inventory.StockTests.RepoRoot(), "database", "verification", "019_archive_server.sql")), admin);
        await verify.ExecuteNonQueryAsync();
    }
}
