using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Dispatch;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Messaging;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Reporting;

/// <summary>
/// The Owner Dashboard (spec section 21). Its figures come from the same reports the Reports page runs, so a tile always
/// equals its report. Each section is worked out on its own: one that fails is shown as unavailable (with why) while the
/// rest still show; sections the user may not see are left out. Business-wide sections (balances, approvals) show only
/// for the whole business.
/// </summary>
public sealed partial class DashboardService(
    ReportService reports,
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    Dispatch.PackingService packing,
    BackupStatusReader backups,
    TimeProvider clock,
    ILogger<DashboardService> logger)
{
    private static readonly TimeSpan DeviceActiveWithin = TimeSpan.FromMinutes(10);

    public async Task<DashboardDto> GetAsync(Guid businessId, Guid? storeId, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(Permissions.ReportsView, businessId, storeId, cancellationToken).ConfigureAwait(false))
        {
            await organisation.RequireAsync(Permissions.ReportsView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        }

        var today = BusinessCalendar.Today(clock);
        var month = new DateOnly(today.Year, today.Month, 1);
        var day = new ReportQuery(today, today, storeId);
        var mtd = new ReportQuery(month, today, storeId);
        var wholeBusiness = storeId is null;
        var sections = new List<DashboardSection>();

        async Task Add(string key, string title, Func<Task<(IReadOnlyList<DashboardMetric> Metrics, ReportDto? Table, string? Report)>> build)
        {
            try
            {
                var (metrics, table, report) = await build().ConfigureAwait(false);
                sections.Add(new DashboardSection(key, title, "ok", null, metrics, table, report));
            }
            catch (AppException e) when (e.Kind is ErrorKind.Forbidden or ErrorKind.NotFound)
            {
                // Not for this user: left out.
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogSectionFailed(logger, key, e.Message);
                sections.Add(new DashboardSection(key, title, "unavailable", "These figures could not be worked out just now.", []));
            }
        }

        await Add("sales-today", "Sales today", () => SalesAsync(businessId, day, cancellationToken)).ConfigureAwait(false);
        await Add("sales-month", "Sales this month", () => SalesAsync(businessId, mtd, cancellationToken)).ConfigureAwait(false);
        await Add("payments-today", "Payments today", async () =>
        {
            var r = await reports.RunAsync(businessId, "payments", day, cancellationToken).ConfigureAwait(false);
            return ([], r, "payments");
        }).ConfigureAwait(false);
        await Add("counters-today", "Counters today", async () =>
        {
            var r = await reports.RunAsync(businessId, "sales-summary", day with { By = "counter" }, cancellationToken).ConfigureAwait(false);
            return ([], Trim(r, ["group", "bills", "net_sales", "returns"], "net_sales", 10), "sales-summary");
        }).ConfigureAwait(false);
        await Add("cashiers-today", "Cashiers today", async () =>
        {
            var r = await reports.RunAsync(businessId, "cashiers", day, cancellationToken).ConfigureAwait(false);
            return ([], Trim(r, ["cashier", "bills", "sales", "average_bill", "overrides", "cash_difference"], "sales", 10), "cashiers");
        }).ConfigureAwait(false);
        await Add("top-items", "Top items this month", async () =>
        {
            var r = await reports.RunAsync(businessId, "items", mtd with { By = "item" }, cancellationToken).ConfigureAwait(false);
            return ([], Trim(r, ["name", "net_qty", "net_sales", "profit", "margin"], "net_sales", 5), "items");
        }).ConfigureAwait(false);
        await Add("top-categories", "Top categories this month", async () =>
        {
            var r = await reports.RunAsync(businessId, "items", mtd with { By = "category" }, cancellationToken).ConfigureAwait(false);
            return ([], Trim(r, ["name", "net_sales", "profit", "margin"], "net_sales", 5), "items");
        }).ConfigureAwait(false);
        await Add("stock", "Stock", () => StockAsync(businessId, storeId, today, cancellationToken)).ConfigureAwait(false);
        await Add("purchases", "Purchases this month", async () =>
        {
            var t = (await reports.RunAsync(businessId, "purchases", mtd, cancellationToken).ConfigureAwait(false)).Totals!;
            return ([Count("Goods receipts", t["receipts"]), Money("Invoice value", t["invoice_total"]), Money("Input tax credit", t["itc"])], null, "purchases");
        }).ConfigureAwait(false);
        if (wholeBusiness)
        {
            await Add("balances", "Balances", () => BalancesAsync(businessId, day, cancellationToken)).ConfigureAwait(false);
        }

        await Add("collections", "Collections", () => CollectionsAsync(businessId, day, mtd, wholeBusiness, cancellationToken)).ConfigureAwait(false);
        await Add("cash", "Cash in the drawers today", () => CashAsync(businessId, day, cancellationToken)).ConfigureAwait(false);
        if (wholeBusiness)
        {
            await Add("approvals", "Waiting for approval", () => ApprovalsAsync(businessId, cancellationToken)).ConfigureAwait(false);
        }

        await Add("dispatch", "Dispatch", () => DispatchAsync(businessId, storeId, day, cancellationToken)).ConfigureAwait(false);
        await Add("backup", "Backup", () => Task.FromResult(Backup())).ConfigureAwait(false);
        await Add("devices", "Counters, devices and messages", () => DevicesAsync(businessId, storeId, cancellationToken)).ConfigureAwait(false);
        return new DashboardDto(businessId, storeId, today, clock.GetUtcNow(), sections);
    }

    // Sections

    private async Task<(IReadOnlyList<DashboardMetric>, ReportDto?, string?)> SalesAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var s = (await reports.RunAsync(businessId, "sales-summary", q with { By = "day" }, cancellationToken).ConfigureAwait(false)).Totals!;
        var bills = Dec(s["bills"]);
        var metrics = new List<DashboardMetric>
        {
            Money("Net sales", s["net_sales"]), Count("Bills", s["bills"]), Money("Average bill", bills == 0 ? null : decimal.Round(Dec(s["sales"]) / bills, 2)),
            Money("Gross sales", s["gross"]), Money("Net revenue (before tax)", s["net_taxable"]), Money("Discounts", s["discount"]),
            Money("Returns", s["returns"]), Money("GST and cess", Dec(s["cgst"]) + Dec(s["sgst"]) + Dec(s["igst"]) + Dec(s["cess"])),
        };
        var items = await reports.RunAsync(businessId, "items", q with { By = "item" }, cancellationToken).ConfigureAwait(false);
        if (items.Totals!.TryGetValue("profit", out var profit))
        {
            metrics.Add(Money("Gross profit", profit));
            metrics.Add(new DashboardMetric("Margin", "percent", items.Totals.GetValueOrDefault("margin") is { } m ? Dec(m) : null));
        }

        return (metrics, null, "sales-summary");
    }

    private async Task<(IReadOnlyList<DashboardMetric>, ReportDto?, string?)> StockAsync(Guid businessId, Guid? storeId, DateOnly today, CancellationToken cancellationToken)
    {
        var now = new ReportQuery(today, today, storeId);
        var metrics = new List<DashboardMetric>();
        var origin = await reports.RunAsync(businessId, "stock-origin", now with { By = "category" }, cancellationToken).ConfigureAwait(false);
        if (origin.Totals!.TryGetValue("total_value", out var value))
        {
            metrics.Add(Money("Stock value (FIFO cost)", value));
            metrics.Add(Money("Bought with GST", origin.Totals["gst_value"]));
            metrics.Add(Money("Bought without GST", origin.Totals["non_gst_value"]));
        }

        var low = await (from b in db.StockBalances.AsNoTracking()
                         join r in db.ReorderLevels.AsNoTracking() on new { b.StoreId, b.VariantId } equals new { r.StoreId, r.VariantId }
                         where b.BusinessId == businessId && (storeId == null || b.StoreId == storeId) && b.Quantity < r.MinimumQuantity
                         select b.Id).CountAsync(cancellationToken).ConfigureAwait(false);
        metrics.Add(Count("Below reorder level", low, low > 0 ? "warning" : null));
        var negative = (await reports.RunAsync(businessId, "negative-stock", now, cancellationToken).ConfigureAwait(false)).Rows.Count;
        metrics.Add(Count("Items below zero", negative, negative > 0 ? "warning" : null));
        var expiry = await reports.RunAsync(businessId, "expiry", now with { To = today.AddDays(30) }, cancellationToken).ConfigureAwait(false);
        var expired = expiry.Rows.Count(r => int.Parse((string)r["days_left"]!, CultureInfo.InvariantCulture) < 0);
        metrics.Add(Count("Batches expired", expired, expired > 0 ? "bad" : null));
        metrics.Add(Count("Batches expiring in 30 days", expiry.Rows.Count - expired, expiry.Rows.Count > expired ? "warning" : null));
        return (metrics, null, "stock-origin");
    }

    private async Task<(IReadOnlyList<DashboardMetric>, ReportDto?, string?)> BalancesAsync(Guid businessId, ReportQuery day, CancellationToken cancellationToken)
    {
        var suppliers = (await reports.RunAsync(businessId, "suppliers", day, cancellationToken).ConfigureAwait(false)).Totals!;
        var ageing = (await reports.RunAsync(businessId, "credit-ageing", day, cancellationToken).ConfigureAwait(false)).Totals!;
        var overdue = Dec(ageing["d30"]) + Dec(ageing["d60"]) + Dec(ageing["d90"]) + Dec(ageing["older"]);
        return ([Money("Owed to suppliers", suppliers["closing"]), Money("Owed by debtors", ageing["balance"]),
            Money("Overdue from debtors", overdue, overdue > 0 ? "warning" : null), Money("Overdue over 90 days", ageing["older"], Dec(ageing["older"]) > 0 ? "bad" : null)],
            null, "credit-ageing");
    }

    private async Task<(IReadOnlyList<DashboardMetric>, ReportDto?, string?)> CollectionsAsync(Guid businessId, ReportQuery day, ReportQuery mtd, bool wholeBusiness,
        CancellationToken cancellationToken)
    {
        var today = (await reports.RunAsync(businessId, "collections", day, cancellationToken).ConfigureAwait(false)).Totals!;
        var month = (await reports.RunAsync(businessId, "collections", mtd, cancellationToken).ConfigureAwait(false)).Totals!;
        var metrics = new List<DashboardMetric> { Money("Collected today", today["net"]), Money("Collected this month", month["net"]) };
        ReportDto? routes = null;
        if (wholeBusiness)
        {
            var promises = await reports.RunAsync(businessId, "promises", day, cancellationToken).ConfigureAwait(false);
            var due = promises.Rows.Count(r => (string?)r["status"] == "Pending");
            metrics.Add(Count("Promises due today, not yet paid", due, due > 0 ? "warning" : null));
            routes = Trim(await reports.RunAsync(businessId, "routes", mtd, cancellationToken).ConfigureAwait(false),
                ["route", "parties", "overdue", "collected", "collected_pct"], "collected", 5);
        }

        return (metrics, routes, "collections");
    }

    private async Task<(IReadOnlyList<DashboardMetric>, ReportDto?, string?)> CashAsync(Guid businessId, ReportQuery day, CancellationToken cancellationToken)
    {
        var shifts = await reports.RunAsync(businessId, "shifts", day, cancellationToken).ConfigureAwait(false);
        var open = shifts.Rows.Count(r => (string?)r["status"] == "Open");
        var differences = shifts.Rows.Where(r => r["difference"] is not null).Select(r => Dec(r["difference"])).ToList();
        var shortest = differences.Count == 0 ? 0m : differences.Min();
        return ([Count("Shifts open", open), Count("Shifts closed", shifts.Rows.Count - open), Money("Over (+) / short (-)", differences.Sum(),
            differences.Sum() != 0 ? "warning" : null), Money("Largest shortage", Math.Min(shortest, 0m), shortest < 0 ? "warning" : null)], null, "shifts");
    }

    private async Task<(IReadOnlyList<DashboardMetric>, ReportDto?, string?)> ApprovalsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(Permissions.ApprovalsView, businessId, null, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden("Approvals are not shown to this user.");
        }

        var now = clock.GetUtcNow();
        var pending = await db.ApprovalRequests.AsNoTracking().Where(a => a.BusinessId == businessId && a.Status == ApprovalStatus.Pending && a.ExpiresAtUtc > now)
            .CountAsync(cancellationToken).ConfigureAwait(false);
        return ([Count("Requests waiting", pending, pending > 0 ? "warning" : null)], null, null);
    }

    private async Task<(IReadOnlyList<DashboardMetric>, ReportDto?, string?)> DispatchAsync(Guid businessId, Guid? storeId, ReportQuery day, CancellationToken cancellationToken)
    {
        var challans = await packing.ListAsync(businessId, storeId, openOnly: true, cancellationToken).ConfigureAwait(false);
        var toPack = challans.Count(c => c.Progress is ChallanProgress.ToPick or ChallanProgress.ToCheck or ChallanProgress.ToPack or ChallanProgress.PartlyPacked);
        var dispatchedToday = (await reports.RunAsync(businessId, "dispatches", day, cancellationToken).ConfigureAwait(false)).Rows.Count;
        var notDelivered = await db.Consignments.AsNoTracking()
            .CountAsync(c => c.BusinessId == businessId && (storeId == null || c.StoreId == storeId) && c.Status == ConsignmentStatus.Dispatched
                             && (c.DeliveryOutcome == DeliveryOutcomes.Failed || c.DeliveryOutcome == DeliveryOutcomes.PartlyDelivered) && c.ReturnRecordedAtUtc == null,
                cancellationToken).ConfigureAwait(false);
        var differences = challans.Count(c => c.HasDifference);
        return ([Count("Bills to pack", toPack, toPack > 0 ? "warning" : null), Count("Packed, ready to send", challans.Count(c => c.ReadyToSend)),
            Count("Dispatched today", dispatchedToday), Count("Deliveries failed, goods not back", notDelivered, notDelivered > 0 ? "warning" : null),
            Count("Differences to settle", differences, differences > 0 ? "warning" : null)], null, "dispatches");
    }

    private (IReadOnlyList<DashboardMetric>, ReportDto?, string?) Backup()
    {
        var b = backups.Read();
        var tone = b.Status switch { "OK" => "good", "FAILED" => "bad", _ => "warning" };
        var metrics = new List<DashboardMetric> { new("Status", "text", Text: b.Message, Tone: tone) };
        if (b.LatestBackupUtc is { } made)
        {
            metrics.Add(new DashboardMetric("Latest backup", "datetime", Text: made.ToString("o", CultureInfo.InvariantCulture)));
            metrics.Add(new DashboardMetric("Restore test", "text",
                Text: b.RestoreTestPassed switch { true => "Passed", false => "Failed", _ => "Not yet tested" }, Tone: b.RestoreTestPassed == false ? "bad" : null));
            metrics.Add(Count("Backups in the folder", b.BackupCount));
        }

        return (metrics, null, null);
    }

    private async Task<(IReadOnlyList<DashboardMetric>, ReportDto?, string?)> DevicesAsync(Guid businessId, Guid? storeId, CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - DeviceActiveWithin;
        var devices = await (from d in db.CounterDevices.AsNoTracking()
                             join c in db.Counters.AsNoTracking() on d.CounterId equals c.Id
                             where d.BusinessId == businessId && d.RevokedAtUtc == null && c.IsActive && (storeId == null || c.StoreId == storeId)
                             select d.LastSeenAtUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        var active = devices.Count(seen => seen >= since);
        var messages = await db.OutboundMessages.AsNoTracking().Where(m => m.BusinessId == businessId)
            .GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var failed = messages.FirstOrDefault(m => m.Key == MessageStatus.Failed)?.Count ?? 0;
        return ([Count("Counter PCs", devices.Count), Count("Active in the last 10 minutes", active, active < devices.Count ? "warning" : "good"),
            Count("Messages waiting to send", messages.FirstOrDefault(m => m.Key == MessageStatus.Queued)?.Count ?? 0),
            Count("Messages failed", failed, failed > 0 ? "warning" : null)], null, null);
    }

    // Helpers

    /// <summary>The first rows by a column, with only the given columns; the totals still cover every row.</summary>
    private static ReportDto Trim(ReportDto report, string[] keys, string orderBy, int take)
    {
        var columns = report.Columns.Where(c => keys.Contains(c.Key)).ToList();
        IReadOnlyDictionary<string, object?> Pick(IReadOnlyDictionary<string, object?> row) =>
            columns.ToDictionary(c => c.Key, c => row.GetValueOrDefault(c.Key));
        var rows = report.Rows.OrderByDescending(r => r.GetValueOrDefault(orderBy) is { } v ? Dec(v) : decimal.MinValue).Take(take).Select(Pick).ToList();
        return report with { Columns = columns, Rows = rows, Totals = report.Totals is null ? null : Pick(report.Totals), Notes = [] };
    }

    private static decimal Dec(object? value) => value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static DashboardMetric Money(string label, object? value, string? tone = null) => new(label, "money", value is null ? null : Dec(value), Tone: tone);

    private static DashboardMetric Count(string label, object? value, string? tone = null) => new(label, "count", value is null ? null : Dec(value), Tone: tone);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard section {Section} failed: {Reason}")]
    private static partial void LogSectionFailed(ILogger logger, string section, string reason);
}
