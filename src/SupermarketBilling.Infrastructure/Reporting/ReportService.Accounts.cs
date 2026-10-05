using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Dispatch;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Dispatch;

namespace SupermarketBilling.Infrastructure.Reporting;

/// <summary>Receivable, collection, dispatch, packing and audit reports (12c).</summary>
public sealed partial class ReportService
{
    internal static readonly ReportDefinitionDto[] AccountDefinitions =
    [
        new("debtors", "Debtor balances", "Receivables", "What each debtor owed at the start and end of the period, and why it changed.", [], false),
        new("credit-ageing", "Credit ageing", "Receivables", "Unpaid bills of each debtor by how long they are overdue, now.", [], false),
        new("collections", "Collections", "Collections", "Money received from debtors, less receipts reversed.", ["day", "collector", "method", "route", "store"], false),
        new("collectors", "Collector performance", "Collections", "Parties planned, visits, receipts, promises and handover differences of each collector.", [], false),
        new("routes", "Route performance", "Collections", "Parties, what they owe now and what was collected on each route.", [], false),
        new("promises", "Promised payments", "Collections", "Promises falling due in the period: kept, broken, pending or cancelled.", [], false),
        new("dispatches", "Lorry dispatch and LR/GR register", "Dispatch",
            "Dispatches with LR/GR, packages and freight; by lorry service for the freight summary.", ["document", "transporter", "day"], false),
        new("packing", "Packing", "Dispatch", "Packing challans with where their goods are and any difference to settle.", [], false),
        new("audit-events", "Audit and security events", "Audit", "What was done, by whom, when (needs the audit permission).", ["event", "user", "detail"], false),
    ];

    private static void RequireBusinessWide(ReportQuery q, string what)
    {
        if (q.StoreId is not null)
        {
            throw AppException.Validation("report.business_only", $"{what} are for the whole business; choose all stores.");
        }
    }

    // Debtors

    private async Task<ReportTable> DebtorsAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        RequireBusinessWide(q, "Debtor balances");
        var sums = await db.DebtorLedger.AsNoTracking().Where(e => e.BusinessId == businessId && e.EntryDate <= q.To)
            .GroupBy(e => new { e.PartyId, Before = e.EntryDate < q.From, e.EntryType })
            .Select(g => new { g.Key.PartyId, g.Key.Before, g.Key.EntryType, Amount = g.Sum(e => e.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var debtors = await DebtorNamesAsync(sums.Select(s => s.PartyId), cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("debtor", "Debtor", ColumnKinds.Text).Column("opening", "Owed at start", ColumnKinds.Money)
            .Column("sales", "Sold on account", ColumnKinds.Money).Column("received", "Received", ColumnKinds.Money).Column("reversed", "Receipts reversed", ColumnKinds.Money)
            .Column("credit_notes", "Credit notes", ColumnKinds.Money).Column("other", "Other (opening, corrections)", ColumnKinds.Money)
            .Column("closing", "Owed at end", ColumnKinds.Money)
            .Note("Owed at end = owed at start + sold on account - received + receipts reversed - credit notes + other. A negative balance is an advance.");
        foreach (var debtor in sums.Select(s => s.PartyId).Distinct().OrderBy(id => debtors.GetValueOrDefault(id, "?"), StringComparer.OrdinalIgnoreCase))
        {
            var mine = sums.Where(s => s.PartyId == debtor).ToList();
            decimal Sum(Func<string, bool> type) => mine.Where(s => !s.Before && type(s.EntryType)).Sum(s => s.Amount);
            var opening = mine.Where(s => s.Before).Sum(s => s.Amount);
            var sales = Sum(t => t == LedgerEntryTypes.Invoice);
            var received = -Sum(t => t == LedgerEntryTypes.Receipt);
            var reversed = Sum(t => t == LedgerEntryTypes.ReceiptReversal);
            var credits = -Sum(t => t == LedgerEntryTypes.CreditNote);
            var other = Sum(t => t is not (LedgerEntryTypes.Invoice or LedgerEntryTypes.Receipt or LedgerEntryTypes.ReceiptReversal or LedgerEntryTypes.CreditNote));
            table.Row(("debtor", debtors.GetValueOrDefault(debtor, "?")), ("opening", opening), ("sales", sales), ("received", received), ("reversed", reversed),
                ("credit_notes", credits), ("other", other), ("closing", opening + sales - received + reversed - credits + other));
        }

        return table;
    }

    private async Task<ReportTable> CreditAgeingAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        RequireBusinessWide(q, "Credit ageing figures");
        var today = BusinessCalendar.Today(clock);
        // Each entry with what is left of it: unpaid (a charge) or unapplied (a payment, an advance).
        var open = await (from e in db.DebtorLedger.AsNoTracking()
                          where e.BusinessId == businessId
                          let settled = e.Amount > 0
                              ? db.DebtorSettlements.Where(s => s.ChargeEntryId == e.Id).Sum(s => (decimal?)s.Amount) ?? 0
                              : db.DebtorSettlements.Where(s => s.PaymentEntryId == e.Id).Sum(s => (decimal?)s.Amount) ?? 0
                          select new { e.PartyId, e.Amount, e.DueDate, Remaining = (e.Amount > 0 ? e.Amount : -e.Amount) - settled })
            .Where(x => x.Remaining != 0).ToListAsync(cancellationToken).ConfigureAwait(false);
        var limits = await db.Debtors.AsNoTracking().Where(d => d.BusinessId == businessId).ToDictionaryAsync(d => d.Id, d => d.CreditLimit, cancellationToken)
            .ConfigureAwait(false);
        var names = await DebtorNamesAsync(open.Select(o => o.PartyId), cancellationToken).ConfigureAwait(false);
        (string Key, string Label, int From, int To)[] buckets =
            [("current", "Not yet due", int.MinValue, 0), ("d30", "1-30 days overdue", 1, 30), ("d60", "31-60 days", 31, 60), ("d90", "61-90 days", 61, 90), ("older", "Over 90 days", 91, int.MaxValue)];
        var table = new ReportTable().Column("debtor", "Debtor", ColumnKinds.Text).Column("limit", "Credit limit", ColumnKinds.Money);
        foreach (var b in buckets)
        {
            table.Column(b.Key, b.Label, ColumnKinds.Money);
        }

        table.Column("advance", "Advance (unapplied)", ColumnKinds.Money).Column("balance", "Balance", ColumnKinds.Money)
            .Note("Unpaid bills by days past their due date, today; the dates do not apply. Balance = unpaid bills - advances.");
        foreach (var debtor in open.GroupBy(o => o.PartyId).OrderBy(g => names.GetValueOrDefault(g.Key, "?"), StringComparer.OrdinalIgnoreCase))
        {
            int Overdue(DateOnly? due) => due is { } d ? today.DayNumber - d.DayNumber : 0;
            var charges = debtor.Where(o => o.Amount > 0).ToList();
            var cells = new List<(string, object?)> { ("debtor", names.GetValueOrDefault(debtor.Key, "?")), ("limit", limits.GetValueOrDefault(debtor.Key)) };
            cells.AddRange(buckets.Select(b => (b.Key, (object?)charges.Where(c => Overdue(c.DueDate) >= b.From && Overdue(c.DueDate) <= b.To).Sum(c => c.Remaining))));
            var advance = debtor.Where(o => o.Amount < 0).Sum(o => o.Remaining);
            cells.Add(("advance", advance));
            cells.Add(("balance", charges.Sum(c => c.Remaining) - advance));
            table.Row([.. cells]);
        }

        return table;
    }

    // Collections

    private sealed class Collected
    {
        public Guid Id { get; set; }

        public DateOnly Date { get; set; }

        public Guid StoreId { get; set; }

        public Guid DebtorId { get; set; }

        public string Method { get; set; } = string.Empty;

        public decimal Amount { get; set; }

        public Guid By { get; set; }

        public bool Reversed { get; set; }
    }

    private Task<List<Collected>> ReceiptsAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken) =>
        (from r in db.DebtorReceipts.AsNoTracking()
         join s in db.CollectorSessions.AsNoTracking() on r.CollectorSessionId equals s.Id into rounds
         from s in rounds.DefaultIfEmpty()
         where r.BusinessId == businessId && r.ReceiptDate >= q.From && r.ReceiptDate <= q.To && (q.StoreId == null || r.StoreId == q.StoreId)
         select new Collected
         {
             Id = r.Id, Date = r.ReceiptDate, StoreId = r.StoreId, DebtorId = r.DebtorId, Method = r.Method, Amount = r.Amount,
             By = s == null ? r.CashierUserId : s.CollectorUserId, Reversed = db.ReceiptReversals.Any(v => v.ReceiptId == r.Id),
         }).ToListAsync(cancellationToken);

    private async Task<ReportTable> CollectionsAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var receipts = await ReceiptsAsync(businessId, q, cancellationToken).ConfigureAwait(false);
        var routes = await RoutesOfAsync(receipts.Select(r => r.DebtorId), cancellationToken).ConfigureAwait(false);
        var users = await UserNamesAsync(receipts.Select(r => r.By).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var stores = await StoreNamesAsync(receipts.Select(r => r.StoreId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("group", q.By switch
            {
                "collector" => "Collected by", "method" => "Method", "route" => "Route", "store" => "Store", _ => "Date",
            }, q.By == "day" ? ColumnKinds.Date : ColumnKinds.Text)
            .Column("receipts", "Receipts", ColumnKinds.Count).Column("received", "Received", ColumnKinds.Money).Column("reversed", "Reversed", ColumnKinds.Money)
            .Column("net", "Net collected", ColumnKinds.Money)
            .Note("Receipts by their date; a receipt reversed later (a bounced cheque, a correction) shows as reversed. Counter receipts count for the cashier.");
        IEnumerable<IGrouping<object, Collected>> groups = q.By switch
        {
            "collector" => receipts.GroupBy(r => (object)users.GetValueOrDefault(r.By, "?")).OrderBy(g => (string)g.Key, StringComparer.OrdinalIgnoreCase),
            "method" => receipts.GroupBy(r => (object)Label(r.Method)).OrderBy(g => (string)g.Key, StringComparer.Ordinal),
            "route" => receipts.GroupBy(r => (object)routes.GetValueOrDefault(r.DebtorId, "No route")).OrderBy(g => (string)g.Key, StringComparer.OrdinalIgnoreCase),
            "store" => receipts.GroupBy(r => (object)stores.GetValueOrDefault(r.StoreId, "?")).OrderBy(g => (string)g.Key, StringComparer.OrdinalIgnoreCase),
            _ => receipts.GroupBy(r => (object)r.Date).OrderBy(g => (DateOnly)g.Key),
        };
        foreach (var g in groups)
        {
            var reversed = g.Where(r => r.Reversed).Sum(r => r.Amount);
            table.Row(("group", g.Key), ("receipts", g.Count()), ("received", g.Sum(r => r.Amount)), ("reversed", reversed), ("net", g.Sum(r => r.Amount) - reversed));
        }

        return table;
    }

    private async Task<ReportTable> CollectorsAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        RequireBusinessWide(q, "Collector figures");
        var receipts = (await ReceiptsAsync(businessId, q, cancellationToken).ConfigureAwait(false)).Where(r => !r.Reversed).ToList();
        var planned = await db.CollectionPlans.AsNoTracking().Where(p => p.BusinessId == businessId && p.PrimaryCollectorUserId != null)
            .GroupBy(p => p.PrimaryCollectorUserId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var visits = await db.VisitOutcomes.AsNoTracking().Where(v => v.BusinessId == businessId && v.VisitDate >= q.From && v.VisitDate <= q.To)
            .GroupBy(v => v.CollectorUserId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var promises = await db.PaymentPromises.AsNoTracking().Where(p => p.BusinessId == businessId && !p.IsCancelled)
            .Where(p => p.RecordedAtUtc >= BusinessCalendar.StartOf(q.From) && p.RecordedAtUtc < BusinessCalendar.StartOf(q.To.AddDays(1)))
            .GroupBy(p => p.RecordedByUserId).Select(g => new { g.Key, Count = g.Count(), Amount = g.Sum(p => p.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rounds = await db.CollectorSessions.AsNoTracking().Where(s => s.BusinessId == businessId && s.BusinessDate >= q.From && s.BusinessDate <= q.To)
            .GroupBy(s => s.CollectorUserId).Select(g => new { g.Key, Count = g.Count(), Variance = g.Sum(s => s.Variance ?? 0) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = planned.Select(p => p.Key).Union(visits.Select(v => v.Key)).Union(rounds.Select(r => r.Key))
            .Union(receipts.Where(r => rounds.Any(x => x.Key == r.By) || planned.Any(x => x.Key == r.By)).Select(r => r.By)).ToList();
        var names = await UserNamesAsync(ids, cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("collector", "Collector", ColumnKinds.Text).Column("parties", "Parties planned (now)", ColumnKinds.Count)
            .Column("visits", "Visits recorded", ColumnKinds.Count).Column("receipts", "Receipts", ColumnKinds.Count).Column("collected", "Collected", ColumnKinds.Money)
            .Column("promises", "Promises taken", ColumnKinds.Count).Column("promised", "Promised", ColumnKinds.Money).Column("rounds", "Rounds", ColumnKinds.Count)
            .Column("variance", "Handover over (+) / short (-)", ColumnKinds.Money)
            .Note("Collected excludes receipts reversed later. Parties planned are those the collector is primary for today.");
        foreach (var id in ids.OrderBy(id => names.GetValueOrDefault(id, "?"), StringComparer.OrdinalIgnoreCase))
        {
            var mine = receipts.Where(r => r.By == id).ToList();
            var p = promises.FirstOrDefault(x => x.Key == id);
            var r = rounds.FirstOrDefault(x => x.Key == id);
            table.Row(("collector", names.GetValueOrDefault(id, "?")), ("parties", planned.FirstOrDefault(x => x.Key == id)?.Count ?? 0),
                ("visits", visits.FirstOrDefault(x => x.Key == id)?.Count ?? 0), ("receipts", mine.Count), ("collected", mine.Sum(x => x.Amount)),
                ("promises", p?.Count ?? 0), ("promised", p?.Amount ?? 0m), ("rounds", r?.Count ?? 0), ("variance", r?.Variance ?? 0m));
        }

        return table;
    }

    private async Task<ReportTable> RoutesAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        RequireBusinessWide(q, "Route figures");
        var plans = await (from p in db.CollectionPlans.AsNoTracking()
                           join r in db.Routes.AsNoTracking() on p.RouteId equals r.Id
                           where p.BusinessId == businessId
                           select new { p.DebtorId, r.Id, Name = r.Code + " - " + r.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var balances = await accounts.BalancesAsync(PartyTypes.Debtor, plans.Select(p => p.DebtorId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var receipts = (await ReceiptsAsync(businessId, q, cancellationToken).ConfigureAwait(false)).Where(r => !r.Reversed).ToList();
        var table = new ReportTable().Column("route", "Route", ColumnKinds.Text).Column("parties", "Parties", ColumnKinds.Count)
            .Column("balance", "Owed now", ColumnKinds.Money).Column("overdue", "Overdue now", ColumnKinds.Money).Column("collected", "Collected in period", ColumnKinds.Money)
            .Ratio("collected_pct", "Collected / owed now", "collected", "balance")
            .Note("Parties and balances are today's; collected is net of reversed receipts, from the parties now on the route.");
        foreach (var route in plans.GroupBy(p => (p.Id, p.Name)).OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase))
        {
            var parties = route.Select(p => p.DebtorId).ToHashSet();
            table.Row(("route", route.Key.Name), ("parties", parties.Count), ("balance", parties.Sum(d => balances.GetValueOrDefault(d).Balance)),
                ("overdue", parties.Sum(d => balances.GetValueOrDefault(d).Overdue)), ("collected", receipts.Where(r => parties.Contains(r.DebtorId)).Sum(r => r.Amount)));
        }

        return table;
    }

    private async Task<ReportTable> PromisesAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        RequireBusinessWide(q, "Promises");
        var today = BusinessCalendar.Today(clock);
        var promises = await (from p in db.PaymentPromises.AsNoTracking()
                              join u in db.Users.AsNoTracking() on p.RecordedByUserId equals u.Id
                              where p.BusinessId == businessId && p.PromisedDate >= q.From && p.PromisedDate <= q.To
                              orderby p.PromisedDate, p.RecordedAtUtc
                              select new { p, By = u.DisplayName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var debtorIds = promises.Select(x => x.p.DebtorId).Distinct().ToList();
        var receipts = await db.DebtorReceipts.AsNoTracking().Where(r => debtorIds.Contains(r.DebtorId) && !db.ReceiptReversals.Any(v => v.ReceiptId == r.Id))
            .Select(r => new { r.DebtorId, r.ReceiptDate, r.Amount, r.CreatedAtUtc }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await DebtorNamesAsync(debtorIds, cancellationToken).ConfigureAwait(false);
        var table = new ReportTable().Column("debtor", "Debtor", ColumnKinds.Text).Column("promised_on", "Promised for", ColumnKinds.Date)
            .Column("amount", "Promised", ColumnKinds.Money).Column("paid", "Paid since", ColumnKinds.Money).Column("status", "Status", ColumnKinds.Text)
            .Column("by", "Taken by", ColumnKinds.Text).Column("note", "Note", ColumnKinds.Text)
            .Note("Kept: paid at least the promise between taking it and the promised date. Paid since counts receipts not reversed.");
        foreach (var x in promises)
        {
            var paid = receipts.Where(r => r.DebtorId == x.p.DebtorId && r.CreatedAtUtc >= x.p.RecordedAtUtc && r.ReceiptDate <= x.p.PromisedDate).Sum(r => r.Amount);
            var status = x.p.IsCancelled ? "Cancelled" : paid >= x.p.Amount ? "Kept" : today > x.p.PromisedDate ? "Broken" : "Pending";
            table.Row(("debtor", names.GetValueOrDefault(x.p.DebtorId, "?")), ("promised_on", x.p.PromisedDate), ("amount", x.p.Amount), ("paid", paid), ("status", status),
                ("by", x.By), ("note", x.p.Note));
        }

        return table;
    }

    // Dispatch and packing

    private async Task<ReportTable> DispatchesAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var consignments = await db.Consignments.AsNoTracking()
            .Where(c => c.BusinessId == businessId && c.Status == ConsignmentStatus.Dispatched && c.DispatchDate >= q.From && c.DispatchDate <= q.To
                        && (q.StoreId == null || c.StoreId == q.StoreId))
            .OrderBy(c => c.DispatchDate).ThenBy(c => c.Number).ToListAsync(cancellationToken).ConfigureAwait(false);
        var table = new ReportTable();
        if (q.By is null or "document")
        {
            table.Column("number", "Dispatch", ColumnKinds.Text).Column("date", "Date", ColumnKinds.Date).Column("party", "Customer", ColumnKinds.Text)
                .Column("mode", "By", ColumnKinds.Text).Column("transporter", "Lorry service", ColumnKinds.Text).Column("lr", "LR/GR", ColumnKinds.Text)
                .Column("lr_date", "LR/GR date", ColumnKinds.Date).Column("route", "From - to", ColumnKinds.Text).Column("vehicle", "Vehicle", ColumnKinds.Text)
                .Column("packages", "Packages", ColumnKinds.Count).Column("weight", "Weight (kg)", ColumnKinds.Quantity).Column("terms", "Freight", ColumnKinds.Text)
                .Column("freight", "Freight amount", ColumnKinds.Money).Column("value", "Goods value", ColumnKinds.Money).Column("eway", "E-way bill", ColumnKinds.Text)
                .Column("delivery", "Delivery", ColumnKinds.Text);
            foreach (var c in consignments)
            {
                table.Row(("number", c.Number), ("date", c.DispatchDate), ("party", c.PartyName), ("mode", Label(c.Mode)), ("transporter", c.TransporterName),
                    ("lr", c.LrNumber), ("lr_date", c.LrDate), ("route", c.BookingOffice is null ? null : $"{c.BookingOffice} - {c.DestinationBranch}"),
                    ("vehicle", c.VehicleNumber), ("packages", c.PackageCount), ("weight", c.WeightKg), ("terms", c.FreightTerms is null ? null : Label(c.FreightTerms)),
                    ("freight", c.FreightAmount), ("value", c.GoodsValue), ("eway", c.EwayBillNumber ?? (c.EwayBillMissing ? "Missing" : null)),
                    ("delivery", c.DeliveryOutcome is null ? "On the way" : Label(c.DeliveryOutcome)));
            }

            return table.Note("Dispatches that stand (cancelled ones are left out). The lorry service rows are the LR/GR register.");
        }

        table.Column("group", q.By == "day" ? "Date" : "Lorry service", q.By == "day" ? ColumnKinds.Date : ColumnKinds.Text)
            .Column("dispatches", "Dispatches", ColumnKinds.Count).Column("packages", "Packages", ColumnKinds.Count)
            .Column("paid", "Freight paid", ColumnKinds.Money).Column("to_pay", "Freight to pay", ColumnKinds.Money).Column("value", "Goods value", ColumnKinds.Money)
            .Note("Freight paid is paid by the business at booking; to pay is collected from the customer at the destination.");
        var groups = q.By == "day"
            ? consignments.GroupBy(c => (object)c.DispatchDate)
            : consignments.GroupBy(c => (object)(c.TransporterName ?? Label(c.Mode))).OrderBy(g => (string)g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            table.Row(("group", g.Key), ("dispatches", g.Count()), ("packages", g.Sum(c => c.PackageCount)),
                ("paid", g.Where(c => c.FreightTerms == FreightTerms.Paid).Sum(c => c.FreightAmount)),
                ("to_pay", g.Where(c => c.FreightTerms == FreightTerms.ToPay).Sum(c => c.FreightAmount)), ("value", g.Sum(c => c.GoodsValue)));
        }

        return table;
    }

    private async Task<ReportTable> PackingAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        var (from, to) = (BusinessCalendar.StartOf(q.From), BusinessCalendar.StartOf(q.To.AddDays(1)));
        var challans = await db.PackingChallans.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.BusinessId == businessId && c.CreatedAtUtc >= from && c.CreatedAtUtc < to && (q.StoreId == null || c.StoreId == q.StoreId))
            .OrderBy(c => c.CreatedAtUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        var figures = await packing.FiguresAsync(challans.SelectMany(c => c.Lines).ToList(), cancellationToken).ConfigureAwait(false);
        var invoiceIds = challans.Select(c => c.InvoiceId).ToList();
        var invoices = await db.SalesInvoices.AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, cancellationToken).ConfigureAwait(false);
        var userIds = challans.SelectMany(c => new[] { c.PickedByUserId, c.CheckedByUserId, c.PackedByUserId }).OfType<Guid>().Distinct().ToList();
        var users = await UserNamesAsync(userIds, cancellationToken).ConfigureAwait(false);
        string? Name(Guid? id) => id is { } u ? users.GetValueOrDefault(u) : null;
        var table = new ReportTable().Column("number", "Challan", ColumnKinds.Text).Column("bill", "Bill", ColumnKinds.Text).Column("party", "Customer", ColumnKinds.Text)
            .Column("progress", "Where the goods are", ColumnKinds.Text).Column("picker", "Picked by", ColumnKinds.Text).Column("checker", "Checked by", ColumnKinds.Text)
            .Column("packer", "Packed by", ColumnKinds.Text).Column("packages", "Packages", ColumnKinds.Count).Column("short", "Short (units)", ColumnKinds.Quantity)
            .Column("lost", "Lost on the way (units)", ColumnKinds.Quantity).Column("difference", "Difference to settle (units)", ColumnKinds.Quantity)
            .Note("Challans created in the period. Quantities are added across items, as counts of units.");
        foreach (var c in challans)
        {
            var lines = c.Lines.Select(l => (Line: l, Figures: figures[l.Id])).ToList();
            table.Row(("number", c.Number), ("bill", invoices.GetValueOrDefault(c.InvoiceId)), ("party", c.PartyName),
                ("progress", Label(PackingService.Progress(c, lines))), ("picker", Name(c.PickedByUserId)), ("checker", Name(c.CheckedByUserId)),
                ("packer", Name(c.PackedByUserId)), ("packages", c.PackageCount),
                ("short", lines.Sum(x => x.Line.CheckedQuantity is { } k ? x.Line.Quantity - k : 0)), ("lost", lines.Sum(x => x.Figures.Lost)),
                ("difference", lines.Sum(x => PackingService.Difference(x.Line, x.Figures))));
        }

        return table;
    }

    // Audit

    private async Task<ReportTable> AuditEventsAsync(Guid businessId, ReportQuery q, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(Permissions.AuditView, businessId, q.StoreId, cancellationToken).ConfigureAwait(false))
        {
            await organisation.RequireAsync(Permissions.AuditView, businessId, q.StoreId, cancellationToken).ConfigureAwait(false);
        }

        var (from, to) = (BusinessCalendar.StartOf(q.From), BusinessCalendar.StartOf(q.To.AddDays(1)));
        var events = db.AuditEvents.AsNoTracking()
            .Where(e => e.BusinessId == businessId && e.OccurredAtUtc >= from && e.OccurredAtUtc < to && (q.StoreId == null || e.StoreId == q.StoreId));
        if (q.By is "event" or "user")
        {
            var counts = q.By == "event"
                ? await events.GroupBy(e => e.EventType).Select(g => new { Key = g.Key, Actor = (Guid?)null, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false)
                : await events.GroupBy(e => e.ActorUserId).Select(g => new { Key = string.Empty, Actor = g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var actors = await UserNamesAsync(counts.Where(c => c.Actor != null).Select(c => c.Actor!.Value).ToList(), cancellationToken).ConfigureAwait(false);
            var table = new ReportTable().Column("group", q.By == "event" ? "Event" : "User", ColumnKinds.Text).Column("events", "Events", ColumnKinds.Count)
                .Note("Business events in the period (sign-ins and other events not tied to the business are in the system audit trail).");
            foreach (var c in counts.Select(c => (Name: q.By == "event" ? c.Key : c.Actor is { } a ? actors.GetValueOrDefault(a, "?") : "System", c.Count))
                         .OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                table.Row(("group", c.Name), ("events", c.Count));
            }

            return table;
        }

        const int Limit = 5000;
        var list = await events.OrderByDescending(e => e.Sequence).Take(Limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await UserNamesAsync(list.Where(e => e.ActorUserId != null).Select(e => e.ActorUserId!.Value).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var detail = new ReportTable().Column("at", "When", ColumnKinds.DateTime).Column("event", "Event", ColumnKinds.Text).Column("user", "User", ColumnKinds.Text)
            .Column("entity", "Record", ColumnKinds.Text).Column("details", "Details", ColumnKinds.Text);
        if (list.Count > Limit)
        {
            detail.Note($"Only the latest {Limit} events are listed; choose a shorter period or group by event to see all.");
        }

        foreach (var e in list.Take(Limit))
        {
            detail.Row(("at", e.OccurredAtUtc), ("event", e.EventType), ("user", e.ActorUserId is { } a ? names.GetValueOrDefault(a, "?") : "System"),
                ("entity", e.EntityType is null ? null : $"{e.EntityType} {e.EntityId}"), ("details", e.PayloadJson.Length > 300 ? e.PayloadJson[..300] + "..." : e.PayloadJson));
        }

        return detail;
    }

    // Names

    private async Task<Dictionary<Guid, string>> DebtorNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return (await db.Debtors.AsNoTracking().Where(d => list.Contains(d.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => $"{d.Code} - {d.DisplayName}");
    }

    private async Task<Dictionary<Guid, string>> RoutesOfAsync(IEnumerable<Guid> debtorIds, CancellationToken cancellationToken)
    {
        var list = debtorIds.Distinct().ToList();
        return await (from p in db.CollectionPlans.AsNoTracking()
                      join r in db.Routes.AsNoTracking() on p.RouteId equals r.Id
                      where list.Contains(p.DebtorId)
                      select new { p.DebtorId, Name = r.Code + " - " + r.Name }).ToDictionaryAsync(x => x.DebtorId, x => x.Name, cancellationToken).ConfigureAwait(false);
    }
}
