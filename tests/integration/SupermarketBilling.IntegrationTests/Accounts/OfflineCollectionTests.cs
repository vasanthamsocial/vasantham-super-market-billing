using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Accounts;

[Collection(ApiTestGroup.Name)]
public sealed class OfflineCollectionTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    private string Base => $"/api/v1/businesses/{Business}";

    private const string Sync = "/api/v1/collections/device/sync";

    private DateTimeOffset Now => factory.Clock.GetUtcNow();

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    private sealed record Phone(TestClient Browser, Guid CollectorId, DebtorDto Debtor, CollectionDeviceDto Device);

    /// <summary>
    /// A collection person's phone, enrolled by the owner (limit Rs. 1,000, 24 hours), with the collector signed in on it,
    /// a party of theirs owing Rs. 2,000 and, unless told otherwise, their round open.
    /// </summary>
    private async Task<Phone> PhoneAsync(TestClient owner, bool openRound = true, decimal limit = 1000m)
    {
        var collector = await factory.CreateSignedInUserAsync("collection_person", Store);
        collector.Client.Dispose();
        var debtor = await Ledgers.DebtorAsync(owner, Business, opening: 2000m);
        (await owner.PutJsonAsync($"{Base}/debtors/{debtor.Id}/collection-plan",
            new SetCollectionPlanRequest(null, null, collector.UserId, null, null, null, "MANUAL"))).EnsureSuccessStatusCode();

        var browser = factory.CreateBrowserClient();
        await Pos.SignInAsync(browser, ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var device = await Ok<CollectionDeviceDto>(browser.PostJsonAsync($"{Base}/collections/devices",
            new EnrolCollectionDeviceRequest(collector.UserId, $"Phone {Guid.NewGuid():N}"[..14], limit, 24)));
        (await browser.PostJsonAsync("/api/v1/auth/logout", new { })).EnsureSuccessStatusCode();
        await Pos.SignInAsync(browser, collector.Username, collector.Password);
        if (openRound)
        {
            await Ok<CollectorSessionDto>(browser.PostJsonAsync($"{Base}/collections/sessions", new OpenCollectorSessionRequest(Store)));
        }

        return new Phone(browser, collector.UserId, debtor, device);
    }

    private OfflineItemRequest Item(Phone phone, long sequence, decimal amount, TimeSpan? ago = null, Guid? debtor = null) =>
        new(Guid.CreateVersion7(), sequence, debtor ?? phone.Debtor.Id, "CASH", amount, Now - (ago ?? TimeSpan.FromHours(1)));

    private static Task<OfflineSyncResponse> SyncAsync(Phone phone, params OfflineItemRequest[] items) =>
        Ok<OfflineSyncResponse>(phone.Browser.PostJsonAsync(Sync, new OfflineSyncRequest(items)));

    [Fact]
    public async Task Collections_from_a_phone_are_posted_in_order_once_with_the_balance_known_only_then()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var phone = await PhoneAsync(owner);
        using var browser = phone.Browser;
        var mine = await browser.GetJsonAsync<MyCollectionDeviceDto>("/api/v1/collections/device");
        Assert.Equal((phone.Device.Id, phone.CollectorId, 1000m, 24, 0L), (mine.DeviceId, mine.CollectorUserId, mine.OfflineLimit, mine.MaxOfflineHours, mine.LastSequence));

        var first = Item(phone, 1, 300m, TimeSpan.FromHours(3));
        var second = Item(phone, 2, 200m, TimeSpan.FromHours(2));
        var third = Item(phone, 3, 100m);

        // The third arrives first (the second was lost): nothing after a gap is posted.
        var gap = await SyncAsync(phone, first, third);
        Assert.Equal(["ACCEPTED", "NOT_PROCESSED"], gap.Results.Select(r => r.Status));
        Assert.Equal((1700m, 1L), (gap.Results[0].BalanceAfter!.Value, gap.LastSequence));
        Assert.Matches(@"^MAIN/RCT/\d{6}$", gap.Results[0].ReceiptNumber);

        // Everything again (a retry after a lost answer): the first is not posted twice.
        var all = await SyncAsync(phone, first, second, third);
        Assert.Equal(["DUPLICATE", "ACCEPTED", "ACCEPTED"], all.Results.Select(r => r.Status));
        Assert.Equal(gap.Results[0].ReceiptNumber, all.Results[0].ReceiptNumber);
        Assert.Equal((1400m, 3L), (all.Results[2].BalanceAfter!.Value, all.LastSequence));
        var receipts = await owner.GetJsonAsync<List<DebtorReceiptDto>>($"{Base}/debtor-receipts?debtorId={phone.Debtor.Id}");
        Assert.Equal([100m, 200m, 300m], receipts.Select(r => r.Amount).Order());
        Assert.All(receipts, r => Assert.NotNull(r.CollectorSessionId));

        // The same id with different details, and a sequence number used again, are refused and not kept.
        var changed = await SyncAsync(phone, first with { Amount = 999m }, Item(phone, 2, 50m));
        Assert.Equal(["REJECTED", "REJECTED"], changed.Results.Select(r => r.Status));
        Assert.Equal(3, (await owner.GetJsonAsync<List<OfflineSubmissionDto>>($"{Base}/collections/offline?collectorUserId={phone.CollectorId}")).Count);
    }

    [Fact]
    public async Task What_cannot_be_posted_is_quarantined_for_a_manager_who_is_not_the_collector()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var phone = await PhoneAsync(owner, openRound: false);
        using var browser = phone.Browser;
        var stranger = await Ledgers.DebtorAsync(owner, Business, opening: 100m);

        var result = await SyncAsync(phone,
            Item(phone, 1, 100m),                                    // no round open
            Item(phone, 2, 100m, TimeSpan.FromHours(30)),            // kept longer than 24 hours
            Item(phone, 3, 100m, TimeSpan.FromHours(-1)),            // the phone's clock is ahead
            Item(phone, 4, 950m),                                    // over the Rs. 1,000 limit with what came before
            Item(phone, 5, 50m, debtor: stranger.Id));               // not the collector's party
        Assert.All(result.Results, r => Assert.Equal("QUARANTINED", r.Status));
        Assert.Contains("round", result.Results[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("24 hours", result.Results[1].Reason, StringComparison.Ordinal);
        Assert.Contains("clock", result.Results[2].Reason, StringComparison.Ordinal);
        Assert.Contains("limit", result.Results[3].Reason, StringComparison.Ordinal);
        Assert.Equal(5L, result.LastSequence);
        Assert.Empty(await owner.GetJsonAsync<List<DebtorReceiptDto>>($"{Base}/debtor-receipts?debtorId={phone.Debtor.Id}"));

        // The collector sees their own quarantine but cannot decide it.
        var quarantined = await browser.GetJsonAsync<List<OfflineSubmissionDto>>($"{Base}/collections/offline?status=QUARANTINED");
        Assert.Equal(5, quarantined.Count);
        var noRound = quarantined.Single(q => q.Sequence == 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync($"{Base}/collections/offline/{noRound.Id}/resolve",
            new ResolveOfflineRequest(true, "Mine", Store, noRound.RowVersion))).StatusCode);

        // The money was handed in at the store: posted as a receipt there. The other one went back to the party.
        Assert.Equal("offline.note_required", await (await owner.PostJsonAsync($"{Base}/collections/offline/{noRound.Id}/resolve",
            new ResolveOfflineRequest(true, " ", Store, noRound.RowVersion))).ProblemCodeAsync());
        var accepted = await Ok<OfflineSubmissionDto>(owner.PostJsonAsync($"{Base}/collections/offline/{noRound.Id}/resolve",
            new ResolveOfflineRequest(true, "Cash handed in at the office", Store, noRound.RowVersion)));
        Assert.Equal("RESOLVED_ACCEPTED", accepted.Status);
        Assert.Matches(@"^MAIN/RCT/\d{6}$", accepted.ReceiptNumber);
        var old = quarantined.Single(q => q.Sequence == 2);
        var rejected = await Ok<OfflineSubmissionDto>(owner.PostJsonAsync($"{Base}/collections/offline/{old.Id}/resolve",
            new ResolveOfflineRequest(false, "Party says it was never paid; money returned", null, old.RowVersion)));
        Assert.Equal(("RESOLVED_REJECTED", (string?)null), (rejected.Status, rejected.ReceiptNumber));
        Assert.Equal("offline.not_quarantined", await (await owner.PostJsonAsync($"{Base}/collections/offline/{old.Id}/resolve",
            new ResolveOfflineRequest(false, "Again", null, rejected.RowVersion))).ProblemCodeAsync());
        Assert.Equal(1900m, (await owner.GetJsonAsync<DebtorDto>($"{Base}/debtors/{phone.Debtor.Id}")).Balance);
    }

    [Fact]
    public async Task Only_the_enrolled_phone_of_the_signed_in_collector_can_send_collections()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var phone = await PhoneAsync(owner);
        using var browser = phone.Browser;
        var item = Item(phone, 1, 10m);

        // Another browser (no device cookie), the owner on this phone, and a revoked phone are all refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostJsonAsync(Sync, new OfflineSyncRequest([item]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.GetAsync("/api/v1/collections/device")).StatusCode);
        (await owner.PostJsonAsync($"{Base}/collections/devices/{phone.Device.Id}/revoke", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync(Sync, new OfflineSyncRequest([item]))).StatusCode);

        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        using (cashier.Client)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Client.GetAsync($"{Base}/collections/devices")).StatusCode);
        }
    }

    [Fact]
    public async Task The_same_collections_sent_twice_at_once_are_posted_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var phone = await PhoneAsync(owner, limit: 10_000m);
        using var browser = phone.Browser;
        var items = Enumerable.Range(1, 60).Select(i => Item(phone, i, 5m)).ToArray();
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => browser.PostJsonAsync(Sync, new OfflineSyncRequest(items)))));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var receipts = await owner.GetJsonAsync<List<DebtorReceiptDto>>($"{Base}/debtor-receipts?debtorId={phone.Debtor.Id}");
        Assert.Equal(60, receipts.Count);
        Assert.Equal(1700m, (await owner.GetJsonAsync<DebtorDto>($"{Base}/debtors/{phone.Debtor.Id}")).Balance);
    }

    [Fact]
    public async Task A_collection_whose_answer_was_lost_and_was_then_kept_on_the_phone_is_posted_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var phone = await PhoneAsync(owner);
        using var browser = phone.Browser;

        // The Collection App sends it at once under the key it would sync it with; the server posts it but the answer is lost.
        var item = Item(phone, 1, 250m, TimeSpan.FromMinutes(1));
        var posted = await Ok<DebtorReceiptDto>(browser.PostJsonAsync($"{Base}/collections/receipts", new
        {
            debtorId = item.DebtorId, method = item.Method, amount = item.Amount, reference = (string?)null, bankName = (string?)null,
            chequeDate = (string?)null, idempotencyKey = "offline-" + item.Id.ToString("N"),
        }));

        // So the phone kept it, and synchronises it later: the same receipt, not a second one.
        var result = Assert.Single((await SyncAsync(phone, item)).Results);
        Assert.Equal(("ACCEPTED", posted.Number, 1750m), (result.Status, result.ReceiptNumber, result.BalanceAfter));
        Assert.Single(await owner.GetJsonAsync<List<DebtorReceiptDto>>($"{Base}/debtor-receipts?debtorId={phone.Debtor.Id}"));
        Assert.Equal(1750m, (await owner.GetJsonAsync<DebtorDto>($"{Base}/debtors/{phone.Debtor.Id}")).Balance);
    }

    [Fact]
    public async Task The_database_keeps_what_the_phone_sent_and_its_order()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var phone = await PhoneAsync(owner);
        using var browser = phone.Browser;
        var result = await SyncAsync(phone, Item(phone, 1, 100m), Item(phone, 2, 5000m));
        Assert.Equal(["ACCEPTED", "QUARANTINED"], result.Results.Select(r => r.Status));

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[]
                     {
                         "UPDATE offline_submissions SET amount = 1 WHERE device_id = @d AND sequence = 1",
                         "UPDATE offline_submissions SET status = 'RESOLVED_REJECTED', resolution_note = 'x', resolved_at_utc = now(), resolved_by_user_id = (SELECT enrolled_by_user_id FROM collection_devices WHERE id = @d) WHERE device_id = @d AND sequence = 1",
                         "DELETE FROM offline_submissions WHERE device_id = @d",
                         "UPDATE collection_devices SET last_sequence = 0 WHERE id = @d",
                         "UPDATE collection_devices SET collector_user_id = enrolled_by_user_id WHERE id = @d",
                         "DELETE FROM collection_devices WHERE id = @d",
                     })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("d", phone.Device.Id);
                Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
        }

        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using var verify = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "016_offline.sql")), admin);
        await verify.ExecuteNonQueryAsync();
    }
}
