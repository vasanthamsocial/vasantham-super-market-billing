using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Archiving;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Archiving;

/// <summary>
/// Retention gates (D-044): the archive's receipt, the owner, the months always kept; a dry run, nothing removed. A pair
/// of its own: it approves the months, which the other archive tests expect still waiting.
/// </summary>
public sealed class RetentionTests(ArchivePair pair) : IClassFixture<ArchivePair>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The month imported and approved by an accountant and then a second owner administrator (whatever earlier tests did).</summary>
    private async Task<ArchiveImportDto> ApprovedAsync(TestClient owner, string month)
    {
        var import = (await owner.GetJsonAsync<List<ArchiveImportDto>>("/api/v1/archive/imports")).Single(i => i.Month == month);
        if (import.Status == "VERIFIED")
        {
            var (accountant, _) = await ArchiveServerTests.ArchiveUserAsync(pair.Archive, owner, new ArchiveGrantRequest("archive_accountant"));
            using (accountant)
            {
                import = await ArchiveServerTests.Ok<ArchiveImportDto>(accountant.PostJsonAsync($"/api/v1/archive/imports/{import.Id}/approve-accounts",
                    new ArchiveApprovalRequest(null, import.RowVersion)));
            }
        }

        if (import.Status == "ACCOUNTANT_APPROVED")
        {
            var (second, _) = await ArchiveServerTests.ArchiveUserAsync(pair.Archive, owner, new ArchiveGrantRequest("archive_owner"));
            using (second)
            {
                import = await ArchiveServerTests.Ok<ArchiveImportDto>(second.PostJsonAsync($"/api/v1/archive/imports/{import.Id}/approve-owner",
                    new ArchiveApprovalRequest(null, import.RowVersion)));
            }
        }

        Assert.Equal("APPROVED", import.Status);
        return import;
    }

    private static async Task<HttpResponseMessage> RegisterReceiptAsync(TestClient client, Guid businessId, byte[] receipt)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(receipt);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "file", "month.sbreceipt");
        return await client.Http.PostAsync(new Uri($"/api/v1/businesses/{businessId}/retention/receipts", UriKind.Relative), form);
    }

    [Fact]
    public async Task An_archived_month_is_cleared_for_removal_only_with_the_archive_receipt_and_only_by_the_owner()
    {
        using var archiveOwner = await pair.ImportedAsync();
        var october = await ApprovedAsync(archiveOwner, "2026-10");
        var september = await ApprovedAsync(archiveOwner, "2026-09");

        // The archive gives receipts for approved months, to its owner administrators only, and keeps a record of each.
        var (reader, _) = await ArchiveServerTests.ArchiveUserAsync(pair.Archive, archiveOwner, new ArchiveGrantRequest("archive_report_user"));
        using (reader)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v1/archive/imports/{october.Id}/receipt")).StatusCode);
        }

        var octoberReceipt = await (await archiveOwner.GetAsync($"/api/v1/archive/imports/{october.Id}/receipt")).Content.ReadAsByteArrayAsync();
        var septemberReceipt = await (await archiveOwner.GetAsync($"/api/v1/archive/imports/{september.Id}/receipt")).Content.ReadAsByteArrayAsync();
        var archiveKeys = await archiveOwner.GetJsonAsync<ArchiveServerKeysDto>("/api/v1/archive/keys");
        Assert.NotEqual(archiveKeys.KeyId, archiveKeys.ReceiptKeyId);

        // On the store server: the months of the current and the previous financial year are always kept.
        var business = pair.Store.BusinessId;
        var retention = $"/api/v1/businesses/{business}/retention";
        using (var owner = await pair.Store.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword))
        {
            var recent = await owner.GetJsonAsync<RetentionPlanDto>($"{retention}/months/2026-10");
            Assert.False(recent.Checks.Single(c => c.Code == "old_enough").Passed);
            Assert.False(recent.Ready);
        }

        // Some years later.
        pair.Store.Clock.SetUtcNow(new DateTimeOffset(2028, 6, 1, 5, 0, 0, TimeSpan.Zero));
        using var storeOwner = await pair.Store.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var status = await storeOwner.GetJsonAsync<RetentionDto>(retention);
        Assert.Equal((true, "2027-04", true), (status.Enabled, status.KeepFrom, status.ArchiveRegistered));
        Assert.Equal("2026-09", status.Months[0].Month);

        // The receipt key: not the archive's encryption key, not something else, and only by the owner.
        Assert.Equal("retention.wrong_key", await (await storeOwner.PutJsonAsync($"{retention}/receipt-key", new SetReceiptKeyRequest(archiveKeys.PublicKeyPem))).ProblemCodeAsync());
        Assert.Equal("archive.key_invalid", await (await storeOwner.PutJsonAsync($"{retention}/receipt-key", new SetReceiptKeyRequest("nope"))).ProblemCodeAsync());
        Assert.Equal("retention.receipt_key_missing", await (await RegisterReceiptAsync(storeOwner, business, octoberReceipt)).ProblemCodeAsync());
        var (accountant, _, _, _) = await pair.Store.CreateSignedInUserAsync("accountant");
        using var accountantSession = accountant;
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutJsonAsync($"{retention}/receipt-key", new SetReceiptKeyRequest(archiveKeys.ReceiptPublicKeyPem))).StatusCode);

        status = await ArchiveServerTests.Ok<RetentionDto>(storeOwner.PutJsonAsync($"{retention}/receipt-key", new SetReceiptKeyRequest(archiveKeys.ReceiptPublicKeyPem)));
        Assert.Equal(archiveKeys.ReceiptKeyId, status.ReceiptKeyId);

        // A changed receipt, one signed by another key, or something else is refused.
        var envelope = JsonNode.Parse(octoberReceipt)!;
        var payloadBytes = Convert.FromBase64String(envelope["payload"]!.GetValue<string>());
        var changed = payloadBytes.ToArray();
        changed[^5] ^= 0x01;
        envelope["payload"] = Convert.ToBase64String(changed);
        Assert.Equal("retention.receipt_invalid", await (await RegisterReceiptAsync(storeOwner, business, Encoding.UTF8.GetBytes(envelope.ToJsonString()))).ProblemCodeAsync());
        using (var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var forged = ArchiveReceipt.Write(JsonSerializer.Deserialize<ArchiveReceiptPayload>(payloadBytes, Web)!, stranger);
            Assert.Equal("retention.receipt_invalid", await (await RegisterReceiptAsync(storeOwner, business, forged)).ProblemCodeAsync());
        }

        Assert.Equal("retention.receipt_invalid", await (await RegisterReceiptAsync(storeOwner, business, "not a receipt"u8.ToArray())).ProblemCodeAsync());

        // October's receipt: every gate but one passes; September, the earliest month, must go first.
        var octoberPlan = await ArchiveServerTests.Ok<RetentionPlanDto>(RegisterReceiptAsync(storeOwner, business, octoberReceipt));
        Assert.Equal("2026-10", octoberPlan.Month);
        Assert.All(octoberPlan.Checks.Where(c => c.Code != "earlier_removed"), c => Assert.True(c.Passed, c.Code + ": " + c.Detail));
        Assert.False(octoberPlan.Checks.Single(c => c.Code == "earlier_removed").Passed);
        Assert.False(octoberPlan.Ready);
        Assert.Empty(octoberPlan.Blockers);
        Assert.Equal(2, octoberPlan.Removed.Single(c => c.Dataset == "sales_invoices").Records);
        Assert.Equal(1, octoberPlan.Removed.Single(c => c.Dataset == "sales_returns").Records);
        Assert.Contains(octoberPlan.Kept, c => c.Dataset == "shifts");
        Assert.Contains("nothing is removed", octoberPlan.Note, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await RegisterReceiptAsync(storeOwner, business, octoberReceipt)).StatusCode); // the same again

        var septemberPlan = await ArchiveServerTests.Ok<RetentionPlanDto>(RegisterReceiptAsync(storeOwner, business, septemberReceipt));
        Assert.True(septemberPlan.Ready, string.Join("; ", septemberPlan.Checks.Where(c => !c.Passed).Select(c => c.Code + ": " + c.Detail)));
        Assert.Contains(septemberPlan.Kept, c => c.Dataset == "debtor_ledger");

        // Others may look, not act.
        Assert.True((await accountant.GetJsonAsync<RetentionPlanDto>($"{retention}/months/2026-09")).Ready);
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterReceiptAsync(accountant, business, septemberReceipt)).StatusCode);

        status = await storeOwner.GetJsonAsync<RetentionDto>(retention);
        Assert.All(status.Months.Take(2), m => Assert.NotNull(m.ReceiptRegisteredAtUtc));

        // Nothing was removed, and the receipts cannot be changed.
        await using (var db = await pair.Store.OpenAppConnectionAsync())
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM sales_invoices WHERE business_date >= '2026-10-01' AND business_date < '2026-11-01'", db);
            Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
            foreach (var sql in new[] { "UPDATE month_removal_receipts SET file_sha256 = repeat('0', 64)", "DELETE FROM month_removal_receipts" })
            {
                await using var command = new NpgsqlCommand(sql, db);
                var error = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
                Assert.True(error is PostgresException { SqlState: PostgresErrorCodes.RestrictViolation }, $"Not refused: {sql} ({error?.Message})");
            }
        }

        await using var admin = await TestDatabase.OpenAdminAsync(pair.Store.DatabaseName);
        await using var verify = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(Inventory.StockTests.RepoRoot(), "database", "verification", "020_retention.sql")), admin);
        await verify.ExecuteNonQueryAsync();
    }
}

/// <summary>Retention is off unless the server's configuration switches it on.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RetentionDisabledTests(ApiFactory factory)
{
    [Fact]
    public async Task Retention_is_switched_off_by_default_and_then_nothing_can_be_done()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var retention = $"/api/v1/businesses/{factory.BusinessId}/retention";
        Assert.False((await owner.GetJsonAsync<RetentionDto>(retention)).Enabled);
        Assert.Equal("retention.disabled", await (await owner.PutJsonAsync($"{retention}/receipt-key", new SetReceiptKeyRequest("-----BEGIN PUBLIC KEY-----"))).ProblemCodeAsync());
        Assert.Equal("retention.disabled", await (await owner.GetAsync($"{retention}/months/2020-01")).ProblemCodeAsync());
    }
}
