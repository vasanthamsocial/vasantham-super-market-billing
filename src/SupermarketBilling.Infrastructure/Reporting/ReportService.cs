using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Reporting;

/// <summary>
/// Reports (spec section 23) as tables over a range of business dates, for one store or the whole business (a
/// business-wide report needs a business-wide role). Totals are computed in the database and the totals row is the
/// sum of the rows, so every consolidated figure reconciles with its components. Cost, profit and margin need
/// <c>reports.profit</c>.
/// </summary>
public sealed partial class ReportService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    Accounts.PartyAccountService accounts,
    Dispatch.PackingService packing,
    TimeProvider clock)
{
    public const int MaxDays = 366;

    private static readonly ReportDefinitionDto[] Definitions =
    [
        new("sales-summary", "Sales summary", "Sales", "Bills, discounts, taxes, returns and net sales.", ["day", "store", "counter", "cashier"], false),
        new("payments", "Payment methods", "Sales", "What was received and refunded by each payment method.", [], false),
        new("gst-rates", "GST by rate", "GST", "Taxable value and tax by rate and supply type, sales less returns.", [], false),
        new("hsn", "HSN summary", "GST", "Quantity, taxable value and tax by HSN/SAC code and rate (for GSTR-1).", [], false),
        new("b2b", "B2B invoices", "GST", "Invoices and credit notes to GST-registered buyers.", [], false),
        new("items", "Item, category and brand sales", "Sales", "Quantity and net sales, with cost, profit and margin where allowed.", ["item", "category", "brand"], true),
        new("cashiers", "Cashier performance", "Counters", "Bills, average bill, discounts, price overrides, returns and cash differences.", [], false),
        new("shifts", "Shift reconciliation", "Counters", "Every shift with its expected and counted cash.", [], false),
        new("returns", "Returns and refunds", "Sales", "Credit notes with their reason and how they were refunded.", [], false),
    ];

    /// <summary>Every report (a property, so the definitions of each part of this class are ready whatever the order of initialisation).</summary>
    private static IEnumerable<ReportDefinitionDto> AllDefinitions => Definitions.Concat(StockDefinitions).Concat(AccountDefinitions);

    public async Task<IReadOnlyList<ReportDefinitionDto>> DefinitionsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(businessId, null, cancellationToken).ConfigureAwait(false);
        var profit = await access.HasPermissionAsync(Permissions.ReportsProfit, businessId, null, cancellationToken).ConfigureAwait(false);
        return AllDefinitions.Select(d => d with { ShowsProfit = d.ShowsProfit && profit }).ToList();
    }

    public async Task<ReportDto> RunAsync(Guid businessId, string key, ReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var definition = AllDefinitions.FirstOrDefault(d => d.Key == key) ?? throw AppException.NotFound("Report");
        if (query.To < query.From || query.To.DayNumber - query.From.DayNumber >= MaxDays)
        {
            throw AppException.Validation("report.range_invalid", $"Choose a range of up to {MaxDays} days, from a date to the same or a later date.");
        }

        if (definition.Groupings.Count > 0 && query.By is not null && !definition.Groupings.Contains(query.By))
        {
            throw AppException.Validation("report.grouping_invalid", $"Group this report by {string.Join(", ", definition.Groupings)}.");
        }

        await RequireAsync(businessId, query.StoreId, cancellationToken).ConfigureAwait(false);
        var q = query with { By = definition.Groupings.Count > 0 ? query.By ?? definition.Groupings[0] : null };
        var profit = await access.HasPermissionAsync(Permissions.ReportsProfit, businessId, query.StoreId, cancellationToken).ConfigureAwait(false);
        var table = key switch
        {
            "sales-summary" => await SalesSummaryAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "payments" => await PaymentsAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "gst-rates" => await GstRatesAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "hsn" => await HsnAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "b2b" => await B2bAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "items" => await ItemsAsync(businessId, q, profit, cancellationToken).ConfigureAwait(false),
            "cashiers" => await CashiersAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "shifts" => await ShiftsAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "stock-summary" => await StockSummaryAsync(businessId, q, profit, cancellationToken).ConfigureAwait(false),
            "stock-valuation" => await StockValuationAsync(businessId, q, profit, cancellationToken).ConfigureAwait(false),
            "stock-origin" => await StockOriginAsync(businessId, q, profit, cancellationToken).ConfigureAwait(false),
            "stock-ageing" => await StockAgeingAsync(businessId, q, profit, cancellationToken).ConfigureAwait(false),
            "expiry" => await ExpiryAsync(businessId, q, profit, cancellationToken).ConfigureAwait(false),
            "negative-stock" => await NegativeStockAsync(businessId, q, profit, cancellationToken).ConfigureAwait(false),
            "purchases" => await PurchasesAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "purchase-returns" => await PurchaseReturnsAsync(businessId, q, profit, cancellationToken).ConfigureAwait(false),
            "suppliers" => await SuppliersAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "debtors" => await DebtorsAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "credit-ageing" => await CreditAgeingAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "collections" => await CollectionsAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "collectors" => await CollectorsAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "routes" => await RoutesAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "promises" => await PromisesAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "dispatches" => await DispatchesAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "packing" => await PackingAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            "audit-events" => await AuditEventsAsync(businessId, q, cancellationToken).ConfigureAwait(false),
            _ => await ReturnsAsync(businessId, q, cancellationToken).ConfigureAwait(false),
        };
        return table.Build(key, definition.Title, q, clock.GetUtcNow());
    }

    // Sales summary

    private sealed class Sums<TKey>
    {
        public TKey Key { get; set; } = default!;

        public int Count { get; set; }

        public decimal Gross { get; set; }

        public decimal Discount { get; set; }

        public decimal Taxable { get; set; }

        public decimal Cgst { get; set; }

        public decimal Sgst { get; set; }

        public decimal Igst { get; set; }

        public decimal Cess { get; set; }

        public decimal RoundOff { get; set; }

        public decimal Total { get; set; }
    }

    private async Task<ReportTable> SalesSummaryAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var table = new ReportTable()
            .Column("group", q.By switch { "store" => "Store", "counter" => "Counter", "cashier" => "Cashier", _ => "Date" }, q.By == "day" ? ColumnKinds.Date : ColumnKinds.Text)
            .Column("bills", "Bills", ColumnKinds.Count).Column("gross", "Gross", ColumnKinds.Money).Column("discount", "Discounts", ColumnKinds.Money)
            .Column("taxable", "Taxable value", ColumnKinds.Money).Column("cgst", "CGST", ColumnKinds.Money).Column("sgst", "SGST", ColumnKinds.Money)
            .Column("igst", "IGST", ColumnKinds.Money).Column("cess", "Cess", ColumnKinds.Money).Column("round_off", "Round-off", ColumnKinds.Money)
            .Column("sales", "Sales", ColumnKinds.Money).Column("returns_count", "Credit notes", ColumnKinds.Count).Column("returns", "Returns", ColumnKinds.Money)
            .Column("net_taxable", "Net taxable value", ColumnKinds.Money).Column("net_sales", "Net sales", ColumnKinds.Money)
            .Note("Sales are bills issued in the range; returns are credit notes issued in the range (whatever the date of the bill).");
        if (q.By == "day")
        {
            var (sales, returns) = await SummariesAsync(businessId, q, i => i.BusinessDate, r => r.BusinessDate, cancellationToken).ConfigureAwait(false);
            foreach (var day in sales.Select(s => s.Key).Union(returns.Select(r => r.Key)).Order())
            {
                SummaryRow(table, day, sales.FirstOrDefault(s => s.Key == day), returns.FirstOrDefault(r => r.Key == day));
            }

            return table;
        }

        Expression<Func<SalesInvoice, Guid>> invoiceKey = q.By switch { "store" => i => i.StoreId, "counter" => i => i.CounterId, _ => i => i.CashierUserId };
        Expression<Func<SalesReturn, Guid>> returnKey = q.By switch { "store" => r => r.StoreId, "counter" => r => r.CounterId, _ => r => r.CashierUserId };
        var (bySales, byReturns) = await SummariesAsync(businessId, q, invoiceKey, returnKey, cancellationToken).ConfigureAwait(false);
        var keys = bySales.Select(s => s.Key).Union(byReturns.Select(r => r.Key)).ToList();
        var names = q.By switch
        {
            "store" => await StoreNamesAsync(keys, cancellationToken).ConfigureAwait(false),
            "counter" => await CounterNamesAsync(keys, cancellationToken).ConfigureAwait(false),
            _ => await UserNamesAsync(keys, cancellationToken).ConfigureAwait(false),
        };
        foreach (var k in keys.OrderBy(k => names.GetValueOrDefault(k, "?"), StringComparer.OrdinalIgnoreCase))
        {
            SummaryRow(table, names.GetValueOrDefault(k, "?"), bySales.FirstOrDefault(s => s.Key == k), byReturns.FirstOrDefault(r => r.Key == k));
        }

        return table;
    }

    private static void SummaryRow(ReportTable table, object group, Sums<DateOnly>? sales, Sums<DateOnly>? returns) =>
        SummaryRow(table, group, sales is null ? null : Cast(sales), returns is null ? null : Cast(returns));

    private static void SummaryRow(ReportTable table, object group, Sums<Guid>? sales, Sums<Guid>? returns) =>
        SummaryRow(table, group, sales is null ? null : Cast(sales), returns is null ? null : Cast(returns));

    private static Sums<object> Cast<T>(Sums<T> s) => new()
    {
        Key = s.Key!, Count = s.Count, Gross = s.Gross, Discount = s.Discount, Taxable = s.Taxable, Cgst = s.Cgst, Sgst = s.Sgst, Igst = s.Igst, Cess = s.Cess,
        RoundOff = s.RoundOff, Total = s.Total,
    };

    private static void SummaryRow(ReportTable table, object group, Sums<object>? s, Sums<object>? r)
    {
        s ??= new Sums<object>();
        r ??= new Sums<object>();
        table.Row(("group", group), ("bills", s.Count), ("gross", s.Gross), ("discount", s.Discount), ("taxable", s.Taxable), ("cgst", s.Cgst), ("sgst", s.Sgst),
            ("igst", s.Igst), ("cess", s.Cess), ("round_off", s.RoundOff), ("sales", s.Total), ("returns_count", r.Count), ("returns", r.Total),
            ("net_taxable", s.Taxable - r.Taxable), ("net_sales", s.Total - r.Total));
    }

    private async Task<(List<Sums<TKey>> Sales, List<Sums<TKey>> Returns)> SummariesAsync<TKey>(Guid businessId, ReportQuery q,
        Expression<Func<SalesInvoice, TKey>> invoiceKey, Expression<Func<SalesReturn, TKey>> returnKey, CancellationToken cancellationToken)
    {
        var sales = await Invoices(businessId, q).GroupBy(invoiceKey).Select(g => new Sums<TKey>
        {
            Key = g.Key,
            Count = g.Count(),
            Gross = g.Sum(i => i.GrossTotal),
            Discount = g.Sum(i => i.DiscountTotal),
            Taxable = g.Sum(i => i.TaxableTotal),
            Cgst = g.Sum(i => i.CgstTotal),
            Sgst = g.Sum(i => i.SgstTotal),
            Igst = g.Sum(i => i.IgstTotal),
            Cess = g.Sum(i => i.CessTotal),
            RoundOff = g.Sum(i => i.RoundOff),
            Total = g.Sum(i => i.GrandTotal),
        }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var returns = await Returns(businessId, q).GroupBy(returnKey).Select(g => new Sums<TKey>
        {
            Key = g.Key,
            Count = g.Count(),
            Taxable = g.Sum(r => r.TaxableTotal),
            Cgst = g.Sum(r => r.CgstTotal),
            Sgst = g.Sum(r => r.SgstTotal),
            Igst = g.Sum(r => r.IgstTotal),
            Cess = g.Sum(r => r.CessTotal),
            RoundOff = g.Sum(r => r.RoundOff),
            Total = g.Sum(r => r.GrandTotal),
        }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (sales, returns);
    }

    // Payments

    private async Task<ReportTable> PaymentsAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var invoices = Invoices(businessId, q);
        var received = await (from p in db.SalesInvoicePayments.AsNoTracking()
                              join i in invoices on p.InvoiceId equals i.Id
                              group p by p.Method into g
                              select new { Method = g.Key, Amount = g.Sum(p => p.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var change = await invoices.SumAsync(i => i.ChangeDue, cancellationToken).ConfigureAwait(false);
        var refunded = await (from r in db.SalesReturnRefunds.AsNoTracking()
                              join s in Returns(businessId, q) on r.ReturnId equals s.Id
                              group r by r.Method into g
                              select new { Method = g.Key, Amount = g.Sum(r => r.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("method", "Method", ColumnKinds.Text).Column("received", "Received", ColumnKinds.Money)
            .Column("refunded", "Refunded", ColumnKinds.Money).Column("net", "Net", ColumnKinds.Money)
            .Note("Cash received is after the change given back. On account is sold on credit; credit note is store credit used to pay.");
        foreach (var method in received.Select(r => r.Method).Union(refunded.Select(r => r.Method)).OrderBy(m => m, StringComparer.Ordinal))
        {
            var r = received.FirstOrDefault(x => x.Method == method)?.Amount ?? 0m;
            if (method == PaymentMethods.Cash)
            {
                r -= change;
            }

            var f = refunded.FirstOrDefault(x => x.Method == method)?.Amount ?? 0m;
            table.Row(("method", Label(method)), ("received", r), ("refunded", f), ("net", r - f));
        }

        return table;
    }

    // GST

    private sealed class TaxSums
    {
        public string TaxMode { get; set; } = string.Empty;

        public string SupplyType { get; set; } = string.Empty;

        public decimal Rate { get; set; }

        public string Hsn { get; set; } = string.Empty;

        public string Unit { get; set; } = string.Empty;

        public decimal Quantity { get; set; }

        public decimal Taxable { get; set; }

        public decimal Cgst { get; set; }

        public decimal Sgst { get; set; }

        public decimal Igst { get; set; }

        public decimal Cess { get; set; }
    }

    private IQueryable<TaxSums> SaleLines(Guid businessId, ReportQuery q) =>
        from l in db.SalesInvoiceLines.AsNoTracking()
        join i in Invoices(businessId, q) on l.InvoiceId equals i.Id
        select new TaxSums
        {
            TaxMode = i.TaxMode, SupplyType = l.SupplyType, Rate = l.GstRatePercent, Hsn = l.HsnSac, Unit = l.UnitCode, Quantity = l.Quantity, Taxable = l.Taxable,
            Cgst = l.Cgst, Sgst = l.Sgst, Igst = l.Igst, Cess = l.Cess,
        };

    private IQueryable<TaxSums> ReturnLines(Guid businessId, ReportQuery q) =>
        from l in db.SalesReturnLines.AsNoTracking()
        join r in Returns(businessId, q) on l.ReturnId equals r.Id
        join o in db.SalesInvoiceLines.AsNoTracking() on l.OriginalLineId equals o.Id
        select new TaxSums
        {
            TaxMode = r.TaxMode, SupplyType = o.SupplyType, Rate = o.GstRatePercent, Hsn = o.HsnSac, Unit = o.UnitCode, Quantity = l.Quantity, Taxable = l.Taxable,
            Cgst = l.Cgst, Sgst = l.Sgst, Igst = l.Igst, Cess = l.Cess,
        };

    private static IQueryable<TaxSums> ByRate(IQueryable<TaxSums> lines) =>
        lines.GroupBy(l => new { l.TaxMode, l.SupplyType, l.Rate }).Select(g => new TaxSums
        {
            TaxMode = g.Key.TaxMode, SupplyType = g.Key.SupplyType, Rate = g.Key.Rate, Taxable = g.Sum(l => l.Taxable), Cgst = g.Sum(l => l.Cgst), Sgst = g.Sum(l => l.Sgst),
            Igst = g.Sum(l => l.Igst), Cess = g.Sum(l => l.Cess),
        });

    private async Task<ReportTable> GstRatesAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        // A bill of supply carries no tax whatever the item: such lines make one row per tax mode, not one per rate.
        static List<TaxSums> Merge(List<TaxSums> rows) =>
            [.. rows.GroupBy(r => r.TaxMode == TaxRegistrationModes.GstRegular ? (r.TaxMode, r.SupplyType, r.Rate) : (r.TaxMode, string.Empty, 0m))
                .Select(g => new TaxSums
                {
                    TaxMode = g.Key.TaxMode, SupplyType = g.Key.Item2, Rate = g.Key.Item3, Taxable = g.Sum(x => x.Taxable), Cgst = g.Sum(x => x.Cgst), Sgst = g.Sum(x => x.Sgst),
                    Igst = g.Sum(x => x.Igst), Cess = g.Sum(x => x.Cess),
                })];

        var sales = Merge(await ByRate(SaleLines(businessId, q)).ToListAsync(cancellationToken).ConfigureAwait(false));
        var returns = Merge(await ByRate(ReturnLines(businessId, q)).ToListAsync(cancellationToken).ConfigureAwait(false));
        var table = new ReportTable().Column("category", "Supply", ColumnKinds.Text).Column("rate", "GST rate", ColumnKinds.Percent)
            .Column("sales_taxable", "Sales taxable", ColumnKinds.Money).Column("sales_cgst", "CGST", ColumnKinds.Money).Column("sales_sgst", "SGST", ColumnKinds.Money)
            .Column("sales_igst", "IGST", ColumnKinds.Money).Column("sales_cess", "Cess", ColumnKinds.Money).Column("returns_taxable", "Returns taxable", ColumnKinds.Money)
            .Column("returns_tax", "Returns tax", ColumnKinds.Money).Column("net_taxable", "Net taxable", ColumnKinds.Money).Column("net_tax", "Net tax", ColumnKinds.Money)
            .Note("Bills of supply (composition or not GST registered) carry no tax and are shown apart. Round-off is not part of the taxable value.");
        var keys = sales.Select(s => (s.TaxMode, s.SupplyType, s.Rate)).Union(returns.Select(r => (r.TaxMode, r.SupplyType, r.Rate)))
            .OrderBy(k => k.TaxMode != TaxRegistrationModes.GstRegular).ThenBy(k => k.TaxMode, StringComparer.Ordinal).ThenBy(k => k.SupplyType != SupplyTypes.Taxable)
            .ThenBy(k => k.SupplyType, StringComparer.Ordinal).ThenBy(k => k.Rate);
        foreach (var (mode, supply, rate) in keys)
        {
            var s = sales.FirstOrDefault(x => x.TaxMode == mode && x.SupplyType == supply && x.Rate == rate) ?? new TaxSums();
            var r = returns.FirstOrDefault(x => x.TaxMode == mode && x.SupplyType == supply && x.Rate == rate) ?? new TaxSums();
            var sTax = s.Cgst + s.Sgst + s.Igst + s.Cess;
            var rTax = r.Cgst + r.Sgst + r.Igst + r.Cess;
            table.Row(("category", SupplyLabel(mode, supply)), ("rate", mode == TaxRegistrationModes.GstRegular && supply == SupplyTypes.Taxable ? rate : null),
                ("sales_taxable", s.Taxable), ("sales_cgst", s.Cgst), ("sales_sgst", s.Sgst), ("sales_igst", s.Igst), ("sales_cess", s.Cess), ("returns_taxable", r.Taxable),
                ("returns_tax", rTax), ("net_taxable", s.Taxable - r.Taxable), ("net_tax", sTax - rTax));
        }

        return table;
    }

    private async Task<ReportTable> HsnAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        static IQueryable<TaxSums> ByHsn(IQueryable<TaxSums> lines) =>
            lines.Where(l => l.TaxMode == TaxRegistrationModes.GstRegular).GroupBy(l => new { l.Hsn, l.Unit, l.Rate }).Select(g => new TaxSums
            {
                Hsn = g.Key.Hsn, Unit = g.Key.Unit, Rate = g.Key.Rate, Quantity = g.Sum(l => l.Quantity), Taxable = g.Sum(l => l.Taxable), Cgst = g.Sum(l => l.Cgst),
                Sgst = g.Sum(l => l.Sgst), Igst = g.Sum(l => l.Igst), Cess = g.Sum(l => l.Cess),
            });

        var sales = await ByHsn(SaleLines(businessId, q)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var returns = await ByHsn(ReturnLines(businessId, q)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("hsn", "HSN/SAC", ColumnKinds.Text).Column("unit", "Unit", ColumnKinds.Text).Column("rate", "Rate", ColumnKinds.Percent)
            .Column("quantity", "Quantity", ColumnKinds.Quantity).Column("taxable", "Taxable value", ColumnKinds.Money).Column("cgst", "CGST", ColumnKinds.Money)
            .Column("sgst", "SGST", ColumnKinds.Money).Column("igst", "IGST", ColumnKinds.Money).Column("cess", "Cess", ColumnKinds.Money).Column("value", "Total value", ColumnKinds.Money)
            .Note("Tax invoices only, net of credit notes in the range. Quantities are in the unit sold.");
        foreach (var (hsn, unit, rate) in sales.Select(s => (s.Hsn, s.Unit, s.Rate)).Union(returns.Select(r => (r.Hsn, r.Unit, r.Rate)))
                     .OrderBy(k => k.Hsn, StringComparer.Ordinal).ThenBy(k => k.Unit, StringComparer.Ordinal).ThenBy(k => k.Rate))
        {
            var s = sales.FirstOrDefault(x => x.Hsn == hsn && x.Unit == unit && x.Rate == rate) ?? new TaxSums();
            var r = returns.FirstOrDefault(x => x.Hsn == hsn && x.Unit == unit && x.Rate == rate) ?? new TaxSums();
            var taxable = s.Taxable - r.Taxable;
            var (cgst, sgst, igst, cess) = (s.Cgst - r.Cgst, s.Sgst - r.Sgst, s.Igst - r.Igst, s.Cess - r.Cess);
            table.Row(("hsn", hsn), ("unit", unit), ("rate", rate), ("quantity", s.Quantity - r.Quantity), ("taxable", taxable), ("cgst", cgst), ("sgst", sgst),
                ("igst", igst), ("cess", cess), ("value", taxable + cgst + sgst + igst + cess));
        }

        return table;
    }

    private async Task<ReportTable> B2bAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var invoices = await Invoices(businessId, q).Where(i => i.BuyerGstin != null && i.TaxMode == TaxRegistrationModes.GstRegular)
            .OrderBy(i => i.BusinessDate).ThenBy(i => i.Number)
            .Select(i => new { i.Number, i.BusinessDate, i.BuyerGstin, i.BuyerName, i.PlaceOfSupplyStateCode, i.TaxableTotal, i.CgstTotal, i.SgstTotal, i.IgstTotal, i.CessTotal, i.GrandTotal })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var notes = await (from r in Returns(businessId, q)
                           join i in db.SalesInvoices.AsNoTracking() on r.OriginalInvoiceId equals i.Id
                           where i.BuyerGstin != null && r.TaxMode == TaxRegistrationModes.GstRegular
                           orderby r.BusinessDate, r.Number
                           select new { r.Number, r.BusinessDate, Original = i.Number, i.BuyerGstin, i.BuyerName, r.PlaceOfSupplyStateCode, r.TaxableTotal, r.CgstTotal, r.SgstTotal,
                               r.IgstTotal, r.CessTotal, r.GrandTotal }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("number", "Number", ColumnKinds.Text).Column("kind", "Document", ColumnKinds.Text).Column("date", "Date", ColumnKinds.Date)
            .Column("gstin", "Buyer GSTIN", ColumnKinds.Text).Column("party", "Buyer", ColumnKinds.Text).Column("place", "Place of supply", ColumnKinds.Text)
            .Column("taxable", "Taxable value", ColumnKinds.Money).Column("cgst", "CGST", ColumnKinds.Money).Column("sgst", "SGST", ColumnKinds.Money)
            .Column("igst", "IGST", ColumnKinds.Money).Column("cess", "Cess", ColumnKinds.Money).Column("total", "Total", ColumnKinds.Money)
            .Note("Credit notes are shown negative, against the original invoice; the totals are net.");
        foreach (var i in invoices)
        {
            table.Row(("number", i.Number), ("kind", "Invoice"), ("date", i.BusinessDate), ("gstin", i.BuyerGstin), ("party", i.BuyerName),
                ("place", IndianStates.Describe(i.PlaceOfSupplyStateCode)), ("taxable", i.TaxableTotal), ("cgst", i.CgstTotal), ("sgst", i.SgstTotal), ("igst", i.IgstTotal),
                ("cess", i.CessTotal), ("total", i.GrandTotal));
        }

        foreach (var r in notes)
        {
            table.Row(("number", r.Number), ("kind", $"Credit note on {r.Original}"), ("date", r.BusinessDate), ("gstin", r.BuyerGstin), ("party", r.BuyerName),
                ("place", IndianStates.Describe(r.PlaceOfSupplyStateCode)), ("taxable", -r.TaxableTotal), ("cgst", -r.CgstTotal), ("sgst", -r.SgstTotal),
                ("igst", -r.IgstTotal), ("cess", -r.CessTotal), ("total", -r.GrandTotal));
        }

        return table;
    }

    // Items

    private sealed class ItemFlow
    {
        public Guid ProductId { get; set; }

        public Guid? CategoryId { get; set; }

        public Guid? BrandId { get; set; }

        public decimal Quantity { get; set; }

        public decimal Taxable { get; set; }

        public decimal Cost { get; set; }
    }

    private sealed class ItemSums
    {
        public Guid? Key { get; set; }

        public decimal Quantity { get; set; }

        public decimal Taxable { get; set; }

        public decimal Cost { get; set; }
    }

    private async Task<ReportTable> ItemsAsync(Guid businessId, ReportQuery q, bool profit, CancellationToken cancellationToken)
    {
        var sold = from l in db.SalesInvoiceLines.AsNoTracking()
                   join i in Invoices(businessId, q) on l.InvoiceId equals i.Id
                   join p in db.Products.AsNoTracking() on l.ProductId equals p.Id
                   select new ItemFlow { ProductId = p.Id, CategoryId = p.CategoryId, BrandId = p.BrandId, Quantity = l.BaseQuantity, Taxable = l.Taxable, Cost = l.CostOfGoods };
        var returned = from l in db.SalesReturnLines.AsNoTracking()
                       join ret in Returns(businessId, q) on l.ReturnId equals ret.Id
                       join o in db.SalesInvoiceLines.AsNoTracking() on l.OriginalLineId equals o.Id
                       join p in db.Products.AsNoTracking() on o.ProductId equals p.Id
                       select new ItemFlow { ProductId = p.Id, CategoryId = p.CategoryId, BrandId = p.BrandId, Quantity = l.BaseQuantity, Taxable = l.Taxable, Cost = l.CostReturned };
        Expression<Func<ItemFlow, Guid?>> key = q.By switch { "category" => f => f.CategoryId, "brand" => f => f.BrandId, _ => f => f.ProductId };
        var s = await Group(sold, key).ToListAsync(cancellationToken).ConfigureAwait(false);
        var r = await Group(returned, key).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = s.Select(x => x.Key).Union(r.Select(x => x.Key)).OfType<Guid>().ToList();
        var names = q.By switch
        {
            "category" => await db.Categories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken).ConfigureAwait(false),
            "brand" => await db.Brands.AsNoTracking().Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken).ConfigureAwait(false),
            _ => await (from p in db.Products.AsNoTracking()
                        join u in db.Units.AsNoTracking() on p.BaseUnitId equals u.Id
                        where ids.Contains(p.Id)
                        select new { p.Id, Name = p.Code + " - " + p.Name + " (" + u.Code + ")" }).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken).ConfigureAwait(false),
        };
        var item = q.By == "item";
        var table = new ReportTable().Column("name", q.By switch { "category" => "Category", "brand" => "Brand", _ => "Item" }, ColumnKinds.Text);
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
        foreach (var k in s.Select(x => x.Key).Union(r.Select(x => x.Key)).OrderBy(k => k is { } id ? names.GetValueOrDefault(id, "?") : "~", StringComparer.OrdinalIgnoreCase))
        {
            var a = s.FirstOrDefault(x => x.Key == k) ?? new ItemSums();
            var b = r.FirstOrDefault(x => x.Key == k) ?? new ItemSums();
            var name = k is { } id ? names.GetValueOrDefault(id, "?") : q.By == "brand" ? "No brand" : "No category";
            var cells = new List<(string, object?)> { ("name", name) };
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
    }

    private static IQueryable<ItemSums> Group(IQueryable<ItemFlow> flows, Expression<Func<ItemFlow, Guid?>> key) =>
        flows.GroupBy(key).Select(g => new ItemSums { Key = g.Key, Quantity = g.Sum(f => f.Quantity), Taxable = g.Sum(f => f.Taxable), Cost = g.Sum(f => f.Cost) });

    // Counters and cashiers

    private async Task<ReportTable> CashiersAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var invoices = Invoices(businessId, q);
        var sales = await invoices.GroupBy(i => i.CashierUserId)
            .Select(g => new { g.Key, Bills = g.Count(), Sales = g.Sum(i => i.GrandTotal), Discounts = g.Sum(i => i.DiscountTotal) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var overrides = await (from l in db.SalesInvoiceLines.AsNoTracking()
                               join i in invoices on l.InvoiceId equals i.Id
                               where l.RateType == SaleRateTypes.Override || l.RateType == SaleRateTypes.OverrideSelf
                               group l by i.CashierUserId into g
                               select new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var returns = await Returns(businessId, q).GroupBy(r => r.CashierUserId).Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(r => r.GrandTotal) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var shifts = await Shifts(businessId, q).Where(s => s.Difference != null).GroupBy(s => s.CashierUserId)
            .Select(g => new { g.Key, Count = g.Count(), Difference = g.Sum(s => s.Difference!.Value) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = sales.Select(x => x.Key).Union(returns.Select(x => x.Key)).Union(shifts.Select(x => x.Key)).ToList();
        var names = await UserNamesAsync(ids, cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("cashier", "Cashier", ColumnKinds.Text).Column("bills", "Bills", ColumnKinds.Count).Column("sales", "Sales", ColumnKinds.Money)
            .Average("average_bill", "Average bill", "sales", "bills").Column("discounts", "Discounts", ColumnKinds.Money)
            .Column("overrides", "Price overrides", ColumnKinds.Count).Column("returns_count", "Credit notes", ColumnKinds.Count).Column("returns", "Returns", ColumnKinds.Money)
            .Column("shifts", "Shifts closed", ColumnKinds.Count).Column("cash_difference", "Cash over (+) / short (-)", ColumnKinds.Money)
            .Note("Price overrides are lines billed at a price other than the price list.");
        foreach (var id in ids.OrderBy(id => names.GetValueOrDefault(id, "?"), StringComparer.OrdinalIgnoreCase))
        {
            var s = sales.FirstOrDefault(x => x.Key == id);
            var r = returns.FirstOrDefault(x => x.Key == id);
            var h = shifts.FirstOrDefault(x => x.Key == id);
            table.Row(("cashier", names.GetValueOrDefault(id, "?")), ("bills", s?.Bills ?? 0), ("sales", s?.Sales ?? 0m), ("discounts", s?.Discounts ?? 0m),
                ("overrides", overrides.FirstOrDefault(x => x.Key == id)?.Count ?? 0), ("returns_count", r?.Count ?? 0), ("returns", r?.Total ?? 0m),
                ("shifts", h?.Count ?? 0), ("cash_difference", h?.Difference ?? 0m));
        }

        return table;
    }

    private async Task<ReportTable> ShiftsAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var shifts = await Shifts(businessId, q).OrderBy(s => s.BusinessDate).ThenBy(s => s.OpenedAtUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        var stores = await StoreNamesAsync(shifts.Select(s => s.StoreId).ToList(), cancellationToken).ConfigureAwait(false);
        var counters = await CounterNamesAsync(shifts.Select(s => s.CounterId).ToList(), cancellationToken).ConfigureAwait(false);
        var users = await UserNamesAsync(shifts.Select(s => s.CashierUserId).Concat(shifts.Where(s => s.ReviewedByUserId != null).Select(s => s.ReviewedByUserId!.Value))
            .ToList(), cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("date", "Date", ColumnKinds.Date).Column("store", "Store", ColumnKinds.Text).Column("counter", "Counter", ColumnKinds.Text)
            .Column("cashier", "Cashier", ColumnKinds.Text).Column("opened", "Opened", ColumnKinds.DateTime).Column("closed", "Closed", ColumnKinds.DateTime)
            .Column("float", "Opening float", ColumnKinds.Money).Column("expected", "Expected cash", ColumnKinds.Money).Column("counted", "Counted cash", ColumnKinds.Money)
            .Column("difference", "Over (+) / short (-)", ColumnKinds.Money).Column("status", "Status", ColumnKinds.Text).Column("note", "Note", ColumnKinds.Text)
            .Column("reviewed", "Reviewed by", ColumnKinds.Text)
            .Note("Expected cash is the float plus cash sales and receipts less refunds and pay-outs; open shifts have no count yet.");
        foreach (var s in shifts)
        {
            table.Row(("date", s.BusinessDate), ("store", stores.GetValueOrDefault(s.StoreId, "?")), ("counter", counters.GetValueOrDefault(s.CounterId, "?")),
                ("cashier", users.GetValueOrDefault(s.CashierUserId, "?")), ("opened", s.OpenedAtUtc), ("closed", s.ClosedAtUtc), ("float", s.OpeningFloat),
                ("expected", s.ExpectedCash), ("counted", s.CountedCash), ("difference", s.Difference), ("status", Label(s.Status)), ("note", s.CloseNote),
                ("reviewed", s.ReviewedByUserId is { } r ? users.GetValueOrDefault(r) : null));
        }

        return table;
    }

    private async Task<ReportTable> ReturnsAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var returns = await Returns(businessId, q).OrderBy(r => r.BusinessDate).ThenBy(r => r.Number).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = returns.Select(r => r.Id).ToList();
        var refunds = await db.SalesReturnRefunds.AsNoTracking().Where(r => ids.Contains(r.ReturnId)).OrderBy(r => r.RefundOrder).ToListAsync(cancellationToken).ConfigureAwait(false);
        var users = await UserNamesAsync(returns.Select(r => r.CashierUserId).ToList(), cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("number", "Credit note", ColumnKinds.Text).Column("date", "Date", ColumnKinds.Date)
            .Column("invoice", "Against bill", ColumnKinds.Text).Column("invoice_date", "Bill date", ColumnKinds.Date).Column("cashier", "By", ColumnKinds.Text)
            .Column("reason", "Reason", ColumnKinds.Text).Column("taxable", "Taxable value", ColumnKinds.Money).Column("tax", "Tax", ColumnKinds.Money)
            .Column("total", "Total", ColumnKinds.Money).Column("refunds", "Refunded as", ColumnKinds.Text);
        foreach (var r in returns)
        {
            table.Row(("number", r.Number), ("date", r.BusinessDate), ("invoice", r.OriginalInvoiceNumber), ("invoice_date", r.OriginalInvoiceDate),
                ("cashier", users.GetValueOrDefault(r.CashierUserId, "?")), ("reason", r.Reason), ("taxable", r.TaxableTotal),
                ("tax", r.CgstTotal + r.SgstTotal + r.IgstTotal + r.CessTotal), ("total", r.GrandTotal),
                ("refunds", string.Join("; ", refunds.Where(f => f.ReturnId == r.Id).Select(f => $"{Label(f.Method)} {f.Amount:0.00}"))));
        }

        return table;
    }

    // Sources and names

    private IQueryable<SalesInvoice> Invoices(Guid businessId, ReportQuery q) =>
        db.SalesInvoices.AsNoTracking().Where(i => i.BusinessId == businessId && i.BusinessDate >= q.From && i.BusinessDate <= q.To
                                                   && (q.StoreId == null || i.StoreId == q.StoreId) && (q.CounterId == null || i.CounterId == q.CounterId)
                                                   && (q.CashierUserId == null || i.CashierUserId == q.CashierUserId));

    private IQueryable<SalesReturn> Returns(Guid businessId, ReportQuery q) =>
        db.SalesReturns.AsNoTracking().Where(r => r.BusinessId == businessId && r.BusinessDate >= q.From && r.BusinessDate <= q.To
                                                  && (q.StoreId == null || r.StoreId == q.StoreId) && (q.CounterId == null || r.CounterId == q.CounterId)
                                                  && (q.CashierUserId == null || r.CashierUserId == q.CashierUserId));

    private IQueryable<Shift> Shifts(Guid businessId, ReportQuery q) =>
        db.Shifts.AsNoTracking().Where(s => s.BusinessId == businessId && s.BusinessDate >= q.From && s.BusinessDate <= q.To
                                            && (q.StoreId == null || s.StoreId == q.StoreId) && (q.CounterId == null || s.CounterId == q.CounterId)
                                            && (q.CashierUserId == null || s.CashierUserId == q.CashierUserId));

    private Task<Dictionary<Guid, string>> StoreNamesAsync(List<Guid> ids, CancellationToken cancellationToken) =>
        db.Stores.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code + " - " + s.Name, cancellationToken);

    private Task<Dictionary<Guid, string>> CounterNamesAsync(List<Guid> ids, CancellationToken cancellationToken) =>
        db.Counters.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code + " - " + c.Name, cancellationToken);

    private Task<Dictionary<Guid, string>> UserNamesAsync(List<Guid> ids, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

    private static string SupplyLabel(string mode, string supply) => mode switch
    {
        TaxRegistrationModes.GstComposition => "Composition (bill of supply)",
        TaxRegistrationModes.NotGstRegistered => "Not GST registered (bill of supply)",
        _ => supply switch
        {
            SupplyTypes.Exempt => "Exempt",
            SupplyTypes.NilRated => "Nil rated",
            SupplyTypes.NonGst => "Non-GST",
            _ => "Taxable",
        },
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["UPI"] = "UPI",
        ["GST_TAX_INVOICE"] = "GST tax invoice",
        ["UNREGISTERED"] = "Unregistered supplier",
        ["PENDING_DOCUMENT"] = "Document pending",
        ["STORE_CREDIT"] = "Store credit",
    };

    /// <summary>"ON_ACCOUNT" to "On account" (with a few names spelt out).</summary>
    private static string Label(string code) =>
        Labels.TryGetValue(code, out var label) ? label : code.Length == 0 ? code : code[0] + code[1..].Replace('_', ' ').ToLowerInvariant();

    private async Task RequireAsync(Guid businessId, Guid? storeId, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(Permissions.ReportsView, businessId, storeId, cancellationToken).ConfigureAwait(false))
        {
            await organisation.RequireAsync(Permissions.ReportsView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        }
    }
}
