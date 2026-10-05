using System.Globalization;
using System.Net;
using System.Text.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Reporting;

[Collection(ApiTestGroup.Name)]
public sealed class DashboardTests(ApiFactory factory)
{
    private const string Day = "2026-10-01";

    private static decimal M(object? value) =>
        value is JsonElement e ? (e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : 0m) : Convert.ToDecimal(value ?? 0m, CultureInfo.InvariantCulture);

    private static DashboardSection Section(DashboardDto dashboard, string key) => dashboard.Sections.Single(s => s.Key == key);

    private static decimal Metric(DashboardDto dashboard, string section, string label) => Section(dashboard, section).Metrics.Single(m => m.Label == label).Value ?? 0m;

    [Fact]
    public async Task Every_tile_equals_its_report_and_a_store_view_leaves_out_business_wide_sections()
    {
        var (business, _, _) = await ReportBusiness.EnsureAsync(factory);
        var root = $"/api/v1/businesses/{business}";
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var dashboard = await owner.GetJsonAsync<DashboardDto>($"{root}/dashboard");
        Assert.Equal(new DateOnly(2026, 10, 1), dashboard.Today);
        Assert.All(dashboard.Sections, s => Assert.Equal("ok", s.Status));
        Assert.Equal(["sales-today", "sales-month", "payments-today", "counters-today", "cashiers-today", "top-items", "top-categories", "stock", "purchases", "balances",
            "collections", "cash", "approvals", "dispatch", "backup", "devices"], dashboard.Sections.Select(s => s.Key));

        Task<ReportDto> Report(string key, string query = "") => owner.GetJsonAsync<ReportDto>($"{root}/reports/{key}?from={Day}&to={Day}{query}");
        var summary = (await Report("sales-summary")).Totals!;
        Assert.Equal((M(summary["net_sales"]), M(summary["bills"]), M(summary["returns"])),
            (Metric(dashboard, "sales-today", "Net sales"), Metric(dashboard, "sales-today", "Bills"), Metric(dashboard, "sales-today", "Returns")));
        Assert.Equal(M((await Report("items")).Totals!["profit"]), Metric(dashboard, "sales-today", "Gross profit"));
        Assert.Equal(M((await Report("stock-origin", "&by=category")).Totals!["total_value"]), Metric(dashboard, "stock", "Stock value (FIFO cost)"));
        Assert.Equal(M((await Report("credit-ageing")).Totals!["balance"]), Metric(dashboard, "balances", "Owed by debtors"));
        Assert.Equal(M((await Report("suppliers")).Totals!["closing"]), Metric(dashboard, "balances", "Owed to suppliers"));
        Assert.Equal(M((await Report("collections")).Totals!["net"]), Metric(dashboard, "collections", "Collected today"));
        Assert.Equal(M((await Report("purchases")).Totals!["invoice_total"]), Metric(dashboard, "purchases", "Invoice value"));

        // The top items are the report's best sellers, with the report's figures.
        var items = await owner.GetJsonAsync<ReportDto>($"{root}/reports/items?from=2026-10-01&to={Day}");
        var top = Section(dashboard, "top-items").Table!;
        Assert.True(top.Rows.Count <= 5);
        Assert.Equal(items.Rows.Select(r => M(r["net_sales"])).OrderDescending().Take(top.Rows.Count), top.Rows.Select(r => M(r["net_sales"])));
        Assert.Contains(Section(dashboard, "backup").Metrics, m => m.Label == "Status" && m.Tone is "good" or "warning" or "bad");

        // One store: its own figures; balances and approvals are for the whole business, so they are left out.
        var store = await ReportBusiness.StoreAsync(factory, business, "AS1", "Accounts store");
        var local = await owner.GetJsonAsync<DashboardDto>($"{root}/dashboard?storeId={store}");
        Assert.DoesNotContain(local.Sections, s => s.Key is "balances" or "approvals");
        Assert.Equal(M((await Report("sales-summary", $"&storeId={store}")).Totals!["net_sales"]), Metric(local, "sales-today", "Net sales"));
    }

    [Fact]
    public async Task Only_report_users_see_the_dashboard()
    {
        var (business, _, _) = await ReportBusiness.EnsureAsync(factory);
        var (cashier, _, _) = await ReportBusiness.UserAsync(factory, "cashier");
        using (cashier)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/businesses/{business}/dashboard")).StatusCode);
        }
    }
}
