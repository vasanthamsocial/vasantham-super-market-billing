using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Archiving;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Reporting;

namespace SupermarketBilling.Infrastructure.Archiving;

/// <summary>
/// Historical reports on the archive server (spec section 22, D-043): read-only, computed from the archived month
/// records exactly as stored by the store server. The sales summary, GST by rate and HSN use the store's own table
/// builders, so an archived month shows the same figures as the store's report did. A user sees a report only when a
/// grant covers it for the business, the store (a store-limited user must choose that store), every financial year of
/// the range and the report key; cost, profit and margin need the profit permission the same way.
/// </summary>
public sealed class ArchiveReportService(SupermarketBillingDbContext db, AuditRecorder audit, ICurrentUser currentUser, TimeProvider clock)
{
    public const int MaxDays = 366;
    private const int MaxListRows = 10_000;

    private sealed record Definition(ReportDefinitionDto Dto, string Permission);

    private static readonly Definition[] Definitions =
    [
        Report("sales-summary", "Sales summary", "Sales", "Bills, discounts, taxes, returns and net sales.", ["month", "day", "store"]),
        Report("payments", "Payment methods", "Sales", "What was received and refunded by each payment method."),
        Report("gst-rates", "GST by rate", "GST", "Taxable value and tax by rate and supply type, sales less returns."),
        Report("hsn", "HSN summary", "GST", "Quantity, taxable value and tax by HSN/SAC code and rate."),
        Report("b2b", "B2B invoices", "GST", "Invoices and credit notes to GST-registered buyers."),
        Report("items", "Item, category and brand sales", "Sales", "Quantity and net sales, with cost, profit and margin where allowed.", ["item", "category", "brand"], true),
        Report("returns", "Returns and refunds", "Sales", "Credit notes with their reason and how they were refunded."),
        Report("shifts", "Shift reconciliation", "Counters", "Every shift with its expected and counted cash."),
        Report("purchases", "Purchases by supplier", "Purchases", "Goods received and returned to each supplier."),
        Report("stock-movements", "Stock movements", "Stock", "Quantity into and out of stock by item, with value where allowed.", [], true),
        Report("debtor-ledger", "Customer ledger", "Accounts", "Each credit customer's entries in the range and balance after the last."),
        Report("supplier-ledger", "Supplier ledger", "Accounts", "Each supplier's entries in the range and balance after the last."),
        new(new ReportDefinitionDto("audit-events", "Audit trail", "Audit", "Every audited action recorded on the store server.", [], false), ArchivePermissions.Audit),
    ];

    private static Definition Report(string key, string title, string group, string description, string[]? groupings = null, bool profit = false) =>
        new(new ReportDefinitionDto(key, title, group, description, groupings ?? [], profit), ArchivePermissions.Reports);

    /// <summary>The reports this user may run for an archived business (for at least some store and year).</summary>
    public async Task<IReadOnlyList<ReportDefinitionDto>> DefinitionsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await EnsureBusinessAsync(businessId, cancellationToken).ConfigureAwait(false);
        var grants = await GrantsAsync(cancellationToken).ConfigureAwait(false);
        bool Any(string permission, string key) => grants.Any(g => g.Covers(permission, businessId, g.StoreId, null, key));
        var list = Definitions.Where(d => Any(d.Permission, d.Dto.Key))
            .Select(d => d.Dto with { ShowsProfit = d.Dto.ShowsProfit && Any(ArchivePermissions.ReportsProfit, d.Dto.Key) }).ToList();
        if (list.Count == 0)
        {
            throw AppException.Forbidden();
        }

        return list;
    }

    public async Task<ReportDto> RunAsync(Guid businessId, string key, ReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var definition = Definitions.FirstOrDefault(d => d.Dto.Key == key) ?? throw AppException.NotFound("Report");
        if (query.To < query.From || query.To.DayNumber - query.From.DayNumber >= MaxDays)
        {
            throw AppException.Validation("report.range_invalid", $"Choose a range of up to {MaxDays} days, from a date to the same or a later date.");
        }

        var groupings = definition.Dto.Groupings;
        if (groupings.Count > 0 && query.By is not null && !groupings.Contains(query.By))
        {
            throw AppException.Validation("report.grouping_invalid", $"Group this report by {string.Join(", ", groupings)}.");
        }

        await EnsureBusinessAsync(businessId, cancellationToken).ConfigureAwait(false);
        var grants = await GrantsAsync(cancellationToken).ConfigureAwait(false);
        var days = MonthsOf(query.From, query.To).Select(m => m < query.From ? query.From : m).ToList();
        bool Covered(string permission) => days.All(day => grants.Any(g => g.Covers(permission, businessId, query.StoreId, day, key)));
        if (!Covered(definition.Permission))
        {
            throw AppException.Forbidden("Your archive access does not cover this report for this business, store or period.");
        }

        var profit = definition.Dto.ShowsProfit && Covered(ArchivePermissions.ReportsProfit);
        var q = query with { By = groupings.Count > 0 ? query.By ?? groupings[0] : null, CounterId = null, CashierUserId = null };
        var scope = new Scope(businessId, q);
        var table = key switch
        {
            "sales-summary" => await SalesSummaryAsync(scope, cancellationToken).ConfigureAwait(false),
            "payments" => await PaymentsAsync(scope, cancellationToken).ConfigureAwait(false),
            "gst-rates" => await GstRatesAsync(scope, cancellationToken).ConfigureAwait(false),
            "hsn" => await HsnAsync(scope, cancellationToken).ConfigureAwait(false),
            "b2b" => await B2bAsync(scope, cancellationToken).ConfigureAwait(false),
            "items" => await ItemsAsync(scope, profit, cancellationToken).ConfigureAwait(false),
            "returns" => await ReturnsAsync(scope, cancellationToken).ConfigureAwait(false),
            "shifts" => await ShiftsAsync(scope, cancellationToken).ConfigureAwait(false),
            "purchases" => await PurchasesAsync(scope, cancellationToken).ConfigureAwait(false),
            "stock-movements" => await StockMovementsAsync(scope, profit, cancellationToken).ConfigureAwait(false),
            "debtor-ledger" => await LedgerAsync(scope, "debtor_ledger", "debtor_id", "debtors", "Customer", cancellationToken).ConfigureAwait(false),
            "supplier-ledger" => await LedgerAsync(scope, "supplier_ledger", "supplier_id", "suppliers", "Supplier", cancellationToken).ConfigureAwait(false),
            _ => await AuditEventsAsync(scope, cancellationToken).ConfigureAwait(false),
        };
        await CoverageNotesAsync(table, businessId, q.From, q.To, cancellationToken).ConfigureAwait(false);
        return table.Build(key, definition.Dto.Title, q, clock.GetUtcNow());
    }

    /// <summary>Records in the archive's audit trail that a report left the archive as a file.</summary>
    public async Task RecordExportAsync(Guid businessId, ReportDto report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        audit.Record("archive.report_exported", "archive_report", businessId,
            details: new { report = report.Key, from = report.From, to = report.To, by = report.By, rows = report.Rows.Count });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    // Access and coverage

    private async Task<List<ArchiveGrant>> GrantsAsync(CancellationToken cancellationToken) =>
        await db.ArchiveGrants.AsNoTracking().Where(g => g.UserId == currentUser.UserId && g.RevokedAtUtc == null).ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task EnsureBusinessAsync(Guid businessId, CancellationToken cancellationToken)
    {
        if (!await db.ArchiveImports.AnyAsync(i => i.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Archived business");
        }
    }

    private static IEnumerable<DateOnly> MonthsOf(DateOnly from, DateOnly to)
    {
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            yield return m;
        }
    }

    /// <summary>Says which months of the range are not in the archive, or not yet approved by the accountant and the owner.</summary>
    private async Task CoverageNotesAsync(ReportTable table, Guid businessId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var first = new DateOnly(from.Year, from.Month, 1);
        var imports = await db.ArchiveImports.AsNoTracking().Where(i => i.BusinessId == businessId && i.Month >= first && i.Month <= to)
            .Select(i => new { i.Month, i.Status }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var missing = MonthsOf(from, to).Where(m => imports.All(i => i.Month != m)).Select(Months.Format).ToList();
        var pending = imports.Where(i => i.Status != ArchiveImportStatus.Approved).OrderBy(i => i.Month).Select(i => Months.Format(i.Month)).ToList();
        if (missing.Count > 0)
        {
            table.Note($"Not in the archive (nothing from these months is shown): {string.Join(", ", missing)}.");
        }

        if (pending.Count > 0)
        {
            table.Note($"Imported and verified but not yet approved by the accountant and the owner: {string.Join(", ", pending)}.");
        }
    }

    // Queries over the archived records

    /// <summary>The business, the months of the range, the dates, and the store (or all stores).</summary>
    private sealed record Scope(Guid BusinessId, ReportQuery Query)
    {
        public object[] Parameters() =>
        [
            new NpgsqlParameter("b", BusinessId),
            new NpgsqlParameter("m1", new DateOnly(Query.From.Year, Query.From.Month, 1)),
            new NpgsqlParameter("m2", Query.To),
            new NpgsqlParameter("from", Query.From),
            new NpgsqlParameter("to", Query.To),
            new NpgsqlParameter("t1", BusinessCalendar.StartOf(Query.From)),
            new NpgsqlParameter("t2", BusinessCalendar.StartOf(Query.To.AddDays(1))),
            new NpgsqlParameter("all", Query.StoreId is null),
            new NpgsqlParameter("store", (Query.StoreId ?? Guid.Empty).ToString("D", CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>A dataset's records of the months in the range (alias.data is the row as the store server stored it).</summary>
    private static string Records(string alias, string dataset) =>
        $"archive_records {alias} WHERE {alias}.business_id = @b AND {alias}.dataset = '{dataset}' AND {alias}.month BETWEEN @m1 AND @m2";

    /// <summary>A dated document of the range, in the chosen store.</summary>
    private static string Dated(string alias, string dateField = "business_date") =>
        $" AND ({alias}.data->>'{dateField}')::date BETWEEN @from AND @to AND (@all OR {alias}.data->>'store_id' = @store)";

    /// <summary>Lines (or payments...) of the documents in a CTE, from the same months.</summary>
    private static string Children(string alias, string dataset, string parentKey, string parent) =>
        $"JOIN archive_records {alias} ON {alias}.business_id = @b AND {alias}.dataset = '{dataset}' AND {alias}.month BETWEEN @m1 AND @m2 AND {alias}.data->>'{parentKey}' = {parent}.d->>'id'";

    private static string N(string alias, string field) => $"({alias}->>'{field}')::numeric";

    private const string Invoices = "inv AS (SELECT i.data AS d FROM archive_records i WHERE i.business_id = @b AND i.dataset = 'sales_invoices' AND i.month BETWEEN @m1 AND @m2";
    private const string Returns = "ret AS (SELECT r.data AS d FROM archive_records r WHERE r.business_id = @b AND r.dataset = 'sales_returns' AND r.month BETWEEN @m1 AND @m2";

    private static string InvoicesCte => Invoices + Dated("i") + ")";

    private static string ReturnsCte => Returns + Dated("r") + ")";

    private Task<List<T>> QueryAsync<T>(Scope scope, string sql, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<T>(sql, scope.Parameters()).ToListAsync(cancellationToken);

    /// <summary>Names of master records (as in the latest archived month): "code - name", or a user's display name.</summary>
    private async Task<Dictionary<string, string>> NamesAsync(Scope scope, string dataset, CancellationToken cancellationToken)
    {
        var expression = dataset switch
        {
            "users" => "m.data->>'display_name'",
            "debtors" => "m.data->>'code' || ' - ' || COALESCE(NULLIF(m.data->>'trade_name', ''), m.data->>'legal_name')",
            _ => "m.data->>'code' || ' - ' || (m.data->>'name')",
        };
        var rows = await QueryAsync<NameRow>(scope,
            $"SELECT m.record_id AS \"id\", {expression} AS \"name\" FROM archive_masters m WHERE m.business_id = @b AND m.dataset = '{dataset}'", cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => r.Name ?? r.Id, StringComparer.Ordinal);
    }

    private static string Name(Dictionary<string, string> names, string? id) => id is null ? string.Empty : names.GetValueOrDefault(id, "? (not in the archive)");

    private sealed class NameRow
    {
        public string Id { get; set; } = string.Empty;

        public string? Name { get; set; }
    }

    // Sales summary

    private async Task<ReportTable> SalesSummaryAsync(Scope scope, CancellationToken cancellationToken)
    {
        var by = scope.Query.By;
        var key = by switch
        {
            "day" => "d->>'business_date'",
            "store" => "d->>'store_id'",
            _ => "to_char((d->>'business_date')::date, 'YYYY-MM')",
        };
        var sales = await QueryAsync<ReportService.Sums<string>>(scope, $"""
            WITH {InvoicesCte}
            SELECT {key} AS "key", count(*)::int AS "count", sum({N("d", "gross_total")}) AS "gross", sum({N("d", "discount_total")}) AS "discount",
                   sum({N("d", "taxable_total")}) AS "taxable", sum({N("d", "cgst_total")}) AS "cgst", sum({N("d", "sgst_total")}) AS "sgst",
                   sum({N("d", "igst_total")}) AS "igst", sum({N("d", "cess_total")}) AS "cess", sum({N("d", "round_off")}) AS "round_off",
                   sum({N("d", "grand_total")}) AS "total"
            FROM inv GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var returns = await QueryAsync<ReportService.Sums<string>>(scope, $"""
            WITH {ReturnsCte}
            SELECT {key} AS "key", count(*)::int AS "count", 0::numeric AS "gross", 0::numeric AS "discount",
                   sum({N("d", "taxable_total")}) AS "taxable", sum({N("d", "cgst_total")}) AS "cgst", sum({N("d", "sgst_total")}) AS "sgst",
                   sum({N("d", "igst_total")}) AS "igst", sum({N("d", "cess_total")}) AS "cess", sum({N("d", "round_off")}) AS "round_off",
                   sum({N("d", "grand_total")}) AS "total"
            FROM ret GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var table = ReportService.SalesSummaryTable(by switch { "store" => "Store", "day" => "Date", _ => "Month" }, by == "day");
        var stores = by == "store" ? await NamesAsync(scope, "stores", cancellationToken).ConfigureAwait(false) : null;
        object Label(string k) => by switch
        {
            "day" => DateOnly.ParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "store" => Name(stores!, k),
            _ => k,
        };
        foreach (var k in sales.Select(s => s.Key).Union(returns.Select(r => r.Key)).OrderBy(k => by == "store" ? Label(k).ToString() : k, StringComparer.OrdinalIgnoreCase))
        {
            var s = sales.FirstOrDefault(x => x.Key == k);
            var r = returns.FirstOrDefault(x => x.Key == k);
            ReportService.SummaryRow(table, Label(k), s is null ? null : ReportService.Cast(s), r is null ? null : ReportService.Cast(r));
        }

        return table;
    }

    // Payments

    private sealed class MethodRow
    {
        public string Key { get; set; } = string.Empty;

        public decimal Amount { get; set; }
    }

    private async Task<ReportTable> PaymentsAsync(Scope scope, CancellationToken cancellationToken)
    {
        var received = await QueryAsync<MethodRow>(scope, $"""
            WITH {InvoicesCte}
            SELECT p.data->>'method' AS "key", sum({N("p.data", "amount")}) AS "amount" FROM inv {Children("p", "sales_invoice_payments", "invoice_id", "inv")} GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var change = (await QueryAsync<decimal>(scope, $"""WITH {InvoicesCte} SELECT COALESCE(sum({N("d", "change_due")}), 0) AS "Value" FROM inv""", cancellationToken)
            .ConfigureAwait(false)).Single();
        var refunded = await QueryAsync<MethodRow>(scope, $"""
            WITH {ReturnsCte}
            SELECT f.data->>'method' AS "key", sum({N("f.data", "amount")}) AS "amount" FROM ret {Children("f", "sales_return_refunds", "return_id", "ret")} GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("method", "Method", ColumnKinds.Text).Column("received", "Received", ColumnKinds.Money)
            .Column("refunded", "Refunded", ColumnKinds.Money).Column("net", "Net", ColumnKinds.Money)
            .Note("Cash received is after the change given back. On account is sold on credit; credit note is store credit used to pay.");
        foreach (var method in received.Select(r => r.Key).Union(refunded.Select(r => r.Key)).Order(StringComparer.Ordinal))
        {
            var r = received.FirstOrDefault(x => x.Key == method)?.Amount ?? 0m;
            if (method == PaymentMethods.Cash)
            {
                r -= change;
            }

            var f = refunded.FirstOrDefault(x => x.Key == method)?.Amount ?? 0m;
            table.Row(("method", ReportService.Label(method)), ("received", r), ("refunded", f), ("net", r - f));
        }

        return table;
    }

    // GST

    /// <summary>
    /// Sale lines with their bill's tax mode, summed by supply type, rate, HSN and unit (the columns of <paramref name="group"/>,
    /// over the line l).
    /// </summary>
    private static string TaxLines(string group) => $"""
        WITH {InvoicesCte}
        SELECT inv.d->>'tax_mode' AS "tax_mode", l.data->>'supply_type' AS "supply_type", {group}
               sum({N("l.data", "taxable")}) AS "taxable", sum({N("l.data", "cgst")}) AS "cgst", sum({N("l.data", "sgst")}) AS "sgst",
               sum({N("l.data", "igst")}) AS "igst", sum({N("l.data", "cess")}) AS "cess"
        FROM inv {Children("l", "sales_invoice_lines", "invoice_id", "inv")} GROUP BY 1, 2, 3, 4, 5
        """;

    /// <summary>
    /// Return lines (l) with the supply type, rate, HSN and unit of the line they return (o), which may be in any archived month or
    /// not archived at all (then the supply type is empty).
    /// </summary>
    private static string ReturnTaxLines(string group) => $"""
        WITH {ReturnsCte}
        SELECT ret.d->>'tax_mode' AS "tax_mode", COALESCE(o.data->>'supply_type', '') AS "supply_type", {group}
               sum({N("l.data", "taxable")}) AS "taxable", sum({N("l.data", "cgst")}) AS "cgst", sum({N("l.data", "sgst")}) AS "sgst",
               sum({N("l.data", "igst")}) AS "igst", sum({N("l.data", "cess")}) AS "cess"
        FROM ret {Children("l", "sales_return_lines", "return_id", "ret")}
        LEFT JOIN archive_records o ON o.business_id = @b AND o.dataset = 'sales_invoice_lines' AND o.record_id = l.data->>'original_line_id'
        GROUP BY 1, 2, 3, 4, 5
        """;

    private const string SaleRate = """COALESCE((l.data->>'gst_rate_percent')::numeric, 0) AS "rate", '' AS "hsn", '' AS "unit", 0::numeric AS "quantity",""";
    private const string ReturnRate = """COALESCE((o.data->>'gst_rate_percent')::numeric, 0) AS "rate", '' AS "hsn", '' AS "unit", 0::numeric AS "quantity",""";

    // Quantities are the line's own (the unit sold); HSN, unit and rate of a return line come from the line it returns.
    private const string SaleHsn =
        """COALESCE((l.data->>'gst_rate_percent')::numeric, 0) AS "rate", COALESCE(l.data->>'hsn_sac', '') AS "hsn", COALESCE(l.data->>'unit_code', '') AS "unit", sum((l.data->>'quantity')::numeric) AS "quantity",""";
    private const string ReturnHsn =
        """COALESCE((o.data->>'gst_rate_percent')::numeric, 0) AS "rate", COALESCE(o.data->>'hsn_sac', '') AS "hsn", COALESCE(o.data->>'unit_code', '') AS "unit", sum((l.data->>'quantity')::numeric) AS "quantity",""";

    private async Task<ReportTable> GstRatesAsync(Scope scope, CancellationToken cancellationToken)
    {
        var sales = await QueryAsync<ReportService.TaxSums>(scope, TaxLines(SaleRate), cancellationToken).ConfigureAwait(false);
        var returns = await QueryAsync<ReportService.TaxSums>(scope, ReturnTaxLines(ReturnRate), cancellationToken).ConfigureAwait(false);
        var unknown = returns.Where(r => r.SupplyType.Length == 0).ToList();
        var table = ReportService.GstRatesTable(sales, returns.Except(unknown).ToList());
        if (unknown.Count > 0)
        {
            var taxable = unknown.Sum(r => r.Taxable);
            var tax = unknown.Sum(r => r.Cgst + r.Sgst + r.Igst + r.Cess);
            table.Row(("category", "Returns of bills not in the archive"), ("rate", null), ("sales_taxable", 0m), ("sales_cgst", 0m), ("sales_sgst", 0m), ("sales_igst", 0m),
                ("sales_cess", 0m), ("returns_taxable", taxable), ("returns_tax", tax), ("net_taxable", -taxable), ("net_tax", -tax));
            table.Note("Some credit notes return bills from months not in the archive; their rate is unknown, so they are shown on a row of their own.");
        }

        return table;
    }

    private async Task<ReportTable> HsnAsync(Scope scope, CancellationToken cancellationToken)
    {
        var sales = (await QueryAsync<ReportService.TaxSums>(scope, TaxLines(SaleHsn), cancellationToken).ConfigureAwait(false))
            .Where(s => s.TaxMode == TaxRegistrationModes.GstRegular).ToList();
        var returns = (await QueryAsync<ReportService.TaxSums>(scope, ReturnTaxLines(ReturnHsn), cancellationToken).ConfigureAwait(false))
            .Where(r => r.TaxMode == TaxRegistrationModes.GstRegular).ToList();
        var unknown = returns.Where(r => r.SupplyType.Length == 0).ToList();
        var table = ReportService.HsnTable(Merge(sales), Merge(returns.Except(unknown).ToList()));
        if (unknown.Count > 0)
        {
            var (taxable, cgst, sgst, igst, cess) = (unknown.Sum(r => r.Taxable), unknown.Sum(r => r.Cgst), unknown.Sum(r => r.Sgst), unknown.Sum(r => r.Igst), unknown.Sum(r => r.Cess));
            table.Row(("hsn", "Not in the archive"), ("unit", string.Empty), ("rate", null), ("quantity", -unknown.Sum(r => r.Quantity)), ("taxable", -taxable), ("cgst", -cgst),
                ("sgst", -sgst), ("igst", -igst), ("cess", -cess), ("value", -(taxable + cgst + sgst + igst + cess)));
            table.Note("Some credit notes return bills from months not in the archive; their HSN is unknown, so they are shown on a row of their own.");
        }

        return table;

        // Like the store: by HSN, unit and rate (the supply type is not part of the HSN summary).
        static List<ReportService.TaxSums> Merge(List<ReportService.TaxSums> rows) =>
            [.. rows.GroupBy(r => (r.Hsn, r.Unit, r.Rate)).Select(g => new ReportService.TaxSums
            {
                TaxMode = TaxRegistrationModes.GstRegular, Hsn = g.Key.Hsn, Unit = g.Key.Unit, Rate = g.Key.Rate, Quantity = g.Sum(x => x.Quantity), Taxable = g.Sum(x => x.Taxable),
                Cgst = g.Sum(x => x.Cgst), Sgst = g.Sum(x => x.Sgst), Igst = g.Sum(x => x.Igst), Cess = g.Sum(x => x.Cess),
            })];
    }

    private sealed class DocumentRow
    {
        public string Number { get; set; } = string.Empty;

        public DateOnly Date { get; set; }

        public string? Original { get; set; }

        public string? Gstin { get; set; }

        public string? Party { get; set; }

        public string? Place { get; set; }

        public decimal Taxable { get; set; }

        public decimal Cgst { get; set; }

        public decimal Sgst { get; set; }

        public decimal Igst { get; set; }

        public decimal Cess { get; set; }

        public decimal Total { get; set; }
    }

    private static string DocumentTotals(string d) =>
        $"({d}->>'taxable_total')::numeric AS \"taxable\", ({d}->>'cgst_total')::numeric AS \"cgst\", ({d}->>'sgst_total')::numeric AS \"sgst\", "
        + $"({d}->>'igst_total')::numeric AS \"igst\", ({d}->>'cess_total')::numeric AS \"cess\", ({d}->>'grand_total')::numeric AS \"total\"";

    private async Task<ReportTable> B2bAsync(Scope scope, CancellationToken cancellationToken)
    {
        var invoices = await QueryAsync<DocumentRow>(scope, $"""
            WITH {InvoicesCte}
            SELECT d->>'number' AS "number", (d->>'business_date')::date AS "date", NULL AS "original", d->>'buyer_gstin' AS "gstin", d->>'buyer_name' AS "party",
                   d->>'place_of_supply_state_code' AS "place", {DocumentTotals("d")}
            FROM inv WHERE d->>'buyer_gstin' IS NOT NULL AND d->>'tax_mode' = '{TaxRegistrationModes.GstRegular}' ORDER BY 2, 1
            """, cancellationToken).ConfigureAwait(false);
        var notes = await QueryAsync<DocumentRow>(scope, $"""
            WITH {ReturnsCte}
            SELECT ret.d->>'number' AS "number", (ret.d->>'business_date')::date AS "date", ret.d->>'original_invoice_number' AS "original",
                   o.data->>'buyer_gstin' AS "gstin", o.data->>'buyer_name' AS "party", ret.d->>'place_of_supply_state_code' AS "place",
                   {DocumentTotals("ret.d")}
            FROM ret LEFT JOIN archive_records o ON o.business_id = @b AND o.dataset = 'sales_invoices' AND o.record_id = ret.d->>'original_invoice_id'
            WHERE ret.d->>'tax_mode' = '{TaxRegistrationModes.GstRegular}' AND (o.id IS NULL OR o.data->>'buyer_gstin' IS NOT NULL) ORDER BY 2, 1
            """, cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("number", "Number", ColumnKinds.Text).Column("kind", "Document", ColumnKinds.Text).Column("date", "Date", ColumnKinds.Date)
            .Column("gstin", "Buyer GSTIN", ColumnKinds.Text).Column("party", "Buyer", ColumnKinds.Text).Column("place", "Place of supply", ColumnKinds.Text)
            .Column("taxable", "Taxable value", ColumnKinds.Money).Column("cgst", "CGST", ColumnKinds.Money).Column("sgst", "SGST", ColumnKinds.Money)
            .Column("igst", "IGST", ColumnKinds.Money).Column("cess", "Cess", ColumnKinds.Money).Column("total", "Total", ColumnKinds.Money)
            .Note("Credit notes are shown negative, against the original invoice; the totals are net.");
        foreach (var i in invoices)
        {
            table.Row(("number", i.Number), ("kind", "Invoice"), ("date", i.Date), ("gstin", i.Gstin), ("party", i.Party), ("place", Place(i.Place)), ("taxable", i.Taxable),
                ("cgst", i.Cgst), ("sgst", i.Sgst), ("igst", i.Igst), ("cess", i.Cess), ("total", i.Total));
        }

        var unknown = notes.Where(n => n.Gstin is null).ToList();
        foreach (var r in notes.Except(unknown))
        {
            table.Row(("number", r.Number), ("kind", $"Credit note on {r.Original}"), ("date", r.Date), ("gstin", r.Gstin), ("party", r.Party), ("place", Place(r.Place)),
                ("taxable", -r.Taxable), ("cgst", -r.Cgst), ("sgst", -r.Sgst), ("igst", -r.Igst), ("cess", -r.Cess), ("total", -r.Total));
        }

        if (unknown.Count > 0)
        {
            table.Note($"{unknown.Count} credit note(s) return bills from months not in the archive ({string.Join(", ", unknown.Select(u => u.Number))}); "
                       + "whether the buyer was registered is unknown, so they are not listed.");
        }

        return table;

        static string? Place(string? code) => code is null ? null : IndianStates.Describe(code);
    }

    // Items

    private sealed class ItemRow
    {
        public string Key { get; set; } = string.Empty;

        public decimal Quantity { get; set; }

        public decimal Taxable { get; set; }

        public decimal Cost { get; set; }
    }

    private sealed class ProductRow
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Category { get; set; }

        public string? Brand { get; set; }
    }

    private async Task<ReportTable> ItemsAsync(Scope scope, bool profit, CancellationToken cancellationToken)
    {
        var sold = await QueryAsync<ItemRow>(scope, $"""
            WITH {InvoicesCte}
            SELECT l.data->>'product_id' AS "key", sum({N("l.data", "base_quantity")}) AS "quantity", sum({N("l.data", "taxable")}) AS "taxable",
                   sum({N("l.data", "cost_of_goods")}) AS "cost"
            FROM inv {Children("l", "sales_invoice_lines", "invoice_id", "inv")} GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var returned = await QueryAsync<ItemRow>(scope, $"""
            WITH {ReturnsCte}
            SELECT COALESCE(v.data->>'product_id', '?') AS "key", sum({N("l.data", "base_quantity")}) AS "quantity", sum({N("l.data", "taxable")}) AS "taxable",
                   sum({N("l.data", "cost_returned")}) AS "cost"
            FROM ret {Children("l", "sales_return_lines", "return_id", "ret")}
            LEFT JOIN archive_masters v ON v.business_id = @b AND v.dataset = 'product_variants' AND v.record_id = l.data->>'variant_id'
            GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var products = (await QueryAsync<ProductRow>(scope, """
            SELECT p.record_id AS "id", (p.data->>'code') || ' - ' || (p.data->>'name') || COALESCE(' (' || (u.data->>'code') || ')', '') AS "name",
                   p.data->>'category_id' AS "category", p.data->>'brand_id' AS "brand"
            FROM archive_masters p LEFT JOIN archive_masters u ON u.business_id = @b AND u.dataset = 'units' AND u.record_id = p.data->>'base_unit_id'
            WHERE p.business_id = @b AND p.dataset = 'products'
            """, cancellationToken).ConfigureAwait(false)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var by = scope.Query.By;
        var names = by switch
        {
            "category" => await NamesOnlyAsync("categories").ConfigureAwait(false),
            "brand" => await NamesOnlyAsync("brands").ConfigureAwait(false),
            _ => products.ToDictionary(p => p.Key, p => p.Value.Name, StringComparer.Ordinal),
        };
        string? GroupOf(string productId) => by switch
        {
            "category" => products.GetValueOrDefault(productId)?.Category,
            "brand" => products.GetValueOrDefault(productId)?.Brand,
            _ => productId,
        };
        static List<(string? Key, ItemRow Sum)> Group(IEnumerable<ItemRow> rows, Func<string, string?> key) =>
            [.. rows.GroupBy(r => key(r.Key)).Select(g => (g.Key, new ItemRow { Quantity = g.Sum(x => x.Quantity), Taxable = g.Sum(x => x.Taxable), Cost = g.Sum(x => x.Cost) }))];
        var s = Group(sold, GroupOf);
        var r = Group(returned, GroupOf);

        var item = by == "item";
        var table = new ReportTable().Column("name", by switch { "category" => "Category", "brand" => "Brand", _ => "Item" }, ColumnKinds.Text);
        if (item)
        {
            table.Column("sold_qty", "Sold", ColumnKinds.Quantity).Column("returned_qty", "Returned", ColumnKinds.Quantity).Column("net_qty", "Net quantity", ColumnKinds.Quantity);
        }

        table.Column("sales", "Sales (before tax)", ColumnKinds.Money).Column("returns", "Returns (before tax)", ColumnKinds.Money).Column("net_sales", "Net sales", ColumnKinds.Money);
        if (profit)
        {
            table.Column("cost", "Cost of goods", ColumnKinds.Money).Column("profit", "Gross profit", ColumnKinds.Money).Ratio("margin", "Margin", "profit", "net_sales")
                .Note("Cost is what the goods sold cost (FIFO), less the cost of goods returned to stock; returned goods written off stay a cost.");
        }

        table.Note(item ? "Quantities are in each item's base unit." : "Quantities are not added across items with different units.");
        string NameOf(string? k) => k is null ? (by == "brand" ? "No brand" : "No category") : Name(names, k);
        foreach (var k in s.Select(x => x.Key).Union(r.Select(x => x.Key)).OrderBy(k => k is null ? "~" : NameOf(k), StringComparer.OrdinalIgnoreCase))
        {
            var a = s.FirstOrDefault(x => x.Key == k).Sum ?? new ItemRow();
            var b = r.FirstOrDefault(x => x.Key == k).Sum ?? new ItemRow();
            var cells = new List<(string, object?)> { ("name", NameOf(k)) };
            if (item)
            {
                cells.AddRange([("sold_qty", a.Quantity), ("returned_qty", b.Quantity), ("net_qty", a.Quantity - b.Quantity)]);
            }

            cells.AddRange([("sales", a.Taxable), ("returns", b.Taxable), ("net_sales", a.Taxable - b.Taxable)]);
            if (profit)
            {
                var cost = a.Cost - b.Cost;
                cells.AddRange([("cost", cost), ("profit", a.Taxable - b.Taxable - cost)]);
            }

            table.Row([.. cells]);
        }

        return table;

        async Task<Dictionary<string, string>> NamesOnlyAsync(string dataset) =>
            (await QueryAsync<NameRow>(scope, $"SELECT m.record_id AS \"id\", m.data->>'name' AS \"name\" FROM archive_masters m WHERE m.business_id = @b AND m.dataset = '{dataset}'",
                cancellationToken).ConfigureAwait(false)).ToDictionary(n => n.Id, n => n.Name ?? n.Id, StringComparer.Ordinal);
    }

    // Returns and shifts

    private sealed class ReturnRow
    {
        public string Id { get; set; } = string.Empty;

        public string Number { get; set; } = string.Empty;

        public DateOnly Date { get; set; }

        public string? Invoice { get; set; }

        public DateOnly? InvoiceDate { get; set; }

        public string? Cashier { get; set; }

        public string? Reason { get; set; }

        public decimal Taxable { get; set; }

        public decimal Tax { get; set; }

        public decimal Total { get; set; }

        public string? Refunds { get; set; }
    }

    private async Task<ReportTable> ReturnsAsync(Scope scope, CancellationToken cancellationToken)
    {
        var returns = await QueryAsync<ReturnRow>(scope, $"""
            WITH {ReturnsCte}
            SELECT ret.d->>'id' AS "id", ret.d->>'number' AS "number", (ret.d->>'business_date')::date AS "date", ret.d->>'original_invoice_number' AS "invoice",
                   (ret.d->>'original_invoice_date')::date AS "invoice_date", ret.d->>'cashier_user_id' AS "cashier", ret.d->>'reason' AS "reason",
                   {N("ret.d", "taxable_total")} AS "taxable",
                   {N("ret.d", "cgst_total")} + {N("ret.d", "sgst_total")} + {N("ret.d", "igst_total")} + {N("ret.d", "cess_total")} AS "tax",
                   {N("ret.d", "grand_total")} AS "total",
                   (SELECT string_agg((f.data->>'method') || ' ' || to_char({N("f.data", "amount")}, 'FM9999999990.00'), '; ' ORDER BY (f.data->>'refund_order')::int)
                    FROM archive_records f WHERE f.business_id = @b AND f.dataset = 'sales_return_refunds' AND f.month BETWEEN @m1 AND @m2
                      AND f.data->>'return_id' = ret.d->>'id') AS "refunds"
            FROM ret ORDER BY 3, 2
            """, cancellationToken).ConfigureAwait(false);
        var users = await NamesAsync(scope, "users", cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("number", "Credit note", ColumnKinds.Text).Column("date", "Date", ColumnKinds.Date)
            .Column("invoice", "Against bill", ColumnKinds.Text).Column("invoice_date", "Bill date", ColumnKinds.Date).Column("cashier", "By", ColumnKinds.Text)
            .Column("reason", "Reason", ColumnKinds.Text).Column("taxable", "Taxable value", ColumnKinds.Money).Column("tax", "Tax", ColumnKinds.Money)
            .Column("total", "Total", ColumnKinds.Money).Column("refunds", "Refunded as", ColumnKinds.Text);
        foreach (var r in returns)
        {
            var refunds = r.Refunds is null ? string.Empty
                : string.Join("; ", r.Refunds.Split("; ").Select(p => p.Split(' ', 2)).Select(p => ReportService.Label(p[0]) + " " + p[1]));
            table.Row(("number", r.Number), ("date", r.Date), ("invoice", r.Invoice), ("invoice_date", r.InvoiceDate), ("cashier", Name(users, r.Cashier)),
                ("reason", r.Reason), ("taxable", r.Taxable), ("tax", r.Tax), ("total", r.Total), ("refunds", refunds));
        }

        return table;
    }

    private sealed class ShiftRow
    {
        public DateOnly Date { get; set; }

        public string Store { get; set; } = string.Empty;

        public string Counter { get; set; } = string.Empty;

        public string Cashier { get; set; } = string.Empty;

        public DateTimeOffset Opened { get; set; }

        public DateTimeOffset? Closed { get; set; }

        public decimal Float { get; set; }

        public decimal? Expected { get; set; }

        public decimal? Counted { get; set; }

        public decimal? Difference { get; set; }

        public string Status { get; set; } = string.Empty;

        public string? Note { get; set; }

        public string? Reviewer { get; set; }
    }

    private async Task<ReportTable> ShiftsAsync(Scope scope, CancellationToken cancellationToken)
    {
        var shifts = await QueryAsync<ShiftRow>(scope, $"""
            SELECT (s.data->>'business_date')::date AS "date", s.data->>'store_id' AS "store", s.data->>'counter_id' AS "counter", s.data->>'cashier_user_id' AS "cashier",
                   (s.data->>'opened_at_utc')::timestamptz AS "opened", (s.data->>'closed_at_utc')::timestamptz AS "closed", {N("s.data", "opening_float")} AS "float",
                   {N("s.data", "expected_cash")} AS "expected", {N("s.data", "counted_cash")} AS "counted", {N("s.data", "difference")} AS "difference",
                   s.data->>'status' AS "status", s.data->>'close_note' AS "note", s.data->>'reviewed_by_user_id' AS "reviewer"
            FROM {Records("s", "shifts")}{Dated("s")} ORDER BY 1, 5
            """, cancellationToken).ConfigureAwait(false);
        var stores = await NamesAsync(scope, "stores", cancellationToken).ConfigureAwait(false);
        var counters = await NamesAsync(scope, "counters", cancellationToken).ConfigureAwait(false);
        var users = await NamesAsync(scope, "users", cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("date", "Date", ColumnKinds.Date).Column("store", "Store", ColumnKinds.Text).Column("counter", "Counter", ColumnKinds.Text)
            .Column("cashier", "Cashier", ColumnKinds.Text).Column("opened", "Opened", ColumnKinds.DateTime).Column("closed", "Closed", ColumnKinds.DateTime)
            .Column("float", "Opening float", ColumnKinds.Money).Column("expected", "Expected cash", ColumnKinds.Money).Column("counted", "Counted cash", ColumnKinds.Money)
            .Column("difference", "Over (+) / short (-)", ColumnKinds.Money).Column("status", "Status", ColumnKinds.Text).Column("note", "Note", ColumnKinds.Text)
            .Column("reviewed", "Reviewed by", ColumnKinds.Text)
            .Note("Expected cash is the float plus cash sales and receipts less refunds and pay-outs.");
        foreach (var s in shifts)
        {
            table.Row(("date", s.Date), ("store", Name(stores, s.Store)), ("counter", Name(counters, s.Counter)), ("cashier", Name(users, s.Cashier)), ("opened", s.Opened),
                ("closed", s.Closed), ("float", s.Float), ("expected", s.Expected), ("counted", s.Counted), ("difference", s.Difference), ("status", ReportService.Label(s.Status)),
                ("note", s.Note), ("reviewed", s.Reviewer is null ? null : Name(users, s.Reviewer)));
        }

        return table;
    }

    // Purchases and stock

    private sealed class SupplierRow
    {
        public string Key { get; set; } = string.Empty;

        public int Count { get; set; }

        public decimal Taxable { get; set; }

        public decimal Tax { get; set; }

        public decimal Total { get; set; }
    }

    private async Task<ReportTable> PurchasesAsync(Scope scope, CancellationToken cancellationToken)
    {
        var received = await QueryAsync<SupplierRow>(scope, $"""
            SELECT g.data->>'supplier_id' AS "key", count(*)::int AS "count", sum({N("g.data", "taxable_total")}) AS "taxable",
                   sum({N("g.data", "cgst_total")} + {N("g.data", "sgst_total")} + {N("g.data", "igst_total")} + {N("g.data", "cess_total")}) AS "tax",
                   sum({N("g.data", "invoice_total")}) AS "total"
            FROM {Records("g", "grns")}{Dated("g")} AND g.data->>'status' = '{GrnStatus.Posted}' GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var returned = await QueryAsync<SupplierRow>(scope, $"""
            SELECT p.data->>'supplier_id' AS "key", count(*)::int AS "count", sum({N("p.data", "taxable")}) AS "taxable",
                   sum({N("p.data", "cgst")} + {N("p.data", "sgst")} + {N("p.data", "igst")} + {N("p.data", "cess")}) AS "tax", sum({N("p.data", "total")}) AS "total"
            FROM {Records("p", "purchase_returns")}{Dated("p")} GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var suppliers = await NamesAsync(scope, "suppliers", cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("supplier", "Supplier", ColumnKinds.Text).Column("grns", "Goods receipts", ColumnKinds.Count)
            .Column("taxable", "Taxable value", ColumnKinds.Money).Column("tax", "Tax", ColumnKinds.Money).Column("total", "Invoice total", ColumnKinds.Money)
            .Column("returns_count", "Purchase returns", ColumnKinds.Count).Column("returns", "Returned", ColumnKinds.Money).Column("net", "Net purchases", ColumnKinds.Money)
            .Note("Posted goods receipts by their business date; purchase returns by theirs.");
        foreach (var k in received.Select(r => r.Key).Union(returned.Select(r => r.Key)).OrderBy(k => Name(suppliers, k), StringComparer.OrdinalIgnoreCase))
        {
            var g = received.FirstOrDefault(x => x.Key == k) ?? new SupplierRow();
            var p = returned.FirstOrDefault(x => x.Key == k) ?? new SupplierRow();
            table.Row(("supplier", Name(suppliers, k)), ("grns", g.Count), ("taxable", g.Taxable), ("tax", g.Tax), ("total", g.Total), ("returns_count", p.Count),
                ("returns", p.Total), ("net", g.Total - p.Total));
        }

        return table;
    }

    private sealed class MovementRow
    {
        public string Key { get; set; } = string.Empty;

        public decimal QuantityIn { get; set; }

        public decimal QuantityOut { get; set; }

        public decimal ValueIn { get; set; }

        public decimal ValueOut { get; set; }
    }

    private async Task<ReportTable> StockMovementsAsync(Scope scope, bool profit, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<MovementRow>(scope, $"""
            SELECT s.data->>'variant_id' AS "key",
                   sum(greatest({N("s.data", "quantity")}, 0)) AS "quantity_in", sum(greatest(-{N("s.data", "quantity")}, 0)) AS "quantity_out",
                   sum(CASE WHEN {N("s.data", "quantity")} > 0 THEN {N("s.data", "value")} ELSE 0 END) AS "value_in",
                   sum(CASE WHEN {N("s.data", "quantity")} < 0 THEN -{N("s.data", "value")} ELSE 0 END) AS "value_out"
            FROM {Records("s", "stock_ledger")}{Dated("s")} GROUP BY 1
            """, cancellationToken).ConfigureAwait(false);
        var variants = (await QueryAsync<NameRow>(scope, """
            SELECT v.record_id AS "id",
                   (p.data->>'code') || ' - ' || (p.data->>'name')
                   || CASE WHEN v.data->>'name' IS NULL OR v.data->>'name' = '' OR v.data->>'name' = p.data->>'name' THEN '' ELSE ', ' || (v.data->>'name') END
                   || COALESCE(' (' || (u.data->>'code') || ')', '') AS "name"
            FROM archive_masters v
            JOIN archive_masters p ON p.business_id = @b AND p.dataset = 'products' AND p.record_id = v.data->>'product_id'
            LEFT JOIN archive_masters u ON u.business_id = @b AND u.dataset = 'units' AND u.record_id = p.data->>'base_unit_id'
            WHERE v.business_id = @b AND v.dataset = 'product_variants'
            """, cancellationToken).ConfigureAwait(false)).ToDictionary(n => n.Id, n => n.Name ?? n.Id, StringComparer.Ordinal);
        var table = new ReportTable().Column("item", "Item", ColumnKinds.Text).Column("in", "Into stock", ColumnKinds.Quantity)
            .Column("out", "Out of stock", ColumnKinds.Quantity).Column("net", "Net change", ColumnKinds.Quantity);
        if (profit)
        {
            table.Column("value_in", "Value in", ColumnKinds.Money).Column("value_out", "Value out", ColumnKinds.Money).Column("value_net", "Net value", ColumnKinds.Money);
        }

        table.Note("Every movement of the stock ledger in the range (receipts, sales, returns, transfers, adjustments), in each item's base unit.");
        foreach (var r in rows.OrderBy(r => Name(variants, r.Key), StringComparer.OrdinalIgnoreCase))
        {
            var cells = new List<(string, object?)> { ("item", Name(variants, r.Key)), ("in", r.QuantityIn), ("out", r.QuantityOut), ("net", r.QuantityIn - r.QuantityOut) };
            if (profit)
            {
                cells.AddRange([("value_in", r.ValueIn), ("value_out", r.ValueOut), ("value_net", r.ValueIn - r.ValueOut)]);
            }

            table.Row([.. cells]);
        }

        return table;
    }

    // Ledgers and audit

    private sealed class LedgerRow
    {
        public string Key { get; set; } = string.Empty;

        public int Entries { get; set; }

        public decimal Added { get; set; }

        public decimal Removed { get; set; }

        public decimal Balance { get; set; }
    }

    private async Task<ReportTable> LedgerAsync(Scope scope, string dataset, string partyField, string parties, string label, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<LedgerRow>(scope, $"""
            WITH e AS (SELECT l.data AS d FROM {Records("l", dataset)}{Dated("l", "entry_date")}),
                 g AS (SELECT d->>'{partyField}' AS k, count(*)::int AS n, sum(greatest({N("d", "amount")}, 0)) AS added, sum(greatest(-{N("d", "amount")}, 0)) AS removed
                       FROM e GROUP BY 1),
                 last AS (SELECT DISTINCT ON (d->>'{partyField}') d->>'{partyField}' AS k, {N("d", "balance_after")} AS balance
                          FROM e ORDER BY d->>'{partyField}', (d->>'sequence')::bigint DESC)
            SELECT g.k AS "key", g.n AS "entries", g.added AS "added", g.removed AS "removed", last.balance AS "balance" FROM g JOIN last ON last.k = g.k
            """, cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(scope, parties, cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("party", label, ColumnKinds.Text).Column("entries", "Entries", ColumnKinds.Count)
            .Column("added", "Added to the balance", ColumnKinds.Money).Column("removed", "Taken off the balance", ColumnKinds.Money)
            .Column("change", "Change", ColumnKinds.Money).Column("balance", "Balance after the last entry", ColumnKinds.Money)
            .Note("Entries dated in the range. The balance is the party's whole balance after its last entry in the range (all stores), as the store server recorded it.");
        foreach (var r in rows.OrderBy(r => Name(names, r.Key), StringComparer.OrdinalIgnoreCase))
        {
            table.Row(("party", Name(names, r.Key)), ("entries", r.Entries), ("added", r.Added), ("removed", r.Removed), ("change", r.Added - r.Removed), ("balance", r.Balance));
        }

        return table;
    }

    private sealed class AuditRow
    {
        public DateTimeOffset At { get; set; }

        public string Event { get; set; } = string.Empty;

        public string? Entity { get; set; }

        public string? Actor { get; set; }

        public string? Store { get; set; }
    }

    private async Task<ReportTable> AuditEventsAsync(Scope scope, CancellationToken cancellationToken)
    {
        var events = await QueryAsync<AuditRow>(scope, $"""
            SELECT (a.data->>'occurred_at_utc')::timestamptz AS "at", a.data->>'event_type' AS "event", a.data->>'entity_type' AS "entity",
                   a.data->>'actor_user_id' AS "actor", a.data->>'store_id' AS "store"
            FROM {Records("a", "audit_events")}
              AND (a.data->>'occurred_at_utc')::timestamptz >= @t1 AND (a.data->>'occurred_at_utc')::timestamptz < @t2
              AND (@all OR a.data->>'store_id' = @store)
            ORDER BY (a.data->>'sequence')::bigint LIMIT {MaxListRows + 1}
            """, cancellationToken).ConfigureAwait(false);
        var users = await NamesAsync(scope, "users", cancellationToken).ConfigureAwait(false);
        var stores = await NamesAsync(scope, "stores", cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("at", "When", ColumnKinds.DateTime).Column("event", "Action", ColumnKinds.Text).Column("entity", "On", ColumnKinds.Text)
            .Column("actor", "By", ColumnKinds.Text).Column("store", "Store", ColumnKinds.Text);
        foreach (var e in events.Take(MaxListRows))
        {
            table.Row(("at", e.At), ("event", e.Event), ("entity", e.Entity), ("actor", e.Actor is null ? "System" : Name(users, e.Actor)),
                ("store", e.Store is null ? null : Name(stores, e.Store)));
        }

        if (events.Count > MaxListRows)
        {
            table.Note($"Only the first {MaxListRows:N0} actions are shown; choose a shorter range.");
        }

        return table;
    }
}
