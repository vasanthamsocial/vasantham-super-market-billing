using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Dispatch;

[Collection(ApiTestGroup.Name)]
public sealed class DispatchTests(ApiFactory factory)
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    private string Base => $"/api/v1/businesses/{Business}";

    private static string Code(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    private static string Lr() => "LR" + Random.Shared.Next(100_000, 999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A lorry service with a booking office near the store and two destination branches, the first 2 days away.</summary>
    private async Task<(TransporterDto Transporter, Guid Booking, Guid Destination, Guid Other)> TransporterAsync(TestClient owner)
    {
        var created = await owner.PostJsonAsync($"{Base}/transporters", new CreateTransporterRequest(Code("T"), $"KPN Parcel {Guid.NewGuid():N}"[..20], Gstin("33AAACK1234C1Z"),
            "0422 2345678", "Gandhipuram, Coimbatore"));
        await created.EnsureSuccessWithBodyAsync();
        var transporter = (await created.Content.ReadFromJsonAsync<TransporterDto>(TestClient.Json))!;
        transporter = await BranchAsync(owner, transporter, new TransporterBranchRequest("Gandhipuram office", "Coimbatore", IsBookingOffice: true, IsDestination: false));
        transporter = await BranchAsync(owner, transporter, new TransporterBranchRequest("Madurai branch", "Madurai", Phone: "0452 2345678"));
        transporter = await BranchAsync(owner, transporter, new TransporterBranchRequest("Salem branch", "Salem"));
        var booking = transporter.Branches.Single(b => b.IsBookingOffice).Id;
        var madurai = transporter.Branches.Single(b => b.City == "Madurai").Id;
        var routed = await owner.PostJsonAsync($"{Base}/transporters/{transporter.Id}/routes", new CreateTransporterRouteRequest(booking, madurai, 2));
        await routed.EnsureSuccessWithBodyAsync();
        return ((await routed.Content.ReadFromJsonAsync<TransporterDto>(TestClient.Json))!, booking, madurai, transporter.Branches.Single(b => b.City == "Salem").Id);
    }

    private static string Gstin(string first14) => SupermarketBilling.Domain.Tax.Gstin.Complete(first14);

    private async Task<TransporterDto> BranchAsync(TestClient owner, TransporterDto transporter, TransporterBranchRequest request)
    {
        var response = await owner.PostJsonAsync($"{Base}/transporters/{transporter.Id}/branches", request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<TransporterDto>(TestClient.Json))!;
    }

    private async Task<DebtorDto> DebtorAsync(TestClient owner)
    {
        var code = Code("D");
        var response = await owner.PostJsonAsync($"{Base}/debtors", new CreateDebtorRequest(code, $"Lorry Party {code}", null, null, "33", Address: "12 East Masi Street, Madurai",
            CreditPeriodDays: 10, CreditLimit: 100_000m));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<DebtorDto>(TestClient.Json))!;
    }

    private static IssueInvoiceRequest Bill(Guid pack, decimal quantity, decimal total, Guid? debtorId, FulfilmentRequest? fulfilment) =>
        Pos.Issue(Pos.Cart(new CartLineRequest(pack, quantity)) with { DebtorId = debtorId }, total) with { Fulfilment = fulfilment };

    private static FulfilmentRequest ByLorry(Guid transporterId, Guid? destination = null) =>
        new("LORRY", "12 East Masi Street, Madurai", "9876543210", transporterId, destination, "Call before delivery");

    private static RecordConsignmentRequest Dispatch(Guid booking, string lr, params Guid[] invoices) =>
        new(Guid.NewGuid().ToString("N"), invoices, PackageCount: 3, DispatchDate: Today, BookingBranchId: booking, LrNumber: lr, LrDate: Today, WeightKg: 42.5m,
            FreightTerms: "TO_PAY", FreightAmount: 350m, VehicleNumber: "tn 38 ab 1234");

    private async Task<HttpResponseMessage> RecordAsync(TestClient client, RecordConsignmentRequest request) => await client.PostJsonAsync($"{Base}/consignments", request);

    private async Task<List<DispatchQueueItemDto>> QueueAsync(TestClient client) => await client.GetJsonAsync<List<DispatchQueueItemDto>>($"{Base}/dispatch/queue");

    private string Challans => $"{Base}/packing-challans";

    private static async Task<ChallanDto> PostChallanAsync(TestClient client, string path, object request)
    {
        var response = await client.PostJsonAsync(path, request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<ChallanDto>(TestClient.Json))!;
    }

    /// <summary>The owner picks everything, a manager checks it, the owner packs it all in one package: ready to dispatch.</summary>
    internal async Task<ChallanDto> PackAllAsync(TestClient owner, params InvoiceDto[] bills)
    {
        var checker = await factory.CreateSignedInUserAsync("manager", businessId: Business);
        using (checker.Client)
        {
            ChallanDto challan = null!;
            foreach (var bill in bills)
            {
                challan = await owner.GetJsonAsync<ChallanDto>($"{Challans}/{bill.Fulfilment!.ChallanId}");
                var all = challan.Lines.Select(l => new CountedLineRequest(l.Id, l.Quantity)).ToList();
                challan = await PostChallanAsync(owner, $"{Challans}/{challan.Id}/pick", new CountChallanRequest(all, challan.RowVersion));
                challan = await PostChallanAsync(checker.Client, $"{Challans}/{challan.Id}/check", new CountChallanRequest(all, challan.RowVersion));
                challan = await PostChallanAsync(owner, $"{Challans}/{challan.Id}/pack", new PackChallanRequest(all, 1, challan.RowVersion));
            }

            return challan;
        }
    }

    [Fact]
    public async Task The_lorry_service_list_keeps_offices_branches_and_routes_and_only_dispatch_staff_change_it()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        Assert.Equal("gstin.invalid", await (await owner.PostJsonAsync($"{Base}/transporters", new CreateTransporterRequest(Code("T"), "Bad GST", "33AAACK1234C1Z9")))
            .ProblemCodeAsync());
        var (transporter, booking, madurai, salem) = await TransporterAsync(owner);
        Assert.Equal(3, transporter.Branches.Count);
        var route = Assert.Single(transporter.Routes);
        Assert.Equal(("Gandhipuram office, Coimbatore", "Madurai branch, Madurai", 2), (route.FromBranch, route.ToBranch, route.TransitDays));

        Assert.Equal("transporter.code_taken", await (await owner.PostJsonAsync($"{Base}/transporters", new CreateTransporterRequest(transporter.Code, "Again"))).ProblemCodeAsync());
        Assert.Equal("route.branches_invalid", await (await owner.PostJsonAsync($"{Base}/transporters/{transporter.Id}/routes",
            new CreateTransporterRouteRequest(madurai, salem, 1))).ProblemCodeAsync());
        Assert.Equal("route.exists", await (await owner.PostJsonAsync($"{Base}/transporters/{transporter.Id}/routes",
            new CreateTransporterRouteRequest(booking, madurai, 3))).ProblemCodeAsync());
        Assert.Equal("branch.role_required", await (await owner.PostJsonAsync($"{Base}/transporters/{transporter.Id}/branches",
            new TransporterBranchRequest("Nowhere", "Erode", IsBookingOffice: false, IsDestination: false))).ProblemCodeAsync());
        // A route's booking office cannot stop being one.
        var office = transporter.Branches.Single(b => b.Id == booking);
        Assert.Equal("branch.in_use", await (await owner.PutJsonAsync($"{Base}/transporters/{transporter.Id}/branches/{booking}",
            new TransporterBranchRequest(office.Name, office.City, IsBookingOffice: false, IsDestination: true, RowVersion: office.RowVersion))).ProblemCodeAsync());

        var cashier = await factory.CreateSignedInUserAsync("cashier", businessId: Business);
        using (cashier.Client)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Client.GetAsync($"{Base}/transporters")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Client.PostJsonAsync($"{Base}/transporters", new CreateTransporterRequest(Code("T"), "Cashier's"))).StatusCode);
        }

        var stores = await factory.CreateSignedInUserAsync("inventory_operator", businessId: Business);
        using (stores.Client)
        {
            (await stores.Client.PostJsonAsync($"{Base}/transporters", new CreateTransporterRequest(Code("T"), "Store team's lorry"))).EnsureSuccessStatusCode();
        }

        var auditor = await factory.CreateSignedInUserAsync("auditor", businessId: Business);
        using (auditor.Client)
        {
            Assert.Contains(await auditor.Client.GetJsonAsync<List<TransporterDto>>($"{Base}/transporters"), t => t.Id == transporter.Id);
            Assert.Equal(HttpStatusCode.Forbidden, (await auditor.Client.PostJsonAsync($"{Base}/transporters", new CreateTransporterRequest(Code("T"), "Auditor's"))).StatusCode);
        }
    }

    [Fact]
    public async Task A_bill_sent_by_lorry_waits_for_dispatch_and_its_lr_shows_the_booking_freight_and_expected_delivery()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (transporter, booking, madurai, _) = await TransporterAsync(owner);
        var debtor = await DebtorAsync(owner);
        (await owner.PutJsonAsync($"{Base}/debtors/{debtor.Id}/delivery", new DeliveryPreferenceRequest("LORRY", transporter.Id, madurai))).EnsureSuccessStatusCode();
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);

        var cashier = await factory.CreateSignedInUserAsync("cashier", businessId: Business);
        cashier.Client.Dispose();
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store, cashier.Username, cashier.Password);
        InvoiceDto first, second;
        using (browser)
        {
            // The counter offers the lorry services and fills in the customer's usual way.
            var options = await browser.GetJsonAsync<CounterDeliveryOptionsDto>($"/api/v1/pos/delivery-options?debtorId={debtor.Id}");
            Assert.Contains(options.Transporters, t => t.Id == transporter.Id && t.Destinations.Any(d => d.Id == madurai) && t.Destinations.All(d => d.Id != booking));
            Assert.Equal(("LORRY", transporter.Id, madurai, "12 East Masi Street, Madurai"),
                (options.Preference!.Mode, options.Preference.TransporterId, options.Preference.DestinationBranchId, options.DeliveryAddress));

            // A lorry without a lorry service is refused, and nothing is billed.
            var refused = await browser.PostJsonAsync("/api/v1/pos/invoices", Bill(pack, 1, 100m, debtor.Id, new FulfilmentRequest("LORRY", "Madurai")));
            Assert.Equal("fulfilment.transporter_required", await refused.ProblemCodeAsync());

            var request = Bill(pack, 2, 200m, debtor.Id, ByLorry(transporter.Id, madurai));
            first = await Pos.IssueAsync(browser, request);
            Assert.Equal(first.Id, (await Pos.IssueAsync(browser, request)).Id); // a retry is the same bill
            second = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, ByLorry(transporter.Id, madurai)));
            var pickup = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, null));
            Assert.Equal(("PICKUP", "PICKUP"), (pickup.Fulfilment!.Mode, pickup.Fulfilment.Status));
        }

        Assert.Equal(("LORRY", "AWAITING_DISPATCH", transporter.Name, "Madurai branch, Madurai"),
            (first.Fulfilment!.Mode, first.Fulfilment.Status, first.Fulfilment.TransporterName, first.Fulfilment.DestinationBranch));
        Assert.DoesNotContain(await QueueAsync(owner), q => q.DebtorId == debtor.Id); // nothing packed yet
        Assert.Equal("consignment.nothing_packed", await (await RecordAsync(owner, Dispatch(booking, Lr(), first.Id))).ProblemCodeAsync());
        await PackAllAsync(owner, first, second);
        var waiting = (await QueueAsync(owner)).Where(q => q.DebtorId == debtor.Id).ToList();
        Assert.Equal([first.Number, second.Number], waiting.Select(q => q.InvoiceNumber));
        Assert.All(waiting, q => Assert.Equal(debtor.DisplayName, q.PartyName));

        // Booking details are required for a lorry; then both bills go on one LR.
        Assert.Equal("consignment.lr_invalid", await (await RecordAsync(owner, Dispatch(booking, Lr(), first.Id) with { LrNumber = null })).ProblemCodeAsync());
        Assert.Equal("consignment.booking_required", await (await RecordAsync(owner, Dispatch(Guid.NewGuid(), Lr(), first.Id))).ProblemCodeAsync());
        var lr = Lr();
        var recorded = await RecordAsync(owner, Dispatch(booking, lr.ToLowerInvariant(), first.Id, second.Id));
        await recorded.EnsureSuccessWithBodyAsync();
        var consignment = (await recorded.Content.ReadFromJsonAsync<ConsignmentDto>(TestClient.Json))!;
        Assert.Matches(@"^MAIN/DSP/\d{6}$", consignment.Number);
        Assert.Equal((lr, Today, "TO_PAY", 350m, 3, 42.5m, "TN38AB1234"),
            (consignment.LrNumber, consignment.LrDate, consignment.FreightTerms, consignment.FreightAmount, consignment.PackageCount, consignment.WeightKg, consignment.VehicleNumber));
        Assert.Equal((transporter.Name, transporter.Gstin, "Gandhipuram office, Coimbatore", "Madurai branch, Madurai", Today.AddDays(2)),
            (consignment.TransporterName, consignment.TransporterGstin, consignment.BookingOffice, consignment.DestinationBranch, consignment.ExpectedDeliveryDate));
        Assert.Equal((300m, false, debtor.DisplayName), (consignment.GoodsValue, consignment.EwayBillMissing, consignment.PartyName));
        Assert.Equal([first.Number, second.Number], consignment.Invoices.Select(i => i.Number).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(await QueueAsync(owner), q => q.DebtorId == debtor.Id);
        var fulfilment = await owner.GetJsonAsync<FulfilmentDto>($"{Base}/invoices/{first.Id}/fulfilment");
        Assert.Equal(("DISPATCHED", $"{consignment.Number} (LR {lr})"), (fulfilment.Status, Assert.Single(fulfilment.Consignments)));

        // The LR/GR register finds it by LR or by bill.
        Assert.Equal(consignment.Id, Assert.Single(await owner.GetJsonAsync<List<ConsignmentDto>>($"{Base}/consignments?search={lr}")).Id);
        Assert.Equal(consignment.Id, Assert.Single(await owner.GetJsonAsync<List<ConsignmentDto>>($"{Base}/consignments?search={Uri.EscapeDataString(second.Number)}")).Id);
    }

    [Fact]
    public async Task A_bill_is_dispatched_once_an_lr_is_used_once_per_lorry_service_and_a_cancelled_dispatch_frees_both()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (transporter, booking, madurai, salem) = await TransporterAsync(owner);
        var (other, otherBooking, otherDestination, _) = await TransporterAsync(owner);
        var debtor = await DebtorAsync(owner);
        var anotherDebtor = await DebtorAsync(owner);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        InvoiceDto first, second, third, elsewhere, theirs;
        using (browser)
        {
            first = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, ByLorry(transporter.Id, madurai)));
            second = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, ByLorry(transporter.Id, madurai)));
            third = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, new FulfilmentRequest("OWN_VEHICLE", "12 East Masi Street, Madurai")));
            elsewhere = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, ByLorry(other.Id)));
            theirs = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, anotherDebtor.Id, ByLorry(transporter.Id, madurai)));
        }

        await PackAllAsync(owner, first, second, third, elsewhere, theirs);

        // One customer, one way, one lorry service per dispatch.
        Assert.Equal("consignment.mixed", await (await RecordAsync(owner, Dispatch(booking, Lr(), first.Id, theirs.Id))).ProblemCodeAsync());
        Assert.Equal("consignment.mixed", await (await RecordAsync(owner, Dispatch(booking, Lr(), first.Id, third.Id))).ProblemCodeAsync());
        Assert.Equal("consignment.mixed", await (await RecordAsync(owner, Dispatch(booking, Lr(), first.Id, elsewhere.Id))).ProblemCodeAsync());

        var lr = Lr();
        var recorded = await RecordAsync(owner, Dispatch(booking, lr, first.Id) with { DestinationBranchId = salem }); // re-routed at booking
        await recorded.EnsureSuccessWithBodyAsync();
        var consignment = (await recorded.Content.ReadFromJsonAsync<ConsignmentDto>(TestClient.Json))!;
        Assert.Equal(("Salem branch, Salem", (DateOnly?)null), (consignment.DestinationBranch, consignment.ExpectedDeliveryDate)); // no route: no estimate

        Assert.Equal("consignment.nothing_packed", await (await RecordAsync(owner, Dispatch(booking, Lr(), first.Id))).ProblemCodeAsync());
        Assert.Equal("consignment.lr_taken", await (await RecordAsync(owner, Dispatch(booking, lr, second.Id))).ProblemCodeAsync());
        // No destination on the bill or the dispatch: refused; another lorry service's LR may be the same.
        Assert.Equal("consignment.destination_required", await (await RecordAsync(owner, Dispatch(otherBooking, lr, elsewhere.Id))).ProblemCodeAsync());
        (await RecordAsync(owner, Dispatch(otherBooking, lr, elsewhere.Id) with { DestinationBranchId = otherDestination })).EnsureSuccessStatusCode();
        var fulfilment = await owner.GetJsonAsync<FulfilmentDto>($"{Base}/invoices/{first.Id}/fulfilment");
        Assert.Equal("fulfilment.dispatched", await (await owner.PutJsonAsync($"{Base}/invoices/{first.Id}/fulfilment",
            new FulfilmentRequest("PICKUP", RowVersion: fulfilment.RowVersion))).ProblemCodeAsync());

        // Own vehicle: the vehicle number is required; an e-way bill is a warning only.
        Assert.Equal("consignment.vehicle_required", await (await RecordAsync(owner,
            new RecordConsignmentRequest(Guid.NewGuid().ToString("N"), [third.Id], 1, Today))).ProblemCodeAsync());
        var trip = await RecordAsync(owner, new RecordConsignmentRequest(Guid.NewGuid().ToString("N"), [third.Id], 1, Today, VehicleNumber: "TN38AB1234", DriverName: "Murugan",
            EwayBillNumber: "123456789012"));
        await trip.EnsureSuccessWithBodyAsync();
        var tripDto = (await trip.Content.ReadFromJsonAsync<ConsignmentDto>(TestClient.Json))!;
        Assert.Equal(("OWN_VEHICLE", (string?)null, "123456789012", "Murugan"), (tripDto.Mode, tripDto.LrNumber, tripDto.EwayBillNumber, tripDto.DriverName));

        // Cancelled in error: kept with the reason, the bill waits again, the LR is free.
        Assert.Equal("consignment.reason_required", await (await owner.PostJsonAsync($"{Base}/consignments/{consignment.Id}/cancel",
            new CancelConsignmentRequest(" ", consignment.RowVersion))).ProblemCodeAsync());
        var cancelled = await owner.PostJsonAsync($"{Base}/consignments/{consignment.Id}/cancel", new CancelConsignmentRequest("Booked on the wrong LR", consignment.RowVersion));
        await cancelled.EnsureSuccessWithBodyAsync();
        var cancelledDto = (await cancelled.Content.ReadFromJsonAsync<ConsignmentDto>(TestClient.Json))!;
        Assert.Equal(("CANCELLED", "Booked on the wrong LR"), (cancelledDto.Status, cancelledDto.CancelReason));
        Assert.Contains(await QueueAsync(owner), q => q.InvoiceId == first.Id);
        (await RecordAsync(owner, Dispatch(booking, lr, second.Id))).EnsureSuccessStatusCode();
        (await RecordAsync(owner, Dispatch(booking, Lr(), first.Id))).EnsureSuccessStatusCode();
        Assert.Equal(2, (await owner.GetJsonAsync<List<ConsignmentDto>>($"{Base}/consignments?search={lr}&transporterId={transporter.Id}")).Count);
    }

    [Fact]
    public async Task Goods_worth_fifty_thousand_or_more_are_flagged_without_an_eway_bill_and_a_retry_records_the_dispatch_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (transporter, booking, madurai, _) = await TransporterAsync(owner);
        var debtor = await DebtorAsync(owner);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 25_000m, stock: 10);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        InvoiceDto invoice;
        using (browser)
        {
            invoice = await Pos.IssueAsync(browser, Bill(pack, 2, 50_000m, debtor.Id, ByLorry(transporter.Id, madurai)));
        }

        await PackAllAsync(owner, invoice);
        var request = Dispatch(booking, Lr(), invoice.Id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RecordAsync(owner, request)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<ConsignmentDto>(TestClient.Json))!));
        Assert.Single(ids.Select(c => c.Id).Distinct());
        Assert.True(ids[0].EwayBillMissing);
        Assert.Equal("idempotency.mismatch", await (await RecordAsync(owner, request with { PackageCount = 9 })).ProblemCodeAsync());
    }

    [Fact]
    public async Task The_same_bill_dispatched_from_several_screens_at_once_goes_out_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => owner.GetAsync($"{Base}/dispatch/queue"))); // warm up
        var (transporter, booking, madurai, _) = await TransporterAsync(owner);
        var debtor = await DebtorAsync(owner);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        var bills = new List<InvoiceDto>();
        using (browser)
        {
            for (var i = 0; i < 3; i++)
            {
                bills.Add(await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, ByLorry(transporter.Id, madurai))));
            }
        }

        await PackAllAsync(owner, [.. bills]);
        foreach (var bill in bills)
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RecordAsync(owner, Dispatch(booking, Lr(), bill.Id)))));
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        }
    }

    [Fact]
    public async Task The_database_keeps_dispatches_as_recorded_and_bills_dispatched_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (transporter, booking, madurai, _) = await TransporterAsync(owner);
        var debtor = await DebtorAsync(owner);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        InvoiceDto invoice, other;
        using (browser)
        {
            invoice = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, ByLorry(transporter.Id, madurai)));
            other = await Pos.IssueAsync(browser, Bill(pack, 1, 100m, debtor.Id, ByLorry(transporter.Id, madurai)));
        }

        await PackAllAsync(owner, invoice, other);
        var recorded = await RecordAsync(owner, Dispatch(booking, Lr(), invoice.Id));
        await recorded.EnsureSuccessWithBodyAsync();
        var consignment = (await recorded.Content.ReadFromJsonAsync<ConsignmentDto>(TestClient.Json))!;
        var second = await RecordAsync(owner, Dispatch(booking, Lr(), other.Id));
        await second.EnsureSuccessWithBodyAsync();
        var otherConsignment = (await second.Content.ReadFromJsonAsync<ConsignmentDto>(TestClient.Json))!;

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[]
                     {
                         "UPDATE consignments SET freight_amount = 1 WHERE id = @id",
                         "UPDATE consignments SET lr_number = 'CHANGED' WHERE id = @id",
                         "DELETE FROM consignments WHERE id = @id",
                         "DELETE FROM consignment_invoices WHERE consignment_id = @id",
                         "UPDATE invoice_fulfilments SET mode = 'PICKUP', delivery_address = NULL, transporter_id = NULL, destination_branch_id = NULL WHERE invoice_id = @invoice",
                         "DELETE FROM invoice_fulfilments WHERE invoice_id = @other",
                     })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("id", consignment.Id);
                command.Parameters.AddWithValue("invoice", invoice.Id);
                command.Parameters.AddWithValue("other", other.Id);
                Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }

            // Sending the same packed goods again on another dispatch directly is refused too.
            await using var insert = new NpgsqlCommand(
                "INSERT INTO consignment_lines (consignment_id, challan_line_id, business_id, tenant_id, quantity) " +
                "SELECT @other, cl.challan_line_id, cl.business_id, cl.tenant_id, 1 FROM consignment_lines cl WHERE cl.consignment_id = @id", db);
            insert.Parameters.AddWithValue("id", consignment.Id);
            insert.Parameters.AddWithValue("other", otherConsignment.Id);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        }

        var sql14 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "014_dispatch.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using var verify = new NpgsqlCommand(sql14, admin);
        await verify.ExecuteNonQueryAsync();
    }
}
