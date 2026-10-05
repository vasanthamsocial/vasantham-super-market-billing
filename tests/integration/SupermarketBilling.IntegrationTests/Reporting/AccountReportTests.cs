using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Reporting;

[Collection(ApiTestGroup.Name)]
public sealed class AccountReportTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Day = "2026-10-01";
    private static readonly SemaphoreSlim Once = new(1, 1);
    private static Scenario? shared;

    private sealed record Scenario(Guid Business, Guid Store, string Debtor, string Route, string Challan, string Dispatch);

    private static Scenario S => shared!;

    private static string Base => $"/api/v1/businesses/{S.Business}/reports";

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    /// <summary>
    /// In a store of its own in the shared report business: debtor D owes Rs. 1,000 from 1 September (30 days overdue) and
    /// is on route R with a collection person. D promises Rs. 200 by 5 October, buys 2 x Rs. 105 on account (due in 10
    /// days), and pays Rs. 300 in cash at the office (the oldest first: Rs. 700 of the opening stays unpaid). A second bill
    /// for D, paid in cash, goes by local delivery: picked by the owner, checked by a manager, packed and dispatched.
    /// </summary>
    public async Task InitializeAsync()
    {
        await Once.WaitAsync();
        try
        {
            if (shared is not null)
            {
                return;
            }

            var (business, managerName, managerPassword) = await ReportBusiness.EnsureAsync(factory);
            var store = await ReportBusiness.StoreAsync(factory, business, "AS1", "Accounts store");
            using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
            var root = $"/api/v1/businesses/{business}";
            var code = $"D{Guid.NewGuid():N}"[..10].ToUpperInvariant();
            var debtor = await Ok<DebtorDto>(owner.PostJsonAsync($"{root}/debtors", new CreateDebtorRequest(code, $"Kannan Stores {code}", null, null, "33",
                Address: "3 Car Street, Coimbatore", CreditPeriodDays: 10, CreditLimit: 10_000m, OpeningBalance: 1000m, OpeningBalanceDate: new DateOnly(2026, 9, 1))));

            var collector = await ReportBusiness.UserAsync(factory, "collection_person");
            var collectorId = (await collector.Client.GetJsonAsync<MeDto>("/api/v1/auth/me")).UserId;
            collector.Client.Dispose();
            var routeCode = $"R{Random.Shared.Next(1000, 9999)}";
            var route = await Ok<RouteDto>(owner.PostJsonAsync($"{root}/routes", new CreateRouteRequest(routeCode, "Car Street round")));
            await Ok<CollectionPlanDto>(owner.PutJsonAsync($"{root}/debtors/{debtor.Id}/collection-plan",
                new SetCollectionPlanRequest(route.Id, 1, collectorId, null, null, null, "MANUAL")));
            await Ok<PromiseDto>(owner.PostJsonAsync($"{root}/debtors/{debtor.Id}/promises", new RecordPromiseRequest(200m, new DateOnly(2026, 10, 5), "After the weekly market")));

            var (_, pack) = await Pos.StockedProductAsync(owner, business, store, price: 105m);
            var cashier = await ReportBusiness.UserAsync(factory, "manager");
            cashier.Client.Dispose();
            var session = await Pos.CounterBrowserAsync(factory, business, store, cashier.Username, cashier.Password);
            InvoiceDto delivered;
            using (session.Browser)
            {
                await Pos.IssueAsync(session.Browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)) with { DebtorId = debtor.Id }, 210m,
                    new PaymentRequest("ON_ACCOUNT", 210m, null)));
                delivered = await Pos.IssueAsync(session.Browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)) with { DebtorId = debtor.Id }, 105m) with
                {
                    Fulfilment = new FulfilmentRequest("LOCAL_DELIVERY", "3 Car Street, Coimbatore"),
                });
            }

            await Ok<DebtorReceiptDto>(owner.PostJsonAsync($"{root}/debtor-receipts",
                new DebtorReceiptRequest(debtor.Id, "CASH", 300m, store, IdempotencyKey: Guid.NewGuid().ToString("N"))));

            var challans = $"{root}/packing-challans/{delivered.Fulfilment!.ChallanId}";
            var challan = await owner.GetJsonAsync<ChallanDto>(challans);
            var all = challan.Lines.Select(l => new CountedLineRequest(l.Id, l.Quantity)).ToList();
            challan = await Ok<ChallanDto>(owner.PostJsonAsync($"{challans}/pick", new CountChallanRequest(all, challan.RowVersion)));
            using (var checker = await factory.LoginAsync(managerName, managerPassword))
            {
                challan = await Ok<ChallanDto>(checker.PostJsonAsync($"{challans}/check", new CountChallanRequest(all, challan.RowVersion)));
            }

            challan = await Ok<ChallanDto>(owner.PostJsonAsync($"{challans}/pack", new PackChallanRequest(all, 1, challan.RowVersion)));
            var trip = await Ok<ConsignmentDto>(owner.PostJsonAsync($"{root}/consignments",
                new RecordConsignmentRequest(Guid.NewGuid().ToString("N"), [delivered.Id], 1, new DateOnly(2026, 10, 1), DriverName: "Ravi")));
            shared = new Scenario(business, store, $"{code} - Kannan Stores {code}", $"{routeCode} - Car Street round", challan.Number, trip.Number);
        }
        finally
        {
            Once.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record MeDto(Guid UserId);

    private static Task<ReportDto> RunAsync(TestClient client, string key, string query = "") =>
        client.GetJsonAsync<ReportDto>($"{Base}/{key}?from={Day}&to={Day}{query}");

    private static decimal M(IReadOnlyDictionary<string, object?> row, string key) =>
        row[key] is JsonElement e ? (e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : 0m) : Convert.ToDecimal(row[key], CultureInfo.InvariantCulture);

    private static string T(IReadOnlyDictionary<string, object?> row, string key) => row[key] is JsonElement e ? e.ToString() : row[key]?.ToString() ?? string.Empty;

    private static IReadOnlyDictionary<string, object?> Row(ReportDto report, string column, string value) => report.Rows.Single(r => T(r, column) == value);

    [Fact]
    public async Task Debtor_balances_and_credit_ageing_agree()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var debtors = await RunAsync(owner, "debtors");
        var d = Row(debtors, "debtor", S.Debtor);
        Assert.Equal((1000m, 210m, 300m, 0m, 0m, 910m), (M(d, "opening"), M(d, "sales"), M(d, "received"), M(d, "reversed"), M(d, "credit_notes"), M(d, "closing")));

        var ageing = Row(await RunAsync(owner, "credit-ageing"), "debtor", S.Debtor);
        Assert.Equal((210m, 700m, 0m, 0m, 910m, 10_000m), (M(ageing, "current"), M(ageing, "d30"), M(ageing, "d60"), M(ageing, "advance"), M(ageing, "balance"), M(ageing, "limit")));
        Assert.Equal(M(d, "closing"), M(ageing, "balance"));
        Assert.Equal("report.business_only", await (await owner.GetAsync($"{Base}/debtors?from={Day}&to={Day}&storeId={S.Store}")).ProblemCodeAsync());
    }

    [Fact]
    public async Task Collections_routes_collectors_and_promises_count_the_same_receipt()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var byMethod = await RunAsync(owner, "collections", $"&storeId={S.Store}&by=method");
        Assert.Equal((1m, 300m, 300m), (M(Row(byMethod, "group", "Cash"), "receipts"), M(Row(byMethod, "group", "Cash"), "received"), M(Row(byMethod, "group", "Cash"), "net")));
        var byRoute = await RunAsync(owner, "collections", $"&storeId={S.Store}&by=route");
        Assert.Equal(300m, M(Row(byRoute, "group", S.Route), "net"));
        Assert.Equal(M(byMethod.Totals!, "net"), M((await RunAsync(owner, "collections", $"&storeId={S.Store}")).Totals!, "net"));

        var route = Row(await RunAsync(owner, "routes"), "route", S.Route);
        Assert.Equal((1m, 910m, 700m, 300m, 32.97m), (M(route, "parties"), M(route, "balance"), M(route, "overdue"), M(route, "collected"), M(route, "collected_pct")));

        var collectors = await RunAsync(owner, "collectors");
        Assert.Contains(collectors.Rows, r => T(r, "collector") == "Test collection_person" && M(r, "parties") >= 1);

        var promise = Row(await owner.GetJsonAsync<ReportDto>($"{Base}/promises?from={Day}&to=2026-10-31"), "debtor", S.Debtor);
        Assert.Equal((200m, 300m, "Kept", "After the weekly market"), (M(promise, "amount"), M(promise, "paid"), T(promise, "status"), T(promise, "note")));
    }

    [Fact]
    public async Task Dispatch_packing_and_audit_reports_show_the_delivery()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var register = await RunAsync(owner, "dispatches", $"&storeId={S.Store}");
        var trip = Row(register, "number", S.Dispatch);
        Assert.Equal(("Local delivery", 1m, 105m, "On the way"), (T(trip, "mode"), M(trip, "packages"), M(trip, "value"), T(trip, "delivery")));
        var byService = await RunAsync(owner, "dispatches", $"&storeId={S.Store}&by=transporter");
        Assert.Equal((1m, 105m), (M(Row(byService, "group", "Local delivery"), "dispatches"), M(Row(byService, "group", "Local delivery"), "value")));

        var challan = Row(await RunAsync(owner, "packing", $"&storeId={S.Store}"), "number", S.Challan);
        Assert.Equal(("Dispatched", "Test manager", 1m, 0m), (T(challan, "progress"), T(challan, "checker"), M(challan, "packages"), M(challan, "difference")));
        Assert.NotEqual(T(challan, "picker"), T(challan, "checker"));

        var events = await RunAsync(owner, "audit-events", "&by=event");
        Assert.True(M(Row(events, "group", "dispatch.recorded"), "events") >= 1);
        var detail = await RunAsync(owner, "audit-events", "&by=detail");
        Assert.Contains(detail.Rows, r => T(r, "event") == "dispatch.recorded" && T(r, "details").Contains(S.Dispatch, StringComparison.Ordinal));
    }
}
