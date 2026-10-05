using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;
using SupermarketBilling.IntegrationTests.Purchases;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Reporting;

[Collection(ApiTestGroup.Name)]
public sealed class StockReportTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Day = "2026-10-01";
    private static readonly SemaphoreSlim Once = new(1, 1);
    private static Scenario? shared;

    private sealed record Scenario(Guid Business, Guid Store, Guid Store2, string Item, string ExpiryItem, string Registered, string Unregistered);

    private static Scenario S => shared!;

    private static string Base => $"/api/v1/businesses/{S.Business}/reports";

    /// <summary>
    /// Stores, items and suppliers of their own in the shared GST-registered report business. Item P (5% GST): opening stock 2 at Rs. 50 (origin other), then a GST tax
    /// invoice for 10 at Rs. 100 + GST (origin GST) and an unregistered supplier's bill for 5 at Rs. 80 (non-GST). Three
    /// are sold (FIFO: the 2 opening and 1 GST), 2 move to a second store (GST lots), and 1 goes back to the unregistered
    /// supplier. Item E is batch tracked: 4 at Rs. 10 expiring on 20 October.
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

            var (business, _, _) = await ReportBusiness.EnsureAsync(factory);
            var store = await ReportBusiness.StoreAsync(factory, business, "SS1", "Stock store");
            var store2 = await ReportBusiness.StoreAsync(factory, business, "SS2", "Stock second store");
            using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
            var (product, pack) = await Pos.StockedProductAsync(owner, business, store, price: 150m, stock: 0);
            await StockTests.PostAsync(owner, business, StockTests.Doc("OPENING", store, StockTests.Line(product, 2, cost: 50m)));

            var registered = await Purchasing.SupplierAsync(owner, business, SupermarketBilling.Domain.Tax.Gstin.Complete($"33AAACR{Random.Shared.Next(1000, 9999)}S1Z"));
            var unregistered = await Purchasing.SupplierAsync(owner, business);
            await Purchasing.SaveAsync(owner, business, Purchasing.Request(store, registered.Id, "GST_TAX_INVOICE", new GrnLineRequest(pack, 10, 100m)));
            var bill = await Purchasing.SaveAsync(owner, business, Purchasing.Request(store, unregistered.Id, "UNREGISTERED", new GrnLineRequest(pack, 5, 80m, CostChangeReason: "Unregistered supplier, no GST")));
            if (bill.Status == "PENDING_APPROVAL")
            {
                // A 20% cost change waits for a second person: the business's first manager approves it.
                var (_, manager, password) = await ReportBusiness.EnsureAsync(factory);
                using var approver = await factory.LoginAsync(manager, password);
                await (await approver.PostJsonAsync($"/api/v1/approvals/{bill.ApprovalRequestId}/approve", new ApprovalDecisionRequest("Unregistered supplier's price")))
                    .EnsureSuccessWithBodyAsync();
            }


            var expiring = await CatalogTests.CreateProductAsync(owner, business, tracksBatches: true, tracksExpiry: true);
            await Purchasing.SaveAsync(owner, business, Purchasing.Request(store, registered.Id, "GST_TAX_INVOICE",
                new GrnLineRequest(expiring.Variants.Single().Units.Single().Id, 4, 10m, BatchNumber: "B-OCT", ExpiresOn: new DateOnly(2026, 10, 20))));

            var cashier = await ReportBusiness.UserAsync(factory, "manager");
            cashier.Client.Dispose();
            var session = await Pos.CounterBrowserAsync(factory, business, store, cashier.Username, cashier.Password);
            using (session.Browser)
            {
                await Pos.IssueAsync(session.Browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 3)), 450m));
            }

            await StockTests.PostAsync(owner, business, new PostStockDocumentRequest("TRANSFER", store, store2, "To the second store", null, Guid.NewGuid().ToString("N"),
                false, [StockTests.Line(product, 2)]));
            var returnable = await SupermarketBilling.IntegrationTests.Purchases.Returns.ReturnableAsync(owner, business, bill.Id);
            await SupermarketBilling.IntegrationTests.Purchases.Returns.SaveAsync(owner, business,
                SupermarketBilling.IntegrationTests.Purchases.Returns.Request(bill.Id, "Damaged in transit", new PurchaseReturnLineRequest(returnable.Lines[0].GrnLineId, 1)));
            shared = new Scenario(business, store, store2, product.Code, expiring.Code, registered.Code, unregistered.Code);
        }
        finally
        {
            Once.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Task<ReportDto> RunAsync(TestClient client, string key, string query = "") =>
        client.GetJsonAsync<ReportDto>($"{Base}/{key}?from={Day}&to={Day}{query}");

    private static decimal M(IReadOnlyDictionary<string, object?> row, string key) =>
        row[key] is JsonElement e ? (e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : 0m) : Convert.ToDecimal(row[key], CultureInfo.InvariantCulture);

    private static string T(IReadOnlyDictionary<string, object?> row, string key) => row[key] is JsonElement e ? e.ToString() : row[key]?.ToString() ?? string.Empty;

    private static IReadOnlyDictionary<string, object?> Starting(ReportDto report, string column, string prefix) =>
        report.Rows.Single(r => T(r, column).StartsWith(prefix + " ", StringComparison.Ordinal));

    [Fact]
    public async Task Stock_by_origin_valuation_and_the_ledger_summary_agree()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        // Origin follows the goods: 9 GST lots (7 here, 2 transferred), 4 non-GST (one went back), none of the opening stock left.
        var origin = await RunAsync(owner, "stock-origin");
        var p = Starting(origin, "name", S.Item);
        Assert.Equal((9m, 900m, 4m, 320m, 0m, 0m, 13m, 1220m),
            (M(p, "gst_qty"), M(p, "gst_value"), M(p, "non_gst_qty"), M(p, "non_gst_value"), M(p, "other_qty"), M(p, "other_value"), M(p, "total_qty"), M(p, "total_value")));
        var second = Starting(await RunAsync(owner, "stock-origin", $"&storeId={S.Store2}"), "name", S.Item);
        Assert.Equal((2m, 200m, 0m), (M(second, "gst_qty"), M(second, "gst_value"), M(second, "non_gst_qty")));

        // Valuation at the end of the day (from the ledger) = the lots in stock.
        var valuation = await RunAsync(owner, "stock-valuation");
        var v = Starting(valuation, "name", S.Item);
        Assert.Equal((13m, 1220m, 93.85m), (M(v, "quantity"), M(v, "value"), M(v, "unit_cost")));
        Assert.Equal(M(origin.Totals!, "total_value"), M(valuation.Totals!, "value"));

        // Ledger summary: opening + purchased - sold + other = closing = valuation.
        var summary = await RunAsync(owner, "stock-summary");
        var s = Starting(summary, "name", S.Item);
        Assert.Equal((0m, 14m, 1320m, 3m, 200m, 2m, 100m, 13m, 1220m),
            (M(s, "opening_qty"), M(s, "purchased_qty"), M(s, "purchased_value"), M(s, "sold_qty"), M(s, "sold_value"), M(s, "other_qty"), M(s, "other_value"),
                M(s, "closing_qty"), M(s, "closing_value")));
        Assert.Equal(M(valuation.Totals!, "value"), M(summary.Totals!, "closing_value"));
        var byStore = await RunAsync(owner, "stock-summary", "&by=store");
        Assert.Equal(M(summary.Totals!, "closing_value"), M(byStore.Totals!, "closing_value"));
        Assert.Equal(200m, M(byStore.Rows.Single(r => T(r, "name").EndsWith("Stock second store", StringComparison.Ordinal)), "closing_value"));

        // The next day opens with what this day closed with.
        var tomorrow = await owner.GetJsonAsync<ReportDto>($"{Base}/stock-summary?from=2026-10-02&to=2026-10-02");
        Assert.Equal((13m, 1220m), (M(Starting(tomorrow, "name", S.Item), "opening_qty"), M(Starting(tomorrow, "name", S.Item), "opening_value")));
    }

    [Fact]
    public async Task Purchases_by_type_debit_notes_and_supplier_balances_add_up()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var purchases = await RunAsync(owner, "purchases");
        var gst = purchases.Rows.Single(r => T(r, "group") == "GST tax invoice");
        Assert.Equal((2m, 1040m, 26m, 26m, 1092m, 52m, 1040m),
            (M(gst, "receipts"), M(gst, "taxable"), M(gst, "cgst"), M(gst, "sgst"), M(gst, "invoice_total"), M(gst, "itc"), M(gst, "landed")));
        var unreg = purchases.Rows.Single(r => T(r, "group") == "Unregistered supplier");
        Assert.Equal((1m, 400m, 400m, 0m, 400m), (M(unreg, "receipts"), M(unreg, "taxable"), M(unreg, "invoice_total"), M(unreg, "itc"), M(unreg, "landed")));
        var bySupplier = await RunAsync(owner, "purchases", "&by=supplier");
        Assert.Equal(M(purchases.Totals!, "invoice_total"), M(bySupplier.Totals!, "invoice_total"));
        Assert.Equal(3, (await RunAsync(owner, "purchases", "&by=document")).Rows.Count);

        var note = Assert.Single((await RunAsync(owner, "purchase-returns")).Rows);
        Assert.Equal(("Damaged in transit", 80m, 0m, 80m), (T(note, "reason"), M(note, "total"), M(note, "itc_reversed"), M(note, "stock_value")));

        var suppliers = await RunAsync(owner, "suppliers");
        var r = Starting(suppliers, "supplier", S.Registered);
        Assert.Equal((0m, 1092m, 0m, 1092m), (M(r, "opening"), M(r, "bills"), M(r, "returns"), M(r, "closing")));
        var u = Starting(suppliers, "supplier", S.Unregistered);
        Assert.Equal((400m, 80m, 320m), (M(u, "bills"), M(u, "returns"), M(u, "closing")));
        Assert.Equal(M(purchases.Totals!, "invoice_total") - 80m, M(suppliers.Totals!, "closing"));
        Assert.Equal("report.business_only", await (await owner.GetAsync($"{Base}/suppliers?from={Day}&to={Day}&storeId={S.Store}")).ProblemCodeAsync());
    }

    [Fact]
    public async Task Expiring_batches_and_stock_age_are_listed()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var expiry = await owner.GetJsonAsync<ReportDto>($"{Base}/expiry?from={Day}&to=2026-10-31");
        var batch = Starting(expiry, "name", S.ExpiryItem);
        Assert.Equal(("B-OCT", "2026-10-20", "19", 4m, 40m), (T(batch, "batch"), T(batch, "expires"), T(batch, "days_left"), M(batch, "quantity"), M(batch, "value")));
        Assert.Empty((await owner.GetJsonAsync<ReportDto>($"{Base}/expiry?from={Day}&to=2026-10-15")).Rows);

        var ageing = await RunAsync(owner, "stock-ageing");
        var p = Starting(ageing, "name", S.Item);
        Assert.Equal((13m, 13m, 0m, 1220m), (M(p, "d30"), M(p, "quantity"), M(p, "older"), M(p, "value")));
        Assert.Empty((await RunAsync(owner, "negative-stock")).Rows);
    }

    [Fact]
    public async Task The_origin_of_a_lot_cannot_be_changed_in_the_database()
    {
        await using var db = await factory.OpenAppConnectionAsync(await TenantOfAsync());
        await using var command = new NpgsqlCommand("UPDATE cost_layers SET origin = 'GST' WHERE origin = 'NON_GST' AND business_id = @b", db);
        command.Parameters.AddWithValue("b", S.Business);
        Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    private async Task<Guid> TenantOfAsync()
    {
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using var command = new NpgsqlCommand("SELECT tenant_id FROM businesses WHERE id = @b", admin);
        command.Parameters.AddWithValue("b", S.Business);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
}
