using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;
using SupermarketBilling.IntegrationTests.Reporting;

namespace SupermarketBilling.IntegrationTests.Sales;

/// <summary>
/// Offline counter billing (D-039): the pack a counter agent gets, bills priced and issued from it with the shared domain
/// code (as the agent does), and how the server receives them.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class OfflineBillingTests(ApiFactory factory)
{
    private const string Pack = "/api/v1/pos/offline/pack";
    private const string Sync = "/api/v1/pos/offline/sync";

    private DateTimeOffset Now => factory.Clock.GetUtcNow();

    private sealed record OfflineCounter(TestClient Browser, CounterDto Counter, CounterDeviceDto Device, string Cashier, string Password);

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    /// <summary>A counter PC whose device the owner lets bill offline (20 bills, Rs. 5,000, 8 hours), with the cashier's shift open.</summary>
    private async Task<OfflineCounter> CounterAsync(TestClient owner, Guid business, Guid store, string? cashier = null, string? password = null)
    {
        var session = await Pos.CounterBrowserAsync(factory, business, store, cashier, password);
        var device = (await owner.GetJsonAsync<List<CounterDeviceDto>>($"/api/v1/businesses/{business}/counters/{session.Counter.Id}/devices")).Single();
        var allowed = await Ok<CounterDeviceDto>(owner.PutJsonAsync($"/api/v1/businesses/{business}/counters/{session.Counter.Id}/devices/{device.Id}/offline",
            new CounterOfflineRequest(20, 5000m, 8)));
        Assert.Equal((20, 5000m, 8), (allowed.OfflineMaxBills, allowed.OfflineMaxAmount, allowed.OfflineMaxHours));
        return new OfflineCounter(session.Browser, session.Counter, allowed, session.Username, session.Password);
    }

    /// <summary>What the counter agent does: price the cart from the pack and issue it with the next number.</summary>
    private OfflineBill Bill(OfflinePack pack, long sequence, OfflineCart cart, params PaymentInput[] payments)
    {
        var draft = OfflineBilling.Price(pack, cart, Now);
        return OfflineBilling.Issue(pack, draft, payments.Length > 0 ? payments : [new PaymentInput("CASH", draft.Result.GrandTotal, null)], Guid.CreateVersion7(),
            sequence, Now);
    }

    private static OfflineCart Cart(params (Guid Pack, decimal Quantity)[] lines) =>
        new("RETAIL", lines.Select(l => new OfflineCartLine(l.Pack, l.Quantity)).ToList());

    private static Task<OfflineBillsSyncResponse> SyncAsync(TestClient browser, params OfflineBill[] bills) =>
        Ok<OfflineBillsSyncResponse>(browser.PostJsonAsync(Sync, new OfflineBillsSyncRequest(bills)));

    [Fact]
    public async Task Bills_issued_without_the_server_are_posted_as_issued_in_the_counters_offline_series()
    {
        var (business, _, _) = await ReportBusiness.EnsureAsync(factory);
        var store = await ReportBusiness.StoreAsync(factory, business, "OFL", "Offline billing store");
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (taxed, a) = await Pos.StockedProductAsync(owner, business, store, price: 105m, mrp: 110m, stock: 10, cost: 80m);
        var (_, b) = await Pos.StockedProductAsync(owner, business, store, price: 50m, supply: "EXEMPT");
        var cashier = await ReportBusiness.UserAsync(factory, "manager");
        cashier.Client.Dispose();
        var counter = await CounterAsync(owner, business, store, cashier.Username, cashier.Password);
        using var browser = counter.Browser;

        var context = await browser.GetJsonAsync<PosContextDto>("/api/v1/pos/context");
        Assert.Equal($"{counter.Counter.Code}/OF", context.OfflineSeries);
        var pack = await browser.GetJsonAsync<OfflinePack>(Pack);
        Assert.Equal(($"{counter.Counter.Code}/OF", 1L, "GST_REGULAR", counter.Device.Id), (pack.NumberPrefix, pack.NextSequence, pack.TaxMode, pack.DeviceId));
        var item = pack.Items.Single(i => i.VariantUnitId == a);
        Assert.Equal(5m, item.GstRatePercent);
        Assert.Equal([110m], item.Mrps);
        Assert.Contains(pack.Prices, p => p.VariantUnitId == a && p.Price == 105m);

        // Priced offline from the pack exactly as the server prices it online.
        var online = await Pos.PriceAsync(browser, Pos.Cart(new CartLineRequest(a, 3), new CartLineRequest(b, 2)));
        var offline = OfflineBilling.Price(pack, Cart((a, 3), (b, 2)), Now);
        Assert.Equal(online.Lines.Select(l => (l.PriceRuleId, l.UnitPrice, l.Taxable, l.Cgst, l.Sgst, l.Total)),
            offline.Lines.Select(l => ((Guid?)l.PriceRuleId, l.UnitPrice, l.Amounts.Taxable, l.Amounts.Cgst, l.Amounts.Sgst, l.Amounts.Total)));
        Assert.Equal((online.Kind, online.GrandTotal, online.RoundOff), (offline.Result.Kind, offline.Result.GrandTotal, offline.Result.RoundOff));

        // Offline: a tax invoice for walk-in cash, and one to a registered buyer paid by UPI.
        var first = Bill(pack, 1, Cart((a, 2), (b, 1)), new PaymentInput("CASH", 300m, null));
        var gstin = SupermarketBilling.Domain.Tax.Gstin.Complete("33AAACK1234C1Z");
        var second = Bill(pack, 2, Cart((a, 1)) with { Buyer = new OfflineBuyer("Kaveri Traders", gstin, null, null, null) }, new PaymentInput("UPI", 105m, "UPI-991"));
        Assert.Equal(($"{counter.Counter.Code}/OF-000001", "TAX_INVOICE", 260m, 40m), (first.Number, first.Kind, first.GrandTotal, first.ChangeDue));

        var synced = await SyncAsync(browser, second, first);
        Assert.Equal([("POSTED", first.Number), ("POSTED", second.Number)], synced.Results.Select(r => (r.Status, r.Number)));
        Assert.All(synced.Results, r => Assert.Null(r.Review));

        // Each is the invoice that was printed: number, time, prices and taxes.
        var invoice = await owner.GetJsonAsync<InvoiceDto>($"/api/v1/businesses/{business}/sales/invoices/{first.Id}");
        Assert.Equal((first.Number, first.IssuedAtUtc, 260m, first.CgstTotal, first.CgstTotal, 40m), (invoice.Number, invoice.IssuedAtUtc, invoice.GrandTotal, invoice.CgstTotal, invoice.SgstTotal, invoice.ChangeDue));
        Assert.True(invoice.CgstTotal > 0);
        Assert.Equal(first.Lines.Select(l => (l.PriceRuleId, l.UnitPrice, l.Amounts.Total)), invoice.Lines.Select(l => (l.PriceRuleId!.Value, l.UnitPrice, l.Total)));
        Assert.Equal(gstin, (await owner.GetJsonAsync<InvoiceDto>($"/api/v1/businesses/{business}/sales/invoices/{second.Id}")).BuyerGstin);
        Assert.Equal(7m, (await StockTests.OnHandAsync(owner, business, store, taxed))!.Quantity);

        // The counter's own series goes on separately.
        var onlineBill = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(b, 1)), 50m));
        Assert.Equal($"{counter.Counter.Code}-000001", onlineBill.Number);
        Assert.Equal(3L, (await browser.GetJsonAsync<OfflinePack>(Pack)).NextSequence);

        // A resend gets its first result; the same id with other contents is refused.
        var again = await SyncAsync(browser, first, first with { GrandTotal = 1m });
        Assert.Equal(["DUPLICATE", "REJECTED"], again.Results.Select(r => r.Status));

        // A gap: nothing after it is processed until the missing bill comes.
        var third = Bill(pack, 3, Cart((b, 2)));
        var fourth = Bill(pack, 4, Cart((b, 1)));
        var gap = await SyncAsync(browser, fourth);
        Assert.Equal(("NOT_PROCESSED", $"{counter.Counter.Code}/OF-000003 must come first."), (gap.Results[0].Status, gap.Results[0].Reason));
        Assert.Equal(["POSTED", "POSTED"], (await SyncAsync(browser, third, fourth)).Results.Select(r => r.Status));
        Assert.Equal("REJECTED", (await SyncAsync(browser, Bill(pack, 2, Cart((b, 1))))).Results[0].Status); // a used number

        var listed = await owner.GetJsonAsync<List<OfflineBillDto>>($"/api/v1/businesses/{business}/offline-bills?counterId={counter.Counter.Id}");
        Assert.Equal([fourth.Number, third.Number, second.Number, first.Number], listed.Select(l => l.Number));
        Assert.All(listed, l => Assert.Equal(("POSTED", counter.Device.Name), (l.Status, l.Device)));
    }

    [Fact]
    public async Task Doubtful_bills_are_posted_for_review_and_bills_that_cannot_be_recorded_wait_for_someone_else_to_decide()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack1) = await Pos.StockedProductAsync(owner, factory.BusinessId, factory.MainStoreId, price: 40m, stock: 2);
        var counter = await CounterAsync(owner, factory.BusinessId, factory.MainStoreId);
        using var browser = counter.Browser;
        var pack = await browser.GetJsonAsync<OfflinePack>(Pack);

        // A price list changed on the PC: the bill is consistent and was issued, so it is posted, flagged for review.
        var tampered = pack with { Prices = pack.Prices.Select(p => p with { Price = 1m }).ToList() };
        var cheap = Bill(tampered, 1, Cart((pack1, 3)));

        // Figures that do not add up cannot be recorded: the number is kept, the bill waits for a manager.
        var wrong = Bill(pack, 2, Cart((pack1, 1)));
        wrong = wrong with { GrandTotal = wrong.GrandTotal - 1, Payments = [new PaymentInput("CASH", wrong.GrandTotal - 1, null)] };
        var fine = Bill(pack, 3, Cart((pack1, 1)));
        var result = await SyncAsync(browser, cheap, wrong, fine);
        Assert.Equal(["POSTED", "QUARANTINED", "POSTED"], result.Results.Select(r => r.Status));
        Assert.Contains("is not a price that was in force", result.Results[0].Review, StringComparison.Ordinal);
        Assert.Contains("do not add up", result.Results[1].Reason, StringComparison.Ordinal);
        Assert.Null(result.Results[1].InvoiceId);

        // Stock went below zero (5 sold of 2): the goods had gone, so the bills are posted anyway.
        var business = $"/api/v1/businesses/{factory.BusinessId}";
        var review = Assert.Single(await owner.GetJsonAsync<List<OfflineBillDto>>($"{business}/offline-bills?status=REVIEW&counterId={counter.Counter.Id}"));
        var reviewed = await Ok<OfflineBillDto>(owner.PostJsonAsync($"{business}/offline-bills/{review.Id}/review",
            new ReviewOfflineBillRequest("Price list was edited on the PC; cashier warned", review.RowVersion)));
        Assert.NotNull(reviewed.ReviewedAtUtc);

        // The shift is closed while a bill is still on the PC: it cannot go into a closed shift.
        var late = Bill(pack, 4, Cart((pack1, 1)));
        await (await browser.PostJsonAsync("/api/v1/pos/shift/close", new CloseShiftRequest([], "Closing with an offline bill outstanding"))).EnsureSuccessWithBodyAsync();
        var closed = Assert.Single((await SyncAsync(browser, late)).Results);
        Assert.Equal("QUARANTINED", closed.Status);
        Assert.Contains("shift", closed.Reason, StringComparison.Ordinal);

        var waiting = await owner.GetJsonAsync<List<OfflineBillDto>>($"{business}/offline-bills?status=QUARANTINED&counterId={counter.Counter.Id}");
        Assert.Equal([late.Number, wrong.Number], waiting.Select(w => w.Number));

        // Not by the cashier who issued it; posted only once the cashier has a shift open again.
        using (var cashier = await factory.LoginAsync(counter.Cashier, counter.Password))
        {
            var self = await cashier.PostJsonAsync($"{business}/offline-bills/{waiting[0].Id}/resolve", new ResolveOfflineBillRequest(false, "Mine", waiting[0].RowVersion));
            Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        }

        var noShift = await owner.PostJsonAsync($"{business}/offline-bills/{waiting[0].Id}/resolve", new ResolveOfflineBillRequest(true, "Cash was in the drawer", waiting[0].RowVersion));
        Assert.Equal("offline_bill.no_shift", await noShift.ProblemCodeAsync());
        await (await browser.PostJsonAsync("/api/v1/pos/shift/open", new OpenShiftRequest([]))).EnsureSuccessWithBodyAsync();
        var posted = await Ok<OfflineBillDto>(owner.PostJsonAsync($"{business}/offline-bills/{waiting[0].Id}/resolve",
            new ResolveOfflineBillRequest(true, "Cash was in the drawer", waiting[0].RowVersion)));
        Assert.Equal(("RESOLVED_POSTED", late.Id), (posted.Status, posted.InvoiceId));
        Assert.Equal(late.Number, (await owner.GetJsonAsync<InvoiceDto>($"{business}/sales/invoices/{late.Id}")).Number);

        // What does not add up cannot be posted; it is recorded as not posted, with why.
        var refused = await owner.PostJsonAsync($"{business}/offline-bills/{waiting[1].Id}/resolve", new ResolveOfflineBillRequest(true, "Try", waiting[1].RowVersion));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var voided = await Ok<OfflineBillDto>(owner.PostJsonAsync($"{business}/offline-bills/{waiting[1].Id}/resolve",
            new ResolveOfflineBillRequest(false, "Printed with a wrong total; customer re-billed", waiting[1].RowVersion)));
        Assert.Equal(("RESOLVED_VOID", (Guid?)null), (voided.Status, voided.InvoiceId));
    }

    [Fact]
    public async Task Only_one_device_of_a_counter_bills_offline_and_only_its_own_bills_are_received()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, item) = await Pos.StockedProductAsync(owner, factory.BusinessId, factory.MainStoreId, price: 30m);
        var cashierUser = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        cashierUser.Client.Dispose();
        var counter = await CounterAsync(owner, factory.BusinessId, factory.MainStoreId, cashierUser.Username, cashierUser.Password);
        using var browser = counter.Browser;
        var business = $"/api/v1/businesses/{factory.BusinessId}";
        var devices = $"{business}/counters/{counter.Counter.Id}/devices";

        // A second PC on the same counter may not bill offline as well: two agents would number the same series.
        using var second = factory.CreateBrowserClient();
        await Pos.SignInAsync(second, ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var other = await Ok<CounterDeviceDto>(second.PostJsonAsync(devices, new EnrolDeviceRequest("Second PC")));
        var refused = await owner.PutJsonAsync($"{devices}/{other.Id}/offline", new CounterOfflineRequest(5, 1000m, 4));
        Assert.Equal("offline.other_device", await refused.ProblemCodeAsync());
        Assert.Equal("offline.hours_invalid", await (await owner.PutJsonAsync($"{devices}/{counter.Device.Id}/offline", new CounterOfflineRequest(5, 1000m, 0))).ProblemCodeAsync());
        Assert.Equal("offline.not_allowed", await (await second.GetAsync(Pack)).ProblemCodeAsync());

        // A cashier cannot change it.
        using (var cashier = await factory.LoginAsync(counter.Cashier, counter.Password))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutJsonAsync($"{devices}/{counter.Device.Id}/offline", new CounterOfflineRequest(null, null, null))).StatusCode);
        }

        var pack = await browser.GetJsonAsync<OfflinePack>(Pack);
        var bill = Bill(pack, 1, Cart((item, 1)));

        // Bills only arrive from the device that issued them.
        var fromOther = Assert.Single((await SyncAsync(second, bill)).Results);
        Assert.Equal("REJECTED", fromOther.Status);

        // Stopping offline billing stops new packs, but bills already issued still arrive.
        await Ok<CounterDeviceDto>(owner.PutJsonAsync($"{devices}/{counter.Device.Id}/offline", new CounterOfflineRequest(null, null, null)));
        Assert.Equal("offline.not_allowed", await (await browser.GetAsync(Pack)).ProblemCodeAsync());
        Assert.Equal("POSTED", Assert.Single((await SyncAsync(browser, bill)).Results).Status);
    }

    [Fact]
    public async Task The_same_bills_sent_at_once_are_posted_once_each()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, item) = await Pos.StockedProductAsync(owner, factory.BusinessId, factory.MainStoreId, price: 10m, stock: 500);
        var counter = await CounterAsync(owner, factory.BusinessId, factory.MainStoreId);
        using var browser = counter.Browser;
        var pack = await browser.GetJsonAsync<OfflinePack>(Pack);
        // Each bill sent by four requests at the same moment (a retry racing the first attempt): one posts it, the others get its result.
        var bills = Enumerable.Range(1, 15).Select(i => Bill(pack, i, Cart((item, 1)))).ToArray();
        foreach (var bill in bills)
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => browser.PostJsonAsync(Sync, new OfflineBillsSyncRequest([bill])))));
            var statuses = new List<string>();
            foreach (var response in responses)
            {
                await response.EnsureSuccessWithBodyAsync();
                statuses.Add(Assert.Single((await response.Content.ReadFromJsonAsync<OfflineBillsSyncResponse>(TestClient.Json))!.Results).Status);
            }

            Assert.Equal(["DUPLICATE", "DUPLICATE", "DUPLICATE", "POSTED"], statuses.Order());
        }

        var listed = await owner.GetJsonAsync<List<OfflineBillDto>>($"/api/v1/businesses/{factory.BusinessId}/offline-bills?counterId={counter.Counter.Id}");
        Assert.Equal(15, listed.Count);
        Assert.All(listed, l => Assert.True(l.Status == "POSTED", l.Reason));
        await using var db = await factory.OpenAppConnectionAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM sales_invoices WHERE counter_id = @c", db);
        count.Parameters.AddWithValue("c", counter.Counter.Id);
        Assert.Equal(15L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task The_database_keeps_offline_bills_as_received()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, item) = await Pos.StockedProductAsync(owner, factory.BusinessId, factory.MainStoreId, price: 25m);
        var counter = await CounterAsync(owner, factory.BusinessId, factory.MainStoreId);
        using var browser = counter.Browser;
        var pack = await browser.GetJsonAsync<OfflinePack>(Pack);
        var good = Bill(pack, 1, Cart((item, 1)));
        var bad = Bill(pack, 2, Cart((item, 1))) with { GrandTotal = 1m };
        Assert.Equal(["POSTED", "QUARANTINED"], (await SyncAsync(browser, good, bad)).Results.Select(r => r.Status));

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[]
                     {
                         "UPDATE offline_bills SET grand_total = 1 WHERE id = @g",
                         "UPDATE offline_bills SET payload = '{}' WHERE id = @b",
                         "UPDATE offline_bills SET status = 'POSTED', invoice_id = @g WHERE id = @b",
                         "DELETE FROM offline_bills WHERE id = @b",
                         "UPDATE offline_bills SET reviewed_at_utc = now(), reviewed_by_user_id = cashier_user_id, resolution_note = 'x' WHERE id = @g",
                     })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("g", good.Id);
                command.Parameters.AddWithValue("b", bad.Id);
                Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }

            // A second device of the counter cannot be let offline behind the service's back either.
            using var second = factory.CreateBrowserClient();
            await Pos.SignInAsync(second, ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
            var other = await Ok<CounterDeviceDto>(second.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/counters/{counter.Counter.Id}/devices",
                new EnrolDeviceRequest("Second PC")));
            await using var twice = new NpgsqlCommand("UPDATE counter_devices SET offline_max_bills = 1, offline_max_amount = 1, offline_max_hours = 1 WHERE id = @d", db);
            twice.Parameters.AddWithValue("d", other.Id);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => twice.ExecuteNonQueryAsync())).SqlState);
        }

        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using var verify = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "017_offline_bills.sql")), admin);
        await verify.ExecuteNonQueryAsync();
    }
}
