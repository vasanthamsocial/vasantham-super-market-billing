using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Dispatch;

[Collection(ApiTestGroup.Name)]
public sealed class PackingTests(ApiFactory factory)
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    private string Base => $"/api/v1/businesses/{Business}";

    private string Challans => $"{Base}/packing-challans";

    private static string Code(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    private static FulfilmentRequest Delivery() => new("LOCAL_DELIVERY", "4 Big Bazaar Street, Coimbatore", "9876543210");

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    private Task<ChallanDto> Step(TestClient client, ChallanDto challan, string step, object request) => Ok<ChallanDto>(client.PostJsonAsync($"{Challans}/{challan.Id}/{step}", request));

    private static CountChallanRequest Count(ChallanDto challan, params (int Line, decimal Quantity, string? Reason)[] counts) =>
        new(challan.Lines.Select(l => counts.FirstOrDefault(c => c.Line == l.LineNumber) is { Line: > 0 } c
            ? new CountedLineRequest(l.Id, c.Quantity, c.Reason)
            : new CountedLineRequest(l.Id, l.Picked ?? l.Quantity)).ToList(), challan.RowVersion);

    private static RecordConsignmentRequest Trip(Guid invoiceId, params ChallanQuantityRequest[] lines) =>
        new(Guid.NewGuid().ToString("N"), [invoiceId], 1, Today, DriverName: "Ravi", Lines: lines.Length == 0 ? null : lines);

    /// <summary>A bill of 10 of one item and 4 of another, for local delivery, from a fresh counter.</summary>
    private async Task<(InvoiceDto Bill, ChallanDto Challan, Pos.CounterSession Counter)> BillAsync(TestClient owner)
    {
        var (_, rice) = await Pos.StockedProductAsync(owner, Business, Store, price: 60m);
        var (_, oil) = await Pos.StockedProductAsync(owner, Business, Store, price: 150m);
        var counter = await Pos.CounterBrowserAsync(factory, Business, Store);
        var bill = await Pos.IssueAsync(counter.Browser,
            Pos.Issue(Pos.Cart(new CartLineRequest(rice, 10), new CartLineRequest(oil, 4)) with { Buyer = new BuyerRequest("Lakshmi Stores", null, null, null, null) }, 1200m)
                with { Fulfilment = Delivery() });
        var challan = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{bill.Fulfilment!.ChallanId}");
        return (bill, challan, counter);
    }

    [Fact]
    public async Task Goods_are_picked_short_checked_by_another_packed_and_sent_in_parts_and_the_bill_never_changes()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (bill, challan, counter) = await BillAsync(owner);
        counter.Browser.Dispose();
        var checker = await factory.CreateSignedInUserAsync("manager", businessId: Business);
        using var check = checker.Client;

        // The challan has the bill's goods and no money at all.
        Assert.Matches(@"^MAIN/PCH/\d{6}$", challan.Number);
        Assert.Equal((bill.Number, "Lakshmi Stores", "LOCAL_DELIVERY", "TO_PICK", "4 Big Bazaar Street, Coimbatore"),
            (challan.InvoiceNumber, challan.PartyName, challan.Mode, challan.Progress, challan.DeliveryAddress));
        Assert.Equal([10m, 4m], challan.Lines.Select(l => l.Quantity));
        Assert.DoesNotContain(typeof(ChallanLineDto).GetProperties(), p => p.Name.Contains("Price", StringComparison.Ordinal) || p.Name.Contains("Cost", StringComparison.Ordinal));
        var pdf = await owner.Http.GetAsync(new Uri($"{Challans}/{challan.Id}/pdf", UriKind.Relative));
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);

        // Only 9 of the 10 are on the shelf: a short pick needs the reason.
        Assert.Equal("challan.short_reason_required", await (await owner.PostJsonAsync($"{Challans}/{challan.Id}/pick", Count(challan, (1, 9m, null)))).ProblemCodeAsync());
        Assert.Equal("challan.not_picked", await (await check.PostJsonAsync($"{Challans}/{challan.Id}/check", Count(challan))).ProblemCodeAsync());
        challan = await Step(owner, challan, "pick", Count(challan, (1, 9m, "Only 9 on the shelf")));
        Assert.Equal("TO_CHECK", challan.Progress);
        Assert.NotNull(challan.Picker);

        // The picker cannot check their own picking.
        Assert.Equal("challan.self_check", await (await owner.PostJsonAsync($"{Challans}/{challan.Id}/check", Count(challan))).ProblemCodeAsync());
        challan = await Step(check, challan, "check", Count(challan));
        Assert.Equal("TO_PACK", challan.Progress);
        Assert.NotEqual(challan.Picker, challan.Checker);

        // Packed in two goes: first the rice in 2 bags, which goes out alone.
        var rice = challan.Lines[0];
        var oil = challan.Lines[1];
        Assert.Equal("challan.pack_too_much", await (await owner.PostJsonAsync($"{Challans}/{challan.Id}/pack",
            new PackChallanRequest([new CountedLineRequest(rice.Id, 10m)], 1, challan.RowVersion))).ProblemCodeAsync());
        challan = await Step(owner, challan, "pack", new PackChallanRequest([new CountedLineRequest(rice.Id, 9m)], 2, challan.RowVersion));
        Assert.Equal(("PARTLY_PACKED", 2, 9m), (challan.Progress, challan.PackageCount, challan.Lines[0].ReadyToSend));
        Assert.Equal("consignment.not_ready", await (await owner.PostJsonAsync($"{Base}/consignments",
            Trip(bill.Id, new ChallanQuantityRequest(oil.Id, 1m)))).ProblemCodeAsync());
        var first = await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments", Trip(bill.Id)));
        var sent = Assert.Single(first.Lines!);
        Assert.Equal((rice.Id, 9m, 540m), (sent.ChallanLineId, sent.Quantity, first.GoodsValue)); // 9 of 10 at Rs. 60
        challan = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{challan.Id}");
        Assert.Equal(("PARTLY_DISPATCHED", 9m, 0m), (challan.Progress, challan.Lines[0].InTransit, challan.Lines[0].ReadyToSend));

        // Then the oil; it goes in a second dispatch.
        challan = await Step(owner, challan, "pack", new PackChallanRequest([new CountedLineRequest(oil.Id, 4m)], 1, challan.RowVersion));
        var labels = await owner.Http.GetAsync(new Uri($"{Challans}/{challan.Id}/labels", UriKind.Relative));
        Assert.Equal((HttpStatusCode.OK, 3), (labels.StatusCode, challan.PackageCount));
        var second = await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments", Trip(bill.Id)));
        Assert.Equal(oil.Id, Assert.Single(second.Lines!).ChallanLineId);
        Assert.Equal("consignment.nothing_packed", await (await owner.PostJsonAsync($"{Base}/consignments", Trip(bill.Id))).ProblemCodeAsync());

        // The rice arrived; the oil did not (shop closed), and came back; it goes again and arrives.
        Assert.Equal("delivery.lines_incomplete", await (await owner.PostJsonAsync($"{Base}/consignments/{first.Id}/delivery",
            new ReportDeliveryRequest(Today, [], null, first.RowVersion))).ProblemCodeAsync());
        await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments/{first.Id}/delivery",
            new ReportDeliveryRequest(Today, [new ChallanQuantityRequest(rice.Id, 9m)], "Received by Lakshmi", first.RowVersion)));
        Assert.Equal("delivery.reason_required", await (await owner.PostJsonAsync($"{Base}/consignments/{second.Id}/delivery",
            new ReportDeliveryRequest(Today, [new ChallanQuantityRequest(oil.Id, 0m)], null, second.RowVersion))).ProblemCodeAsync());
        var failed = await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments/{second.Id}/delivery",
            new ReportDeliveryRequest(Today, [new ChallanQuantityRequest(oil.Id, 0m)], "Shop closed", second.RowVersion)));
        Assert.Equal("FAILED", failed.DeliveryOutcome);
        Assert.Equal("consignment.reported", await (await owner.PostJsonAsync($"{Base}/consignments/{second.Id}/cancel",
            new CancelConsignmentRequest("Too late", failed.RowVersion))).ProblemCodeAsync());
        challan = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{challan.Id}");
        Assert.Equal((4m, 0m), (challan.Lines[1].Lost, challan.Lines[1].ReadyToSend)); // not back yet: lost for now

        var back = await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments/{second.Id}/return",
            new RecordReturnRequest([new ChallanQuantityRequest(oil.Id, 4m)], failed.RowVersion)));
        Assert.True(back.ReturnRecorded);
        challan = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{challan.Id}");
        Assert.Equal((0m, 4m, 4m), (challan.Lines[1].Lost, challan.Lines[1].Returned, challan.Lines[1].ReadyToSend));
        var again = await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments", Trip(bill.Id)));
        await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments/{again.Id}/delivery",
            new ReportDeliveryRequest(Today, [new ChallanQuantityRequest(oil.Id, 4m)], null, again.RowVersion)));

        // Everything that was sent arrived; the one bag of rice never picked is the difference to settle, and the bill is as billed.
        challan = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{challan.Id}");
        Assert.Equal(("DELIVERED", 1m, 0m), (challan.Progress, challan.Lines[0].Difference, challan.Lines[1].Difference));
        Assert.Equal(["CREATED", "PICKED", "CHECKED", "PACKED", "DISPATCHED", "PACKED", "DISPATCHED", "DELIVERED", "FAILED", "RETURNED", "DISPATCHED", "DELIVERED"],
            challan.Events.Select(e => e.Kind));
        var unchanged = await owner.GetJsonAsync<InvoiceDto>($"{Base}/sales/invoices/{bill.Id}");
        Assert.Equal((1200m, 10m), (unchanged.GrandTotal, unchanged.Lines[0].Quantity));
        Assert.Contains(await owner.GetJsonAsync<List<ChallanSummaryDto>>(Challans), c => c.Id == challan.Id && c.HasDifference);
    }

    [Fact]
    public async Task A_credit_note_settles_the_short_and_credited_goods_are_never_sent()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (bill, challan, counter) = await BillAsync(owner);
        var checker = await factory.CreateSignedInUserAsync("manager", businessId: Business);
        using (checker.Client)
        {
            challan = await Step(owner, challan, "pick", Count(challan, (2, 3m, "Last tin dented")));
            challan = await Step(checker.Client, challan, "check", Count(challan));
        }

        challan = await Step(owner, challan, "pack", new PackChallanRequest(challan.Lines.Select(l => new CountedLineRequest(l.Id, l.Checked!.Value)).ToList(), 1, challan.RowVersion));
        Assert.Equal(1m, challan.Lines[1].Difference);

        // The customer is not charged for the tin that is not coming, and also gives back 2 of the 10 rice before dispatch.
        using (counter.Browser)
        {
            var found = await Returns.FindAsync(counter.Browser, bill.Number);
            await Returns.IssueAsync(counter.Browser, Returns.Request(bill.Id, 270m,
                [new ReturnLineRequest(found.Lines[0].OriginalLineId, 2, Restock: false), new ReturnLineRequest(found.Lines[1].OriginalLineId, 1, Restock: false)]));
        }

        challan = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{challan.Id}");
        Assert.Equal((8m, 3m), (challan.Lines[0].ReadyToSend, challan.Lines[1].ReadyToSend));
        Assert.Equal((0m, 0m), (challan.Lines[0].Difference, challan.Lines[1].Difference));
        Assert.Equal("consignment.not_ready", await (await owner.PostJsonAsync($"{Base}/consignments",
            Trip(bill.Id, new ChallanQuantityRequest(challan.Lines[0].Id, 10m)))).ProblemCodeAsync());
        var trip = await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments", Trip(bill.Id)));
        Assert.Equal([8m, 3m], trip.Lines!.Select(l => l.Quantity));
        Assert.Equal(930m, trip.GoodsValue); // 8 x 60 + 3 x 150
    }

    [Fact]
    public async Task A_bill_changed_to_pickup_cancels_its_challan_and_back_to_delivery_starts_a_new_one()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (bill, challan, counter) = await BillAsync(owner);
        counter.Browser.Dispose();
        var fulfilment = await owner.GetJsonAsync<FulfilmentDto>($"{Base}/invoices/{bill.Id}/fulfilment");
        fulfilment = await Ok<FulfilmentDto>(owner.PutJsonAsync($"{Base}/invoices/{bill.Id}/fulfilment", new FulfilmentRequest("PICKUP", RowVersion: fulfilment.RowVersion)));
        Assert.Null(fulfilment.ChallanId);
        var cancelled = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{challan.Id}");
        Assert.Equal(("CANCELLED", "Changed to pickup"), (cancelled.Progress, cancelled.CancelReason));
        Assert.Equal("challan.cancelled", await (await owner.PostJsonAsync($"{Challans}/{challan.Id}/pick", Count(cancelled))).ProblemCodeAsync());

        fulfilment = await Ok<FulfilmentDto>(owner.PutJsonAsync($"{Base}/invoices/{bill.Id}/fulfilment", Delivery() with { RowVersion = fulfilment.RowVersion }));
        Assert.NotEqual(challan.Id, fulfilment.ChallanId);
        Assert.Equal("TO_PICK", (await owner.GetJsonAsync<ChallanDto>($"{Challans}/{fulfilment.ChallanId}")).Progress);
    }

    [Fact]
    public async Task Only_dispatch_staff_pack_and_a_stale_screen_is_refused()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, challan, counter) = await BillAsync(owner);
        counter.Browser.Dispose();
        var cashier = await factory.CreateSignedInUserAsync("cashier", businessId: Business);
        using (cashier.Client)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Client.GetAsync($"{Challans}/{challan.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Client.PostJsonAsync($"{Challans}/{challan.Id}/pick", Count(challan))).StatusCode);
        }

        var store = await factory.CreateSignedInUserAsync("inventory_operator", businessId: Business);
        using (store.Client)
        {
            await Step(store.Client, challan, "pick", Count(challan));
        }

        Assert.Equal("concurrency.conflict", await (await owner.PostJsonAsync($"{Challans}/{challan.Id}/pick", Count(challan))).ProblemCodeAsync());
        Assert.Equal("challan.lines_incomplete", await (await owner.PostJsonAsync($"{Challans}/{challan.Id}/check",
            new CountChallanRequest([], (await owner.GetJsonAsync<ChallanDto>($"{Challans}/{challan.Id}")).RowVersion))).ProblemCodeAsync());
    }

    [Fact]
    public async Task Several_screens_sending_the_same_packed_goods_at_once_send_no_more_than_was_packed()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => owner.GetAsync($"{Base}/dispatch/queue"))); // warm up
        var (bill, challan, counter) = await BillAsync(owner);
        counter.Browser.Dispose();
        var checker = await factory.CreateSignedInUserAsync("manager", businessId: Business);
        using (checker.Client)
        {
            challan = await Step(owner, challan, "pick", Count(challan));
            challan = await Step(checker.Client, challan, "check", Count(challan));
        }

        var rice = challan.Lines[0].Id;
        challan = await Step(owner, challan, "pack", new PackChallanRequest([new CountedLineRequest(rice, 3m)], 1, challan.RowVersion));
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => owner.PostJsonAsync($"{Base}/consignments", Trip(bill.Id, new ChallanQuantityRequest(rice, 1m))))));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        challan = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{challan.Id}");
        Assert.Equal((3m, 0m), (challan.Lines[0].InTransit, challan.Lines[0].ReadyToSend));
    }

    [Fact]
    public async Task The_database_keeps_what_was_picked_checked_packed_and_delivered()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (bill, challan, counter) = await BillAsync(owner);
        counter.Browser.Dispose();
        var checker = await factory.CreateSignedInUserAsync("manager", businessId: Business);
        using (checker.Client)
        {
            challan = await Step(owner, challan, "pick", Count(challan));
            challan = await Step(checker.Client, challan, "check", Count(challan));
        }

        challan = await Step(owner, challan, "pack", new PackChallanRequest(challan.Lines.Select(l => new CountedLineRequest(l.Id, l.Quantity)).ToList(), 2, challan.RowVersion));
        var trip = await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments", Trip(bill.Id)));
        await Ok<ConsignmentDto>(owner.PostJsonAsync($"{Base}/consignments/{trip.Id}/delivery",
            new ReportDeliveryRequest(Today, trip.Lines!.Select(l => new ChallanQuantityRequest(l.ChallanLineId, l.Quantity)).ToList(), null, trip.RowVersion)));

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[]
                     {
                         "UPDATE packing_challan_lines SET picked_quantity = picked_quantity - 1, short_reason = 'x' WHERE challan_id = @challan",
                         "UPDATE packing_challan_lines SET packed_quantity = 0 WHERE challan_id = @challan",
                         "UPDATE packing_challans SET checked_by_user_id = picked_by_user_id WHERE id = @challan",
                         "UPDATE packing_challans SET status = 'CANCELLED', cancel_reason = 'x' WHERE id = @challan",
                         "DELETE FROM packing_challans WHERE id = @challan",
                         "DELETE FROM packing_events WHERE challan_id = @challan",
                         "UPDATE consignment_lines SET delivered_quantity = 0 WHERE consignment_id = @trip",
                         "UPDATE consignments SET delivery_outcome = 'FAILED', delivery_note = 'x' WHERE id = @trip",
                         "UPDATE consignments SET status = 'CANCELLED', cancel_reason = 'x', cancelled_by_user_id = created_by_user_id, cancelled_at_utc = now() WHERE id = @trip",
                     })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("challan", challan.Id);
                command.Parameters.AddWithValue("trip", trip.Id);
                var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Contains(refused.SqlState, new[] { PostgresErrorCodes.RestrictViolation, PostgresErrorCodes.CheckViolation });
            }
        }

        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        foreach (var file in new[] { "014_dispatch.sql", "015_packing.sql" })
        {
            await using var verify = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", file)), admin);
            await verify.ExecuteNonQueryAsync();
        }
    }
}
