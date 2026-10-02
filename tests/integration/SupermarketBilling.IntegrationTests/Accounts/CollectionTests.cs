using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Accounts;

[Collection(ApiTestGroup.Name)]
public sealed class CollectionTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    private DateOnly Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).DateTime);

    private static async Task<T> SendAsync<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    private async Task<(TestClient Client, Guid UserId)> CollectorAsync()
    {
        var user = await factory.CreateSignedInUserAsync("collection_person", Store);
        return (user.Client, user.UserId);
    }

    private Task<CollectionPlanDto> PlanAsync(TestClient owner, Guid debtorId, SetCollectionPlanRequest plan) =>
        SendAsync<CollectionPlanDto>(owner.PutJsonAsync($"/api/v1/businesses/{Business}/debtors/{debtorId}/collection-plan", plan));

    [Fact]
    public async Task A_collector_sees_the_parties_due_today_in_route_order_with_why_and_what_they_owe()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (collectorClient, collector) = await CollectorAsync();
        using var me = collectorClient;
        var code = $"R{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var route = await SendAsync<RouteDto>(owner.PostJsonAsync($"/api/v1/businesses/{Business}/routes", new CreateRouteRequest(code, $"Market road {code}")));

        // Weekly on today's weekday, second on the route, owing Rs. 1,000 since 1 September.
        var weekly = await Ledgers.DebtorAsync(owner, Business, opening: 1000m);
        var plan = await PlanAsync(owner, weekly.Id, new SetCollectionPlanRequest(route.Id, 2, collector, null, new TimeOnly(10, 0), new TimeOnly(12, 0), "WEEKDAYS",
            [Today.DayOfWeek.ToString().ToUpperInvariant()]));
        Assert.StartsWith("Every ", plan.Description, StringComparison.Ordinal);

        // First on the route, visited when an invoice falls due: the opening balance is due today.
        var dueToday = await Ledgers.DebtorAsync(owner, Business);
        (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/debtors/{dueToday.Id}/opening-balance",
            new OpeningBalanceRequest(300m, Today.AddDays(-10), Today))).EnsureSuccessStatusCode();
        await PlanAsync(owner, dueToday.Id, new SetCollectionPlanRequest(route.Id, 1, collector, null, null, null, "DUE_DATE", DueOffsetDays: 0));

        // Not scheduled today, but with an assigned visit; and another with a promise falling due today.
        var assigned = await Ledgers.DebtorAsync(owner, Business);
        await PlanAsync(owner, assigned.Id, new SetCollectionPlanRequest(null, null, collector, null, null, null, "MANUAL"));
        await SendAsync<VisitDto>(owner.PostJsonAsync($"/api/v1/businesses/{Business}/collection-visits", new AssignVisitRequest(assigned.Id, collector, Today, "Collect the cheque")));
        var promised = await Ledgers.DebtorAsync(owner, Business, opening: 500m);
        await PlanAsync(owner, promised.Id, new SetCollectionPlanRequest(null, null, collector, null, null, null, "MONTHLY", MonthDay: Today.AddDays(5).Day));
        await SendAsync<PromiseDto>(owner.PostJsonAsync($"/api/v1/businesses/{Business}/debtors/{promised.Id}/promises", new RecordPromiseRequest(500m, Today)));
        var notToday = await Ledgers.DebtorAsync(owner, Business, opening: 200m);
        await PlanAsync(owner, notToday.Id, new SetCollectionPlanRequest(route.Id, 3, collector, null, null, null, "MONTHLY", MonthDay: Today.AddDays(5).Day));

        var day = await me.GetJsonAsync<DayListDto>($"/api/v1/businesses/{Business}/collections/day");
        // On the route in sequence first, then the parties without a route (by name).
        Assert.Equal([dueToday.Id, weekly.Id], day.Parties.Take(2).Select(p => p.DebtorId));
        Assert.Equal(new[] { assigned.Id, promised.Id }.Order(), day.Parties.Skip(2).Select(p => p.DebtorId).Order());
        var first = day.Parties[0];
        Assert.Equal(["DUE_DATE"], first.Reasons);
        Assert.Equal((300m, 0m, 0), (first.DueBalance, first.OverdueBalance, first.DaysOverdue));
        var second = day.Parties[1];
        Assert.Equal(["SCHEDULE"], second.Reasons);
        Assert.Equal((1000m, 1000m, "Opening balance"), (second.TotalBalance, second.OverdueBalance, second.OldestUnpaidDocument));
        Assert.Equal((Today.DayNumber - new DateOnly(2026, 9, 1).DayNumber, (TimeOnly?)new TimeOnly(10, 0)), (second.DaysOverdue, second.PreferredFrom));
        var visitParty = day.Parties.Single(p => p.DebtorId == assigned.Id);
        Assert.Equal(["ASSIGNED"], visitParty.Reasons);
        Assert.Equal("Collect the cheque", Assert.Single(visitParty.Notes));
        var promiseParty = day.Parties.Single(p => p.DebtorId == promised.Id);
        Assert.Equal(["PROMISE"], promiseParty.Reasons);
        Assert.Equal(500m, promiseParty.PromisedAmount);
        Assert.DoesNotContain(day.Parties, p => p.DebtorId == notToday.Id);
        Assert.Contains((await me.GetJsonAsync<DayListDto>($"/api/v1/businesses/{Business}/collections/day?includeOverdue=true")).Parties,
            p => p.DebtorId == notToday.Id && p.Reasons.SequenceEqual(["OVERDUE"]));

        // The promise is kept when the money comes in; the party shows as collected today.
        await SendAsync<DebtorReceiptDto>(owner.PostJsonAsync($"/api/v1/businesses/{Business}/debtor-receipts",
            new DebtorReceiptRequest(promised.Id, "UPI", 500m, Store, IdempotencyKey: Guid.NewGuid().ToString("N"))));
        Assert.Equal("KEPT", (await owner.GetJsonAsync<List<PromiseDto>>($"/api/v1/businesses/{Business}/debtors/{promised.Id}/promises"))[0].Status);
        var after = (await me.GetJsonAsync<DayListDto>($"/api/v1/businesses/{Business}/collections/day")).Parties.Single(p => p.DebtorId == promised.Id);
        Assert.Equal(("COLLECTED", 500m, (decimal?)500m), (after.Status, after.CollectedToday, after.LastCollectionAmount));
    }

    [Fact]
    public async Task An_absent_collectors_parties_go_to_the_backup_and_collectors_see_only_their_own_day()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (primaryClient, primary) = await CollectorAsync();
        var (backupClient, backup) = await CollectorAsync();
        using var primaryUser = primaryClient;
        using var backupUser = backupClient;
        var debtor = await Ledgers.DebtorAsync(owner, Business, opening: 100m);
        await PlanAsync(owner, debtor.Id, new SetCollectionPlanRequest(null, null, primary, backup, null, null, "WEEKDAYS", [Today.DayOfWeek.ToString()]));

        Assert.Contains((await primaryUser.GetJsonAsync<DayListDto>($"/api/v1/businesses/{Business}/collections/day")).Parties, p => p.DebtorId == debtor.Id);
        Assert.DoesNotContain((await backupUser.GetJsonAsync<DayListDto>($"/api/v1/businesses/{Business}/collections/day")).Parties, p => p.DebtorId == debtor.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await backupUser.GetAsync($"/api/v1/businesses/{Business}/collections/day?collectorUserId={primary}")).StatusCode);

        await SendAsync<AbsenceDto>(owner.PostJsonAsync($"/api/v1/businesses/{Business}/collector-absences", new RecordAbsenceRequest(primary, Today, "Sick")));
        var away = await primaryUser.GetJsonAsync<DayListDto>($"/api/v1/businesses/{Business}/collections/day");
        Assert.True(away.Absent);
        Assert.DoesNotContain(away.Parties, p => p.DebtorId == debtor.Id);
        var covered = (await owner.GetJsonAsync<DayListDto>($"/api/v1/businesses/{Business}/collections/day?collectorUserId={backup}")).Parties.Single(p => p.DebtorId == debtor.Id);
        Assert.Equal(["SCHEDULE", "BACKUP"], covered.Reasons);
    }

    [Fact]
    public async Task Plans_name_real_collectors_and_visits_and_promises_can_only_be_cancelled()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (collectorClient, collector) = await CollectorAsync();
        collectorClient.Dispose();
        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        using var cashierClient = cashier.Client;
        var debtor = await Ledgers.DebtorAsync(owner, Business);

        Assert.Contains(await owner.GetJsonAsync<List<CollectorDto>>($"/api/v1/businesses/{Business}/collectors"), c => c.UserId == collector);
        Assert.Equal("plan.not_a_collector", await (await owner.PutJsonAsync($"/api/v1/businesses/{Business}/debtors/{debtor.Id}/collection-plan",
            new SetCollectionPlanRequest(null, null, cashier.UserId, null, null, null, "MANUAL"))).ProblemCodeAsync());
        Assert.Equal("plan.weekday_invalid", await (await owner.PutJsonAsync($"/api/v1/businesses/{Business}/debtors/{debtor.Id}/collection-plan",
            new SetCollectionPlanRequest(null, null, collector, null, null, null, "WEEKDAYS", ["FUNDAY"]))).ProblemCodeAsync());
        Assert.Equal("visit.date_past", await (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/collection-visits",
            new AssignVisitRequest(debtor.Id, collector, Today.AddDays(-1)))).ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await cashierClient.GetAsync($"/api/v1/businesses/{Business}/routes")).StatusCode);

        var visit = await SendAsync<VisitDto>(owner.PostJsonAsync($"/api/v1/businesses/{Business}/collection-visits", new AssignVisitRequest(debtor.Id, collector, Today)));
        (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/collection-visits/{visit.Id}/cancel", new { })).EnsureSuccessStatusCode();
        Assert.True((await owner.GetJsonAsync<List<VisitDto>>($"/api/v1/businesses/{Business}/collection-visits?date={Today:yyyy-MM-dd}")).Single(v => v.Id == visit.Id).IsCancelled);

        await using var db = await factory.OpenAppConnectionAsync();
        foreach (var sql in new[] { "UPDATE collection_visits SET is_cancelled = false WHERE id = @id", "DELETE FROM collection_visits WHERE id = @id",
                     "UPDATE collection_visits SET note = 'changed' WHERE id = @id" })
        {
            await using var command = new NpgsqlCommand(sql, db);
            command.Parameters.AddWithValue("id", visit.Id);
            Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
    }
}
