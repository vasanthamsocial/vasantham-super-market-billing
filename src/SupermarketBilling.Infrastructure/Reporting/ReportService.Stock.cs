using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Infrastructure.Catalog;

namespace SupermarketBilling.Infrastructure.Reporting;

/// <summary>Stock and purchase reports (12b). Stock values are cost, so they need <c>reports.profit</c>; quantities do not.</summary>
public sealed partial class ReportService
{
    internal static readonly ReportDefinitionDto[] StockDefinitions =
    [
        new("stock-summary", "Inventory ledger summary", "Stock", "Opening stock, purchases, sales, other movements and closing stock for the period, from the stock ledger.",
            ["item", "category", "store"], true),
        new("stock-valuation", "Stock valuation", "Stock", "Quantity and FIFO value of stock on the last day of the range.", ["item", "category", "store"], true),
        new("stock-origin", "GST and non-GST origin stock", "Stock",
            "Stock in hand now by where it came from: bought with GST, bought without GST, or other; the total is the consolidated stock.", ["item", "category"], true),
        new("stock-ageing", "Stock ageing", "Stock", "Stock in hand now by how long ago it was received.", [], true),
        new("expiry", "Expiry", "Stock", "Batches in stock that have expired or expire by the last day of the range.", [], true),
        new("negative-stock", "Negative stock", "Stock", "Items sold or issued below zero now in stock, at their last cost.", [], true),
        new("purchases", "Purchases", "Purchases",
            "Goods receipts by purchase type (GST, non-GST, unregistered...), supplier, day or document; the totals are the consolidated purchases.",
            ["classification", "supplier", "day", "document"], false),
        new("purchase-returns", "Purchase returns", "Purchases", "Debit notes with the tax taken back and the stock that left.", [], true),
        new("suppliers", "Supplier balances", "Purchases", "What the business owed each supplier at the start and end of the period, and why it changed.", [], false),
    ];

    // Stock from the ledger

    private sealed class Movement
    {
        public Guid StoreId { get; set; }

        public Guid VariantId { get; set; }

        public Guid? CategoryId { get; set; }

        public string Type { get; set; } = string.Empty;

        public bool Before { get; set; }

        public decimal Quantity { get; set; }

        public decimal Value { get; set; }
    }

    private sealed class MovementSums
    {
        public Guid? Key { get; set; }

        public string Type { get; set; } = string.Empty;

        public bool Before { get; set; }

        public decimal Quantity { get; set; }

        public decimal Value { get; set; }
    }

    private IQueryable<Movement> Ledger(Guid businessId, ReportQuery q, bool fromStart) =>
        from e in db.StockLedger.AsNoTracking()
        join v in db.ProductVariants.AsNoTracking() on e.VariantId equals v.Id
        join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
        where e.BusinessId == businessId && e.BusinessDate <= q.To && (fromStart || e.BusinessDate >= q.From) && (q.StoreId == null || e.StoreId == q.StoreId)
        select new Movement
        {
            StoreId = e.StoreId, VariantId = e.VariantId, CategoryId = p.CategoryId, Type = e.MovementType, Before = e.BusinessDate < q.From, Quantity = e.Quantity,
            Value = e.Value,
        };

    /// <summary>The ledger with the grouping key (item, category or store) worked out in the database.</summary>
    private IQueryable<KeyedMovement> Keyed(Guid businessId, ReportQuery q)
    {
        var ledger = Ledger(businessId, q, fromStart: true);
        return q.By switch
        {
            "category" => ledger.Select(m => new KeyedMovement { Key = m.CategoryId, Type = m.Type, Before = m.Before, Quantity = m.Quantity, Value = m.Value }),
            "store" => ledger.Select(m => new KeyedMovement { Key = m.StoreId, Type = m.Type, Before = m.Before, Quantity = m.Quantity, Value = m.Value }),
            _ => ledger.Select(m => new KeyedMovement { Key = m.VariantId, Type = m.Type, Before = m.Before, Quantity = m.Quantity, Value = m.Value }),
        };
    }

    private sealed class KeyedMovement
    {
        public Guid? Key { get; set; }

        public string Type { get; set; } = string.Empty;

        public bool Before { get; set; }

        public decimal Quantity { get; set; }

        public decimal Value { get; set; }
    }

    private async Task<ReportTable> StockSummaryAsync(Guid businessId, ReportQuery q, bool profit, CancellationToken cancellationToken)
    {
        var grouped = await Keyed(businessId, q).GroupBy(m => new { m.Key, m.Type, m.Before })
            .Select(g => new MovementSums { Key = g.Key.Key, Type = g.Key.Type, Before = g.Key.Before, Quantity = g.Sum(m => m.Quantity), Value = g.Sum(m => m.Value) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await StockNamesAsync(q.By, grouped.Select(g => g.Key), cancellationToken).ConfigureAwait(false);
        var item = q.By is null or "item";
        var table = new ReportTable().Column("name", Heading(q.By), ColumnKinds.Text);
        foreach (var (k, label) in new[] { ("opening", "Opening"), ("purchased", "Purchased"), ("sold", "Sold"), ("other", "Other movements"), ("closing", "Closing") })
        {
            if (item)
            {
                table.Column($"{k}_qty", $"{label} qty", ColumnKinds.Quantity);
            }

            if (profit)
            {
                table.Column($"{k}_value", $"{label} value", ColumnKinds.Money);
            }
        }

        table.Note("Purchased is receipts less purchase returns; sold is sales less customer returns put back in stock; other is opening stock entered, " +
                   "adjustments, damage, wastage, counts and transfers. Closing = opening + purchased - sold + other.")
            .Note("Values are FIFO cost.");
        foreach (var k in grouped.Select(g => g.Key).Distinct().OrderBy(k => Name(names, k, q.By), StringComparer.OrdinalIgnoreCase))
        {
            var mine = grouped.Where(g => g.Key == k).ToList();
            (decimal Q, decimal V) Sum(Func<MovementSums, bool> filter) => (mine.Where(filter).Sum(m => m.Quantity), mine.Where(filter).Sum(m => m.Value));
            var opening = Sum(m => m.Before);
            var purchased = Sum(m => !m.Before && m.Type is MovementTypes.Receipt or MovementTypes.PurchaseReturn);
            var sold = Sum(m => !m.Before && m.Type is MovementTypes.Sale or MovementTypes.SaleReturn);
            var other = Sum(m => !m.Before && m.Type is not (MovementTypes.Receipt or MovementTypes.PurchaseReturn or MovementTypes.Sale or MovementTypes.SaleReturn));
            var cells = new List<(string, object?)> { ("name", Name(names, k, q.By)) };
            foreach (var (col, (qty, value)) in new[]
                     {
                         ("opening", opening), ("purchased", purchased), ("sold", (-sold.Q, -sold.V)), ("other", other),
                         ("closing", (opening.Q + purchased.Q + sold.Q + other.Q, opening.V + purchased.V + sold.V + other.V)),
                     })
            {
                if (item)
                {
                    cells.Add(($"{col}_qty", qty));
                }

                if (profit)
                {
                    cells.Add(($"{col}_value", value));
                }
            }

            table.Row([.. cells]);
        }

        return table;
    }

    private async Task<ReportTable> StockValuationAsync(Guid businessId, ReportQuery q, bool profit, CancellationToken cancellationToken)
    {
        var rows = await Keyed(businessId, q).GroupBy(m => m.Key)
            .Select(g => new MovementSums { Key = g.Key, Quantity = g.Sum(m => m.Quantity), Value = g.Sum(m => m.Value) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await StockNamesAsync(q.By, rows.Select(r => r.Key), cancellationToken).ConfigureAwait(false);
        var item = q.By is null or "item";
        var table = new ReportTable().Column("name", Heading(q.By), ColumnKinds.Text);
        if (item)
        {
            table.Column("quantity", "Quantity", ColumnKinds.Quantity);
        }

        if (profit)
        {
            table.Column("value", "Value (FIFO cost)", ColumnKinds.Money);
            if (item)
            {
                table.Column("unit_cost", "Average cost", ColumnKinds.Money);
            }
        }

        table.Note($"Stock as at the end of {q.To:dd-MM-yyyy}, from every stock movement up to that day.");
        foreach (var r in rows.Where(r => r.Quantity != 0 || r.Value != 0).OrderBy(r => Name(names, r.Key, q.By), StringComparer.OrdinalIgnoreCase))
        {
            var cells = new List<(string, object?)> { ("name", Name(names, r.Key, q.By)) };
            if (item)
            {
                cells.Add(("quantity", r.Quantity));
            }

            if (profit)
            {
                cells.Add(("value", r.Value));
                if (item)
                {
                    cells.Add(("unit_cost", r.Quantity > 0 ? decimal.Round(r.Value / r.Quantity, 2, MidpointRounding.AwayFromZero) : null));
                }
            }

            table.Row([.. cells]);
        }

        return table;
    }

    // Stock now, from the lots

    private sealed class Lot
    {
        public Guid StoreId { get; set; }

        public Guid VariantId { get; set; }

        public Guid? CategoryId { get; set; }

        public Guid? BatchId { get; set; }

        public DateOnly? ExpiresOn { get; set; }

        public string Origin { get; set; } = string.Empty;

        public DateTimeOffset ReceivedAtUtc { get; set; }

        public decimal Quantity { get; set; }

        public decimal UnitCost { get; set; }
    }

    private IQueryable<Lot> Lots(Guid businessId, ReportQuery q) =>
        from l in db.CostLayers.AsNoTracking()
        join v in db.ProductVariants.AsNoTracking() on l.VariantId equals v.Id
        join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
        where l.BusinessId == businessId && l.RemainingQuantity > 0 && (q.StoreId == null || l.StoreId == q.StoreId)
        select new Lot
        {
            StoreId = l.StoreId, VariantId = l.VariantId, CategoryId = p.CategoryId, BatchId = l.BatchId, ExpiresOn = l.ExpiresOn, Origin = l.Origin,
            ReceivedAtUtc = l.ReceivedAtUtc, Quantity = l.RemainingQuantity, UnitCost = l.UnitCost,
        };

    private static decimal LotValue(decimal quantity, decimal unitCost) => decimal.Round(quantity * unitCost, 2, MidpointRounding.AwayFromZero);

    private async Task<ReportTable> StockOriginAsync(Guid businessId, ReportQuery q, bool profit, CancellationToken cancellationToken)
    {
        var lots = await Lots(businessId, q).ToListAsync(cancellationToken).ConfigureAwait(false);
        var category = q.By == "category";
        Func<Lot, Guid?> key = category ? l => l.CategoryId : l => l.VariantId;
        var names = await StockNamesAsync(q.By, lots.Select(key), cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("name", Heading(q.By), ColumnKinds.Text);
        foreach (var (origin, label) in new[] { ("gst", "GST origin"), ("non_gst", "Non-GST origin"), ("other", "Other origin"), ("total", "Consolidated") })
        {
            if (!category)
            {
                table.Column($"{origin}_qty", $"{label} qty", ColumnKinds.Quantity);
            }

            if (profit)
            {
                table.Column($"{origin}_value", $"{label} value", ColumnKinds.Money);
            }
        }

        table.Note("GST origin: bought on a GST tax invoice, an import or reverse charge. Non-GST origin: bought on a bill of supply or from an unregistered supplier. " +
                   "Other: opening stock, adjustments, count gains and purchases whose document is pending. Origin stays with the goods through transfers and returns.")
            .Note("Stock in hand now; the dates do not apply.");
        foreach (var group in lots.GroupBy(key).OrderBy(g => Name(names, g.Key, q.By), StringComparer.OrdinalIgnoreCase))
        {
            var cells = new List<(string, object?)> { ("name", Name(names, group.Key, q.By)) };
            foreach (var (origin, filter) in new (string, Func<Lot, bool>)[]
                     {
                         ("gst", l => l.Origin == StockOrigins.Gst), ("non_gst", l => l.Origin == StockOrigins.NonGst), ("other", l => l.Origin == StockOrigins.Other),
                         ("total", _ => true),
                     })
            {
                if (!category)
                {
                    cells.Add(($"{origin}_qty", group.Where(filter).Sum(l => l.Quantity)));
                }

                if (profit)
                {
                    cells.Add(($"{origin}_value", group.Where(filter).Sum(l => LotValue(l.Quantity, l.UnitCost))));
                }
            }

            table.Row([.. cells]);
        }

        return table;
    }

    private async Task<ReportTable> StockAgeingAsync(Guid businessId, ReportQuery q, bool profit, CancellationToken cancellationToken)
    {
        var lots = await Lots(businessId, q).ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await StockNamesAsync("item", lots.Select(l => (Guid?)l.VariantId), cancellationToken).ConfigureAwait(false);
        var today = BusinessCalendar.Today(clock);
        (string Key, string Label, int From, int To)[] buckets =
            [("d30", "0-30 days", 0, 30), ("d60", "31-60 days", 31, 60), ("d90", "61-90 days", 61, 90), ("d180", "91-180 days", 91, 180), ("older", "Over 180 days", 181, int.MaxValue)];
        var table = new ReportTable().Column("name", "Item", ColumnKinds.Text);
        foreach (var b in buckets)
        {
            table.Column(b.Key, b.Label, ColumnKinds.Quantity);
        }

        table.Column("quantity", "In stock", ColumnKinds.Quantity);
        if (profit)
        {
            table.Column("value", "Value", ColumnKinds.Money).Column("older_value", "Value over 180 days", ColumnKinds.Money);
        }

        table.Note("Age is counted from when each lot came into the store (a transfer starts a new age at the receiving store). Stock in hand now; the dates do not apply.");
        foreach (var group in lots.GroupBy(l => l.VariantId).OrderBy(g => Name(names, g.Key, "item"), StringComparer.OrdinalIgnoreCase))
        {
            int Age(Lot l) => today.DayNumber - DateOnly.FromDateTime(l.ReceivedAtUtc.UtcDateTime).DayNumber;
            var cells = new List<(string, object?)> { ("name", Name(names, group.Key, "item")) };
            cells.AddRange(buckets.Select(b => (b.Key, (object?)group.Where(l => Age(l) >= b.From && Age(l) <= b.To).Sum(l => l.Quantity))));
            cells.Add(("quantity", group.Sum(l => l.Quantity)));
            if (profit)
            {
                cells.Add(("value", group.Sum(l => LotValue(l.Quantity, l.UnitCost))));
                cells.Add(("older_value", group.Where(l => Age(l) > 180).Sum(l => LotValue(l.Quantity, l.UnitCost))));
            }

            table.Row([.. cells]);
        }

        return table;
    }

    private async Task<ReportTable> ExpiryAsync(Guid businessId, ReportQuery q, bool profit, CancellationToken cancellationToken)
    {
        var lots = await Lots(businessId, q).Where(l => l.ExpiresOn != null && l.ExpiresOn <= q.To).ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await StockNamesAsync("item", lots.Select(l => (Guid?)l.VariantId), cancellationToken).ConfigureAwait(false);
        var batchIds = lots.Where(l => l.BatchId != null).Select(l => l.BatchId!.Value).Distinct().ToList();
        var batches = await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.BatchNumber, cancellationToken).ConfigureAwait(false);
        var stores = await StoreNamesAsync(lots.Select(l => l.StoreId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var today = BusinessCalendar.Today(clock);
        var table = new ReportTable().Column("name", "Item", ColumnKinds.Text).Column("batch", "Batch", ColumnKinds.Text).Column("store", "Store", ColumnKinds.Text)
            .Column("expires", "Expires on", ColumnKinds.Date).Column("days_left", "Days left", ColumnKinds.Text).Column("quantity", "In stock", ColumnKinds.Quantity);
        if (profit)
        {
            table.Column("value", "Value", ColumnKinds.Money);
        }

        table.Note($"Batches in stock now that expire on or before {q.To:dd-MM-yyyy}; a negative number of days means already expired.");
        foreach (var group in lots.GroupBy(l => (l.VariantId, l.BatchId, l.StoreId, l.ExpiresOn)).OrderBy(g => g.Key.ExpiresOn)
                     .ThenBy(g => Name(names, g.Key.VariantId, "item"), StringComparer.OrdinalIgnoreCase))
        {
            var cells = new List<(string, object?)>
            {
                ("name", Name(names, group.Key.VariantId, "item")), ("batch", group.Key.BatchId is { } b ? batches.GetValueOrDefault(b) : null),
                ("store", stores.GetValueOrDefault(group.Key.StoreId)), ("expires", group.Key.ExpiresOn),
                ("days_left", (group.Key.ExpiresOn!.Value.DayNumber - today.DayNumber).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("quantity", group.Sum(l => l.Quantity)),
            };
            if (profit)
            {
                cells.Add(("value", group.Sum(l => LotValue(l.Quantity, l.UnitCost))));
            }

            table.Row([.. cells]);
        }

        return table;
    }

    private async Task<ReportTable> NegativeStockAsync(Guid businessId, ReportQuery q, bool profit, CancellationToken cancellationToken)
    {
        var balances = await db.StockBalances.AsNoTracking().Where(b => b.BusinessId == businessId && b.Quantity < 0 && (q.StoreId == null || b.StoreId == q.StoreId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await StockNamesAsync("item", balances.Select(b => (Guid?)b.VariantId), cancellationToken).ConfigureAwait(false);
        var stores = await StoreNamesAsync(balances.Select(b => b.StoreId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("name", "Item", ColumnKinds.Text).Column("store", "Store", ColumnKinds.Text).Column("quantity", "Below zero", ColumnKinds.Quantity);
        if (profit)
        {
            table.Column("last_cost", "Last cost", ColumnKinds.Money).Column("value", "At last cost", ColumnKinds.Money);
        }

        table.Note("Stock sold or issued that was not in the books; the next receipt covers it. Now; the dates do not apply.");
        foreach (var b in balances.OrderBy(b => stores.GetValueOrDefault(b.StoreId)).ThenBy(b => Name(names, b.VariantId, "item"), StringComparer.OrdinalIgnoreCase))
        {
            var cells = new List<(string, object?)> { ("name", Name(names, b.VariantId, "item")), ("store", stores.GetValueOrDefault(b.StoreId)), ("quantity", b.Quantity) };
            if (profit)
            {
                cells.Add(("last_cost", b.LastCost));
                cells.Add(("value", LotValue(b.Quantity, b.LastCost)));
            }

            table.Row([.. cells]);
        }

        return table;
    }

    // Purchases

    private IQueryable<Grn> PostedGrns(Guid businessId, ReportQuery q) =>
        db.Grns.AsNoTracking().Where(g => g.BusinessId == businessId && g.Status == GrnStatus.Posted && g.BusinessDate >= q.From && g.BusinessDate <= q.To
                                          && (q.StoreId == null || g.StoreId == q.StoreId));

    private async Task<ReportTable> PurchasesAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var grns = await PostedGrns(businessId, q).OrderBy(g => g.BusinessDate).ThenBy(g => g.Number).ToListAsync(cancellationToken).ConfigureAwait(false);
        var suppliers = await SupplierNamesAsync(grns.Select(g => g.SupplierId), cancellationToken).ConfigureAwait(false);
        var document = q.By == "document";
        var table = new ReportTable().Column("group", q.By switch
        {
            "supplier" => "Supplier", "day" => "Date", "document" => "Goods receipt", _ => "Purchase type",
        }, q.By == "day" ? ColumnKinds.Date : ColumnKinds.Text);
        if (document)
        {
            table.Column("date", "Date", ColumnKinds.Date).Column("supplier", "Supplier", ColumnKinds.Text).Column("invoice", "Supplier invoice", ColumnKinds.Text)
                .Column("type", "Purchase type", ColumnKinds.Text);
        }
        else
        {
            table.Column("receipts", "Receipts", ColumnKinds.Count);
        }

        table.Column("taxable", "Taxable value", ColumnKinds.Money).Column("cgst", "CGST", ColumnKinds.Money).Column("sgst", "SGST", ColumnKinds.Money)
            .Column("igst", "IGST", ColumnKinds.Money).Column("cess", "Cess", ColumnKinds.Money).Column("invoice_total", "Invoice total", ColumnKinds.Money)
            .Column("itc", "Input tax credit", ColumnKinds.Money).Column("expenses", "Freight and expenses", ColumnKinds.Money)
            .Column("landed", "Landed cost", ColumnKinds.Money)
            .Note("Posted goods receipts only (not those waiting for approval or rejected). Input tax credit is the GST on receipts whose tax can be claimed " +
                  "(a GST business buying on a tax invoice, an import or reverse charge); other GST is part of the cost.")
            .Note("Purchase returns are in their own report.");
        static decimal Tax(Grn g) => g.CgstTotal + g.SgstTotal + g.IgstTotal + g.CessTotal;
        IEnumerable<IGrouping<object, Grn>> groups = q.By switch
        {
            "supplier" => grns.GroupBy(g => (object)suppliers.GetValueOrDefault(g.SupplierId, "?")).OrderBy(g => (string)g.Key, StringComparer.OrdinalIgnoreCase),
            "day" => grns.GroupBy(g => (object)g.BusinessDate).OrderBy(g => (DateOnly)g.Key),
            "document" => grns.GroupBy(g => (object)g.Number),
            _ => grns.GroupBy(g => (object)Label(g.Classification)).OrderBy(g => (string)g.Key, StringComparer.Ordinal),
        };
        foreach (var group in groups)
        {
            var cells = new List<(string, object?)> { ("group", group.Key) };
            if (document)
            {
                var g = group.Single();
                cells.AddRange([("date", g.BusinessDate), ("supplier", suppliers.GetValueOrDefault(g.SupplierId, "?")), ("invoice", g.SupplierInvoiceNumber),
                    ("type", Label(g.Classification))]);
            }
            else
            {
                cells.Add(("receipts", group.Count()));
            }

            cells.AddRange([("taxable", group.Sum(g => g.TaxableTotal)), ("cgst", group.Sum(g => g.CgstTotal)), ("sgst", group.Sum(g => g.SgstTotal)),
                ("igst", group.Sum(g => g.IgstTotal)), ("cess", group.Sum(g => g.CessTotal)), ("invoice_total", group.Sum(g => g.InvoiceTotal)),
                ("itc", group.Where(g => g.TaxRecoverable).Sum(Tax)), ("expenses", group.Sum(g => g.ExpensesTotal)), ("landed", group.Sum(g => g.LandedTotal))]);
            table.Row([.. cells]);
        }

        return table;
    }

    private async Task<ReportTable> PurchaseReturnsAsync(Guid businessId, ReportQuery q, bool profit, CancellationToken cancellationToken)
    {
        var notes = await (from r in db.PurchaseReturns.AsNoTracking()
                           join g in db.Grns.AsNoTracking() on r.GrnId equals g.Id
                           where r.BusinessId == businessId && r.BusinessDate >= q.From && r.BusinessDate <= q.To && (q.StoreId == null || r.StoreId == q.StoreId)
                           orderby r.BusinessDate, r.Number
                           select new { r, Grn = g.Number, g.SupplierInvoiceNumber }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var suppliers = await SupplierNamesAsync(notes.Select(n => n.r.SupplierId), cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("number", "Debit note", ColumnKinds.Text).Column("date", "Date", ColumnKinds.Date)
            .Column("supplier", "Supplier", ColumnKinds.Text).Column("grn", "Against receipt", ColumnKinds.Text).Column("invoice", "Supplier invoice", ColumnKinds.Text)
            .Column("reason", "Reason", ColumnKinds.Text).Column("taxable", "Taxable value", ColumnKinds.Money).Column("tax", "Tax", ColumnKinds.Money)
            .Column("total", "Total", ColumnKinds.Money).Column("itc_reversed", "Input tax credit reversed", ColumnKinds.Money);
        if (profit)
        {
            table.Column("stock_value", "Stock value returned", ColumnKinds.Money);
        }

        foreach (var n in notes)
        {
            var tax = n.r.Cgst + n.r.Sgst + n.r.Igst + n.r.Cess;
            var cells = new List<(string, object?)>
            {
                ("number", n.r.Number), ("date", n.r.BusinessDate), ("supplier", suppliers.GetValueOrDefault(n.r.SupplierId, "?")), ("grn", n.Grn),
                ("invoice", n.SupplierInvoiceNumber), ("reason", n.r.Reason), ("taxable", n.r.Taxable), ("tax", tax), ("total", n.r.Total),
                ("itc_reversed", n.r.TaxRecoverable ? tax : 0m),
            };
            if (profit)
            {
                cells.Add(("stock_value", n.r.StockValue));
            }

            table.Row([.. cells]);
        }

        return table;
    }

    private async Task<ReportTable> SuppliersAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        if (q.StoreId is not null)
        {
            throw AppException.Validation("report.business_only", "Supplier balances are for the whole business; choose all stores.");
        }

        var sums = await db.SupplierLedger.AsNoTracking().Where(e => e.BusinessId == businessId && e.EntryDate <= q.To)
            .GroupBy(e => new { e.PartyId, Before = e.EntryDate < q.From, e.EntryType })
            .Select(g => new { g.Key.PartyId, g.Key.Before, g.Key.EntryType, Amount = g.Sum(e => e.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await SupplierNamesAsync(sums.Select(s => s.PartyId), cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("supplier", "Supplier", ColumnKinds.Text).Column("opening", "Owed at start", ColumnKinds.Money)
            .Column("bills", "Goods received", ColumnKinds.Money).Column("returns", "Debit notes", ColumnKinds.Money).Column("paid", "Paid", ColumnKinds.Money)
            .Column("other", "Other (opening, corrections)", ColumnKinds.Money).Column("closing", "Owed at end", ColumnKinds.Money)
            .Note("Owed at end = owed at start + goods received - debit notes - paid + other. A negative balance is an advance paid to the supplier.");
        foreach (var supplier in sums.Select(s => s.PartyId).Distinct().OrderBy(id => names.GetValueOrDefault(id, "?"), StringComparer.OrdinalIgnoreCase))
        {
            var mine = sums.Where(s => s.PartyId == supplier).ToList();
            decimal Sum(Func<string, bool> type) => mine.Where(s => !s.Before && type(s.EntryType)).Sum(s => s.Amount);
            var opening = mine.Where(s => s.Before).Sum(s => s.Amount);
            var bills = Sum(t => t == LedgerEntryTypes.Grn);
            var returns = -Sum(t => t == LedgerEntryTypes.DebitNote);
            var paid = -Sum(t => t == LedgerEntryTypes.Payment);
            var other = Sum(t => t is not (LedgerEntryTypes.Grn or LedgerEntryTypes.DebitNote or LedgerEntryTypes.Payment));
            table.Row(("supplier", names.GetValueOrDefault(supplier, "?")), ("opening", opening), ("bills", bills), ("returns", returns), ("paid", paid), ("other", other),
                ("closing", opening + bills - returns - paid + other));
        }

        return table;
    }

    // Names

    private static string Heading(string? by) => by switch { "category" => "Category", "store" => "Store", _ => "Item" };

    private static string Name(Dictionary<Guid, string> names, Guid? key, string? by) =>
        key is { } id ? names.GetValueOrDefault(id, "?") : by == "category" ? "No category" : "?";

    private async Task<Dictionary<Guid, string>> StockNamesAsync(string? by, IEnumerable<Guid?> keys, CancellationToken cancellationToken)
    {
        var ids = keys.OfType<Guid>().Distinct().ToList();
        return by switch
        {
            "category" => await db.Categories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken).ConfigureAwait(false),
            "store" => await StoreNamesAsync(ids, cancellationToken).ConfigureAwait(false),
            _ => (await (from v in db.ProductVariants.AsNoTracking()
                         join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                         join u in db.Units.AsNoTracking() on p.BaseUnitId equals u.Id
                         where ids.Contains(v.Id)
                         select new { v.Id, p.Code, Product = p.Name, Variant = v.Name, Unit = u.Code }).ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(x => x.Id, x => $"{x.Code} - {(x.Variant == x.Product || string.IsNullOrEmpty(x.Variant) ? x.Product : $"{x.Product} {x.Variant}")} ({x.Unit})"),
        };
    }

    private async Task<Dictionary<Guid, string>> SupplierNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Suppliers.AsNoTracking().Where(s => list.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code + " - " + (s.TradeName ?? s.Name), cancellationToken)
            .ConfigureAwait(false);
    }
}
