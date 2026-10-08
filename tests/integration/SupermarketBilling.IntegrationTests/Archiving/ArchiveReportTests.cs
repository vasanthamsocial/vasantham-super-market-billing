using System.Net;
using System.Text.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Archiving;

/// <summary>Historical reports on the archive server (D-043): the store's figures, only to those allowed.</summary>
public sealed partial class ArchiveServerTests
{
    private const string OctoberRange = "from=2026-10-01&to=2026-10-31";

    private string Reports => $"/api/v1/archive/businesses/{pair.Store.BusinessId}/reports";

    /// <summary>The owner administrator, with September and October imported (by this test or an earlier one).</summary>
    private async Task<TestClient> ImportedAsync()
    {
        var owner = await Archive.LoginAsync(ArchivePair.OwnerUsername, ArchivePair.OwnerPassword);
        foreach (var month in new[] { pair.September, pair.October })
        {
            var response = await UploadAsync(owner, month);
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        return owner;
    }

    [Fact]
    public async Task Archived_months_report_the_same_figures_as_the_store_server_did()
    {
        using var owner = await ImportedAsync();
        using var storeOwner = await pair.Store.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        foreach (var (key, by) in new (string, string?)[]
                 {
                     ("sales-summary", "day"), ("sales-summary", "store"), ("payments", null), ("gst-rates", null), ("hsn", null), ("b2b", null), ("items", "item"),
                     ("items", "category"), ("items", "brand"), ("returns", null), ("shifts", null),
                 })
        {
            var query = by is null ? OctoberRange : $"{OctoberRange}&by={by}";
            var store = await storeOwner.GetJsonAsync<JsonElement>($"/api/v1/businesses/{pair.Store.BusinessId}/reports/{key}?{query}");
            var archived = await owner.GetJsonAsync<JsonElement>($"{Reports}/{key}?{query}");
            foreach (var part in new[] { "title", "columns", "rows", "totals" })
            {
                Assert.True(store.GetProperty(part).GetRawText() == archived.GetProperty(part).GetRawText(),
                    $"{key} by {by}: {part} differ.\nStore:   {store.GetProperty(part).GetRawText()}\nArchive: {archived.GetProperty(part).GetRawText()}");
            }
        }

        // Not empty: the month's bills, the credit note and the cash after change are there.
        var summary = await owner.GetJsonAsync<ReportDto>($"{Reports}/sales-summary?{OctoberRange}");
        Assert.Equal("2026-10", Assert.Single(summary.Rows)["group"]?.ToString());
        Assert.Equal((238m, 40m, 198m), (Number(summary.Totals!["sales"]), Number(summary.Totals["returns"]), Number(summary.Totals["net_sales"])));
        var payments = await owner.GetJsonAsync<ReportDto>($"{Reports}/payments?{OctoberRange}");
        Assert.Equal(118m, Number(payments.Rows.Single(r => r["method"]?.ToString() == "Cash")["net"]));

        // Reports of the archive's own: the customer's opening balance in September.
        var ledger = await owner.GetJsonAsync<ReportDto>($"{Reports}/debtor-ledger?from=2026-09-01&to=2026-09-30");
        var debtor = Assert.Single(ledger.Rows);
        Assert.Equal((1m, 500m, 500m), (Number(debtor["entries"]), Number(debtor["added"]), Number(debtor["balance"])));
        var stock = await owner.GetJsonAsync<ReportDto>($"{Reports}/stock-movements?{OctoberRange}");
        Assert.Equal((201m, 4m), (Number(stock.Totals!["in"]), Number(stock.Totals["out"]))); // opening stock of both items and 1 back; 4 sold
        Assert.Contains(stock.Columns, c => c.Key == "value_out");
    }

    [Fact]
    public async Task Archive_reports_show_each_user_only_their_businesses_stores_years_reports_and_profit()
    {
        using var owner = await ImportedAsync();
        var business = pair.Store.BusinessId;

        // A report user for two reports of 2026-27: those reports, in that year, without profit.
        var (reader, _) = await ArchiveUserAsync(owner, new ArchiveGrantRequest("archive_report_user", business, null, 2026, ["sales-summary", "items"]));
        using (reader)
        {
            var definitions = await reader.GetJsonAsync<List<ReportDefinitionDto>>(Reports);
            Assert.Equal(["sales-summary", "items"], definitions.Select(d => d.Key));
            Assert.False(definitions.Single(d => d.Key == "items").ShowsProfit);
            var items = await reader.GetJsonAsync<ReportDto>($"{Reports}/items?{OctoberRange}");
            Assert.DoesNotContain(items.Columns, c => c.Key is "cost" or "profit" or "margin");
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Reports}/gst-rates?{OctoberRange}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Reports}/sales-summary?from=2026-03-01&to=2026-04-30")).StatusCode); // March is 2025-26
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Reports}/audit-events?{OctoberRange}")).StatusCode);
        }

        // A user limited to one store must choose it.
        var (storeUser, _) = await ArchiveUserAsync(owner, new ArchiveGrantRequest("archive_report_user", business, pair.Store.MainStoreId));
        using (storeUser)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await storeUser.GetAsync($"{Reports}/sales-summary?{OctoberRange}")).StatusCode);
            var mine = await storeUser.GetJsonAsync<ReportDto>($"{Reports}/sales-summary?{OctoberRange}&storeId={pair.Store.MainStoreId}");
            Assert.Equal(238m, Number(mine.Totals!["sales"]));
        }

        // The accountant sees profit; the auditor the audit trail.
        var (accountant, _) = await ArchiveUserAsync(owner, new ArchiveGrantRequest("archive_accountant", business));
        using (accountant)
        {
            var items = await accountant.GetJsonAsync<ReportDto>($"{Reports}/items?{OctoberRange}");
            Assert.Contains(items.Columns, c => c.Key == "profit");
        }

        var (auditor, _) = await ArchiveUserAsync(owner, new ArchiveGrantRequest("archive_auditor", business));
        using (auditor)
        {
            var events = await auditor.GetJsonAsync<ReportDto>($"{Reports}/audit-events?{OctoberRange}");
            Assert.Contains(events.Rows, r => r["event"]?.ToString() == "sales.invoice_issued");
        }

        // Months missing from the archive are named; ranges and groupings are checked.
        var wide = await owner.GetJsonAsync<ReportDto>($"{Reports}/sales-summary?from=2026-08-01&to=2026-10-31");
        Assert.Contains(wide.Notes, n => n.StartsWith("Not in the archive", StringComparison.Ordinal) && n.Contains("2026-08", StringComparison.Ordinal));
        Assert.Equal("report.range_invalid", await (await owner.GetAsync($"{Reports}/sales-summary?from=2026-01-01&to=2027-01-31")).ProblemCodeAsync());
        Assert.Equal("report.grouping_invalid", await (await owner.GetAsync($"{Reports}/sales-summary?{OctoberRange}&by=cashier")).ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{Reports}/no-such-report?{OctoberRange}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/archive/businesses/{Guid.NewGuid()}/reports")).StatusCode);

        // An export is a file, and the archive's audit trail records it.
        var csv = await owner.GetAsync($"{Reports}/gst-rates?{OctoberRange}&format=csv");
        await csv.EnsureSuccessWithBodyAsync();
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        await using var admin = await TestDatabase.OpenAdminAsync(Archive.DatabaseName);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM audit_events WHERE event_type = 'archive.report_exported' AND payload_json::text LIKE '%gst-rates%'", admin);
        Assert.True((long)(await command.ExecuteScalarAsync())! >= 1);
    }

    private static decimal Number(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDecimal(),
        null => 0m,
        _ => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
    };
}
