using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Reporting;

[Collection(ApiTestGroup.Name)]
public sealed class ReportTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Day = "2026-10-01";
    private static readonly SemaphoreSlim Once = new(1, 1);
    private static Scenario? shared;

    private static string Base => $"/api/v1/businesses/{S.Business}/reports";

    private sealed record Scenario(Guid Business, Guid StoreId, Guid OtherStoreId, string CashierName, InvoiceDto Bill, InvoiceDto B2b, string Buyer, string Manager,
        string ManagerPassword);

    /// <summary>
    /// A GST-registered business of its own (so other tests' bills do not mix in): bill 1 is 2 x A (Rs. 105 with 5% GST) and 1 x B (Rs. 50,
    /// exempt), paid Rs. 300 cash (Rs. 40 change); bill 2 is 1 x A to a GST-registered buyer by UPI; then 1 x A of bill 1
    /// is returned for cash. Stock cost is Rs. 10 a piece.
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

            using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
            var code = $"RPT{Random.Shared.Next(100, 999)}";
            var (business, storeId) = await GstBillingTests.BusinessAsync(owner, code, "GST_REGULAR",
                SupermarketBilling.Domain.Tax.Gstin.Complete($"33AAACR{Random.Shared.Next(1000, 9999)}R1Z"));
            var other = await (await owner.PostJsonAsync($"/api/v1/businesses/{business}/stores", new CreateStoreRequest("S2", "Second store", "33", null, null)))
                .Content.ReadFromJsonAsync<StoreDto>(TestClient.Json);
            var (_, a) = await Pos.StockedProductAsync(owner, business, storeId, price: 105m);
            var (_, b) = await Pos.StockedProductAsync(owner, business, storeId, price: 50m, supply: "EXEMPT");
            var cashier = await factory.CreateSignedInUserAsync("manager", businessId: business);
            cashier.Client.Dispose();
            var session = await Pos.CounterBrowserAsync(factory, business, storeId, cashier.Username, cashier.Password);
            using (session.Browser)
            {
                var bill = await Pos.IssueAsync(session.Browser, Pos.Issue(Pos.Cart(new CartLineRequest(a, 2), new CartLineRequest(b, 1)), 260m,
                    new PaymentRequest("CASH", 300m, null)));
                var buyer = "=HYPERLINK(\"x\") Traders";
                var b2b = await Pos.IssueAsync(session.Browser, Pos.Issue(Pos.Cart(new CartLineRequest(a, 1)) with
                {
                    Buyer = new BuyerRequest(buyer, SupermarketBilling.Domain.Tax.Gstin.Complete("33AAACK1234C1Z"), null, "Gandhipuram, Coimbatore", null),
                }, 105m, new PaymentRequest("UPI", 105m, "UPI-77")));
                var found = await Returns.FindAsync(session.Browser, bill.Number);
                var lineA = found.Lines.First(l => l.Sold == 2).OriginalLineId;
                await Returns.IssueAsync(session.Browser, Returns.Request(bill.Id, 105m, [new ReturnLineRequest(lineA, 1)]));
                shared = new Scenario(business, storeId, other!.Id, "Test manager", bill, b2b, buyer, cashier.Username, cashier.Password); // test users are named after their role
            }
        }
        finally
        {
            Once.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Scenario S => shared!;

    /// <summary>A user of the report business; a privileged role is approved by its first manager (someone other than the owner).</summary>
    private async Task<TestClient> UserAsync(string role, Guid? storeId = null)
    {
        var username = $"r{Guid.NewGuid():N}"[..20];
        ApiFactory.CreateUserResponseDto body;
        using (var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword))
        {
            var created = await owner.PostJsonAsync($"/api/v1/businesses/{S.Business}/users", new CreateUserRequest(username, "Report " + role, "Temporary-Pass-001", role, storeId));
            await created.EnsureSuccessWithBodyAsync();
            body = (await created.Content.ReadFromJsonAsync<ApiFactory.CreateUserResponseDto>(TestClient.Json))!;
        }

        if (body.Role.Outcome == "pending_approval")
        {
            using var approver = await factory.LoginAsync(S.Manager, S.ManagerPassword);
            await (await approver.PostJsonAsync($"/api/v1/approvals/{body.Role.ApprovalRequestId}/approve", new ApprovalDecisionRequest("report test"))).EnsureSuccessWithBodyAsync();
        }

        var client = await factory.LoginAsync(username, "Temporary-Pass-001");
        await (await client.PostJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest("Temporary-Pass-001", "Report-User-Password-9"))).EnsureSuccessWithBodyAsync();
        return client;
    }

    private static Task<ReportDto> RunAsync(TestClient client, string key, string? by = null) =>
        client.GetJsonAsync<ReportDto>($"{Base}/{key}?from={Day}&to={Day}&storeId={S.StoreId}{(by is null ? string.Empty : $"&by={by}")}");

    private static decimal M(IReadOnlyDictionary<string, object?> row, string key) =>
        row[key] is JsonElement e ? e.GetDecimal() : Convert.ToDecimal(row[key], CultureInfo.InvariantCulture);

    private static string T(IReadOnlyDictionary<string, object?> row, string key) => row[key] is JsonElement e ? e.ToString() : row[key]?.ToString() ?? string.Empty;

    private static IReadOnlyDictionary<string, object?> Row(ReportDto report, string column, string value) => report.Rows.Single(r => T(r, column) == value);

    /// <summary>Every summed column of the totals row is the sum of the rows.</summary>
    private static void TotalsAddUp(ReportDto report)
    {
        foreach (var column in report.Columns.Skip(1).Where(c => c.Kind is "money" or "quantity" or "count"))
        {
            if (report.Totals!.TryGetValue(column.Key, out var total) && total is not null && column.Key != "average_bill")
            {
                Assert.Equal(report.Rows.Sum(r => r.TryGetValue(column.Key, out var v) && v is JsonElement { ValueKind: JsonValueKind.Number } e ? e.GetDecimal() : 0m),
                    total is JsonElement t ? t.GetDecimal() : 0m);
            }
        }
    }

    [Fact]
    public async Task The_sales_summary_reconciles_by_day_and_by_cashier_and_with_payments_gst_and_items()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var byDay = await RunAsync(owner, "sales-summary");
        var day = Assert.Single(byDay.Rows);
        Assert.Equal((2m, 365m, 365m, 1m, 105m, 260m, 250m),
            (M(day, "bills"), M(day, "taxable") + M(day, "cgst") + M(day, "sgst"), M(day, "sales"), M(day, "returns_count"), M(day, "returns"), M(day, "net_sales"),
                M(day, "net_taxable")));
        Assert.Equal(350m, M(day, "taxable"));
        TotalsAddUp(byDay);
        var byCashier = await RunAsync(owner, "sales-summary", "cashier");
        Assert.Equal(S.CashierName, T(Assert.Single(byCashier.Rows), "group"));
        Assert.Equal(M(byDay.Totals!, "net_sales"), M(byCashier.Totals!, "net_sales"));

        // Payments: cash after change, and what was received is what was billed.
        var payments = await RunAsync(owner, "payments");
        Assert.Equal((260m, 105m, 155m), (M(Row(payments, "method", "Cash"), "received"), M(Row(payments, "method", "Cash"), "refunded"), M(Row(payments, "method", "Cash"), "net")));
        Assert.Equal(105m, M(Row(payments, "method", "Upi"), "received"));
        Assert.Equal((M(day, "sales"), M(day, "returns")), (M(payments.Totals!, "received"), M(payments.Totals!, "refunded")));
        TotalsAddUp(payments);

        // GST by rate: the taxable values add up to the summary, net of the return.
        var gst = await RunAsync(owner, "gst-rates");
        var five = gst.Rows.Single(r => T(r, "category") == "Taxable" && M(r, "rate") == 5m);
        Assert.Equal((300m, 7.5m, 7.5m, 100m, 5m, 200m, 10m),
            (M(five, "sales_taxable"), M(five, "sales_cgst"), M(five, "sales_sgst"), M(five, "returns_taxable"), M(five, "returns_tax"), M(five, "net_taxable"), M(five, "net_tax")));
        Assert.Equal(50m, M(Row(gst, "category", "Exempt"), "net_taxable"));
        Assert.Equal((M(day, "taxable"), M(day, "net_taxable")), (M(gst.Totals!, "sales_taxable"), M(gst.Totals!, "net_taxable")));
        TotalsAddUp(gst);
        var hsn = await RunAsync(owner, "hsn");
        Assert.Equal(M(day, "net_taxable"), M(hsn.Totals!, "taxable"));
        Assert.Equal(M(gst.Totals!, "net_taxable") + M(gst.Totals!, "net_tax"), M(hsn.Totals!, "value"));

        // Items: quantities net of the return; profit from FIFO cost (Rs. 10 a piece), and the net sales match the summary.
        var items = await RunAsync(owner, "items");
        var a = items.Rows.Single(r => M(r, "sold_qty") == 3m);
        Assert.Equal((1m, 2m, 200m, 20m, 180m, 90m), (M(a, "returned_qty"), M(a, "net_qty"), M(a, "net_sales"), M(a, "cost"), M(a, "profit"), M(a, "margin")));
        Assert.Equal(M(day, "net_taxable"), M(items.Totals!, "net_sales"));
        Assert.Equal(Math.Round(M(items.Totals!, "profit") * 100 / M(items.Totals!, "net_sales"), 2), M(items.Totals!, "margin"));
        TotalsAddUp(items);
        Assert.Equal(M(day, "net_taxable"), M((await RunAsync(owner, "items", "category")).Totals!, "net_sales"));
    }

    [Fact]
    public async Task Cashier_shift_returns_and_b2b_reports_list_what_happened()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var cashiers = await RunAsync(owner, "cashiers");
        var row = Assert.Single(cashiers.Rows);
        Assert.Equal((2m, 365m, 182.5m, 1m, 105m), (M(row, "bills"), M(row, "sales"), M(row, "average_bill"), M(row, "returns_count"), M(row, "returns")));
        Assert.Equal(182.5m, M(cashiers.Totals!, "average_bill"));

        var shift = Assert.Single((await RunAsync(owner, "shifts")).Rows);
        Assert.Equal((S.CashierName, "Open"), (T(shift, "cashier"), T(shift, "status")));

        var returns = Assert.Single((await RunAsync(owner, "returns")).Rows);
        Assert.Equal((S.Bill.Number, "Customer changed mind", 105m, "Cash 105.00"), (T(returns, "invoice"), T(returns, "reason"), M(returns, "total"), T(returns, "refunds")));

        var b2b = await RunAsync(owner, "b2b");
        var invoice = Assert.Single(b2b.Rows);
        Assert.Equal((S.B2b.Number, S.Buyer, 100m, 105m, "33 - Tamil Nadu"), (T(invoice, "number"), T(invoice, "party"), M(invoice, "taxable"), M(invoice, "total"), T(invoice, "place")));
    }

    [Fact]
    public async Task Reports_export_to_csv_without_letting_a_spreadsheet_run_formulas()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var response = await owner.Http.GetAsync(new Uri($"{Base}/b2b?from={Day}&to={Day}&storeId={S.StoreId}&format=csv", UriKind.Relative));
        Assert.Equal(("text/csv", $"b2b_{Day}_{Day}.csv"), (response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentDisposition?.FileNameStar));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length > 1
            ? Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            : Encoding.UTF8.GetString(bytes[3..]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("\"Number\",\"Document\",\"Date\",\"Buyer GSTIN\",\"Buyer\",\"Place of supply\",\"Taxable value\",\"CGST\",\"SGST\",\"IGST\",\"Cess\",\"Total\"", lines[0]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\") Traders\"", lines[1], StringComparison.Ordinal);
        Assert.Contains($"\"{Day}\"", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("\"Total\"", lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("\"105.00\"", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_need_permission_profit_needs_more_and_a_store_manager_sees_only_their_store()
    {
        using (var cashier = await UserAsync("cashier"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"{Base}?")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"{Base}/sales-summary?from={Day}&to={Day}&storeId={S.StoreId}")).StatusCode);
        }

        // Managers, accountants and auditors see cost and profit (managers can appoint auditors, so they hold what auditors hold).
        using (var manager = await factory.LoginAsync(S.Manager, S.ManagerPassword))
        {
            Assert.True((await manager.GetJsonAsync<List<ReportDefinitionDto>>(Base)).Single(d => d.Key == "items").ShowsProfit);
            Assert.Contains((await RunAsync(manager, "items")).Columns, c => c.Key == "margin");
        }

        // A manager of one store reports on that store only.
        using (var local = await UserAsync("manager", S.StoreId))
        {
            Assert.Equal(2m, M((await RunAsync(local, "sales-summary")).Totals!, "bills"));
            Assert.Equal(HttpStatusCode.Forbidden, (await local.GetAsync($"{Base}/sales-summary?from={Day}&to={Day}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await local.GetAsync($"{Base}/sales-summary?from={Day}&to={Day}&storeId={S.OtherStoreId}")).StatusCode);
        }

        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        Assert.Equal("report.range_invalid", await (await owner.GetAsync($"{Base}/sales-summary?from=2026-10-02&to=2026-10-01")).ProblemCodeAsync());
        Assert.Equal("report.range_invalid", await (await owner.GetAsync($"{Base}/sales-summary?from=2025-09-01&to=2026-10-01")).ProblemCodeAsync());
        Assert.Equal("report.grouping_invalid", await (await owner.GetAsync($"{Base}/sales-summary?from={Day}&to={Day}&by=colour")).ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{Base}/no-such-report?from={Day}&to={Day}")).StatusCode);
    }
}
