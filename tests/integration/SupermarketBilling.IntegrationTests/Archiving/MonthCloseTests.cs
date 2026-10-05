using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Archiving;
using SupermarketBilling.IntegrationTests.Accounts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Archiving;

/// <summary>
/// Monthly close (spec section 22): checks, lock, and the archive package. On its own installation, because locking a
/// month and moving the clock a month on would disturb every other test.
/// </summary>
public sealed class MonthCloseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private string Base => $"/api/v1/businesses/{factory.BusinessId}";

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    [Fact]
    public async Task A_month_is_locked_in_order_once_complete_and_packaged_for_the_archive_only()
    {
        // September: a debtor's opening balance. October: a bill in a shift.
        Guid invoiceId;
        TestClient counter;
        using (var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword))
        {
            await Ledgers.DebtorAsync(owner, factory.BusinessId, opening: 500m);
            var (_, pack) = await Pos.StockedProductAsync(owner, factory.BusinessId, factory.MainStoreId, price: 40m);
            var session = await Pos.CounterBrowserAsync(factory, factory.BusinessId, factory.MainStoreId);
            counter = session.Browser;
            invoiceId = (await Pos.IssueAsync(counter, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)), 80m, new PaymentRequest("UPI", 80m, "UTR-1")))).Id;
        }

        // December 2nd: September, October and November are over.
        factory.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 2, 5, 0, 0, TimeSpan.Zero));
        using var manager = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var months = await manager.GetJsonAsync<List<MonthStatusDto>>($"{Base}/months");
        Assert.Equal([("2026-12", "IN_PROGRESS"), ("2026-11", "OPEN"), ("2026-10", "OPEN"), ("2026-09", "OPEN")], months.Select(m => (m.Month, m.State)));

        // October cannot be locked before September, nor while its shift is open; December is not over.
        var october = await manager.GetJsonAsync<MonthChecksDto>($"{Base}/months/2026-10/checks");
        Assert.False(october.Ready);
        Assert.Equal(["earlier_locked", "shifts_closed"], october.Checks.Where(c => !c.Passed).Select(c => c.Key));
        Assert.Contains("2026-09", october.Checks.Single(c => c.Key == "earlier_locked").Detail, StringComparison.Ordinal);
        Assert.Equal("month.not_ready", await (await manager.PostJsonAsync($"{Base}/months/2026-10/lock", new LockMonthRequest(null))).ProblemCodeAsync());
        Assert.Contains((await manager.GetJsonAsync<MonthChecksDto>($"{Base}/months/2026-12/checks")).Checks, c => c.Key == "month_over" && !c.Passed);

        var september = await Ok<MonthStatusDto>(manager.PostJsonAsync($"{Base}/months/2026-09/lock", new LockMonthRequest("Opening balances")));
        Assert.Equal(("LOCKED", "Owner One"), (september.State, september.LockedBy));
        Assert.Equal("month.locked", await (await manager.PostJsonAsync($"{Base}/months/2026-09/lock", new LockMonthRequest(null))).ProblemCodeAsync());

        // A manager closes the counter's shift (paid by UPI, no float: nothing to count); October passes and is locked.
        counter.Dispose();
        var open = Assert.Single(await manager.GetJsonAsync<List<ShiftSummaryDto>>($"{Base}/shifts?storeId={factory.MainStoreId}"), s => s.Status == "OPEN");
        await (await manager.PostJsonAsync($"{Base}/shifts/{open.Id}/close", new CloseShiftRequest([], "Month end"))).EnsureSuccessWithBodyAsync();

        october = await manager.GetJsonAsync<MonthChecksDto>($"{Base}/months/2026-10/checks");
        Assert.True(october.Ready, string.Join("; ", october.Checks.Where(c => !c.Passed).Select(c => $"{c.Key}: {c.Detail}")));
        await Ok<MonthStatusDto>(manager.PostJsonAsync($"{Base}/months/2026-10/lock", new LockMonthRequest(null)));

        // Nothing dated in a locked month can be recorded or changed any more.
        var late = await manager.PostJsonAsync($"{Base}/debtors", new CreateDebtorRequest("LATE1", "Late opening", null, null, "33", CreditPeriodDays: 15, CreditLimit: 1000m,
            OpeningBalance: 100m, OpeningBalanceDate: new DateOnly(2026, 10, 15)));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("locked", (await late.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[]
                     {
                         "UPDATE shifts SET close_note = 'changed' WHERE business_id = @b AND business_date = '2026-10-01'",
                         "DELETE FROM month_locks WHERE business_id = @b",
                     })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("b", factory.BusinessId);
                Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
        }

        // The package is encrypted for the registered archive only, and signed by this server.
        Assert.Equal("archive.no_recipient", await (await manager.PostJsonAsync($"{Base}/months/2026-10/package", new { })).ProblemCodeAsync());
        Assert.Equal("archive.key_invalid", await (await manager.PutJsonAsync($"{Base}/archive/recipient", new SetArchiveRecipientRequest("not a key", null))).ProblemCodeAsync());
        using var archive = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var keys = await Ok<ArchiveKeysDto>(manager.PutJsonAsync($"{Base}/archive/recipient", new SetArchiveRecipientRequest(archive.ExportSubjectPublicKeyInfoPem(), null)));
        Assert.Equal(ArchivePackage.KeyId(archive.ExportSubjectPublicKeyInfo()), keys.RecipientKeyId);
        Assert.Equal("month.not_locked", await (await manager.PostJsonAsync($"{Base}/months/2026-11/package", new { })).ProblemCodeAsync());

        var response = await manager.PostJsonAsync($"{Base}/months/2026-10/package", new { });
        await response.EnsureSuccessWithBodyAsync();
        var file = await response.Content.ReadAsByteArrayAsync();
        using var signer = ECDsa.Create();
        signer.ImportFromPem(keys.SignerPublicKeyPem);
        var contents = ArchivePackage.Open(file, archive, id => id == keys.SignerKeyId ? signer : null);
        Assert.Equal(("2026-10", factory.BusinessId), (contents.Manifest.Month, contents.Manifest.BusinessId));
        var invoice = Assert.Single(contents.Datasets["sales_invoices"]);
        Assert.Equal(invoiceId, invoice.GetProperty("id").GetGuid());
        Assert.Equal(80m, contents.Manifest.Datasets.Single(d => d.Name == "sales_invoices").Totals["grand_total"]);
        Assert.Equal(2, contents.Datasets["stock_ledger"].Count(r => r.GetProperty("movement_type").GetString() == "OPENING" || r.GetProperty("movement_type").GetString() == "SALE"));
        Assert.Single(contents.Datasets["shifts"]);
        Assert.NotEmpty(contents.Datasets["audit_events"]);
        Assert.Empty(contents.Datasets["debtor_ledger"]); // the opening balance is September's

        // No secrets travel: users carry names only.
        Assert.All(contents.Datasets["users"], u => Assert.False(u.TryGetProperty("password_hash", out _) || u.TryGetProperty("mfa_secret_protected", out _)));
        Assert.DoesNotContain("token_hash", System.Text.Encoding.UTF8.GetString(file), StringComparison.Ordinal);

        // Another archive cannot open it; the server keeps its checksum.
        using var stranger = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArchivePackageException>(() => ArchivePackage.Open(file, stranger, id => id == keys.SignerKeyId ? signer : null));
        var packages = await manager.GetJsonAsync<List<MonthPackageDto>>($"{Base}/months/2026-10/packages");
        Assert.Equal(ArchivePackage.FileSha256(file), Assert.Single(packages).FileSha256);
        Assert.Equal(1, (await manager.GetJsonAsync<List<MonthStatusDto>>($"{Base}/months")).Single(m => m.Month == "2026-10").Packages);

        // Only those allowed may lock; a cashier cannot see the months.
        var cashier = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        using (cashier.Client)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Client.GetAsync($"{Base}/months")).StatusCode);
        }

        var auditor = await factory.CreateSignedInUserAsync("auditor");
        using (auditor.Client)
        {
            Assert.Equal(HttpStatusCode.OK, (await auditor.Client.GetAsync($"{Base}/months")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await auditor.Client.PostJsonAsync($"{Base}/months/2026-11/lock", new LockMonthRequest(null))).StatusCode);
        }

        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using var verify = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "018_month_close.sql")), admin);
        await verify.ExecuteNonQueryAsync();
    }
}
