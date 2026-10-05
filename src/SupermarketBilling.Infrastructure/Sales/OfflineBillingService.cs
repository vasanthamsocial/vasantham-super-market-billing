using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Sales;

/// <summary>
/// Offline counter billing (D-039). A manager lets one device of a counter bill without the server, within limits. While
/// online, the POS hands that device's counter agent a pack (items, prices, the counter's offline series and its next
/// number); when the server cannot be reached the agent prices and issues real invoices from that series with the same
/// code as the server. When the server is back the bills arrive in order and each is posted as issued, with its own
/// number and time: an issued invoice cannot be refused, so doubts (a price no longer in force, a changed tax rate) are
/// flagged for review, and only a bill that cannot be recorded (its figures do not add up, its shift is closed) waits
/// for a manager, its number kept.
/// </summary>
public sealed class OfflineBillingService(
    SupermarketBillingDbContext db,
    CounterService counters,
    OrganisationService organisation,
    IAccessControl access,
    DocumentNumbers numbers,
    StockEngine stock,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string IdempotencyPrefix = "offline-";
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    // Settings

    /// <summary>Allows (or stops) offline billing on one device of a counter. Only one device per counter may bill offline.</summary>
    public async Task<CounterDeviceDto> SetOfflineAsync(Guid businessId, Guid counterId, Guid deviceId, CounterOfflineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var counter = await db.Counters.FirstOrDefaultAsync(c => c.Id == counterId && c.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Counter");
        await organisation.RequireAsync(Permissions.CountersManage, businessId, counter.StoreId, cancellationToken).ConfigureAwait(false);
        var device = await db.CounterDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.CounterId == counterId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Device");
        OfflineLimits? limits = request is { MaxBills: null, MaxAmount: null, MaxHours: null }
            ? null
            : new OfflineLimits(request.MaxBills ?? 0, request.MaxAmount ?? 0, request.MaxHours ?? 0);
        if (limits is not null)
        {
            if (await db.CounterDevices.AnyAsync(d => d.CounterId == counterId && d.Id != deviceId && d.OfflineMaxBills != null, cancellationToken).ConfigureAwait(false))
            {
                throw AppException.Conflict("offline.other_device", $"Another device of counter {counter.Code} already bills offline. Stop it there first: only one device may number the offline series.");
            }

            var (_, index) = await BillingService.RegistrationInForceAsync(db, businessId, BusinessCalendar.Today(clock), cancellationToken).ConfigureAwait(false);
            if (!OfflineSeries.Fits(counter.InvoicePrefix(index)))
            {
                throw AppException.Validation("offline.prefix_too_long", $"Counter {counter.Code}'s offline invoice numbers would be longer than the 16 characters GST allows. Use a shorter counter code.");
            }
        }

        Valid(() => device.SetOffline(limits, currentUser.UserId, clock.GetUtcNow()));
        audit.Record(limits is null ? "counter.offline_stopped" : "counter.offline_allowed", "counter_device", device.Id, businessId, counter.StoreId,
            details: new { counter = counter.Code, device.Name, limits?.MaxBills, limits?.MaxAmount, limits?.MaxHours });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await counters.DevicesAsync(businessId, counterId, null, cancellationToken).ConfigureAwait(false)).First(d => d.Id == deviceId);
    }

    // Pack

    /// <summary>What this device's counter agent needs to bill offline for the signed-in cashier in their open shift.</summary>
    public async Task<OfflinePack> PackAsync(string? deviceToken, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var limits = pos.Device.Offline
            ?? throw AppException.Conflict("offline.not_allowed", "This counter PC may not bill without the server. A manager can allow it under Counters.");
        var shift = await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CounterId == pos.Counter.Id && s.Status == ShiftStatus.Open, cancellationToken).ConfigureAwait(false);
        if (shift is null || shift.CashierUserId != currentUser.UserId)
        {
            throw AppException.Conflict("shift.not_open", "Open your shift on this counter first: offline bills belong to it.");
        }

        var businessId = pos.Counter.BusinessId;
        var now = clock.GetUtcNow();
        var (registration, index) = await BillingService.RegistrationInForceAsync(db, businessId, BusinessCalendar.Today(clock, pos.Store.TimeZone), cancellationToken)
            .ConfigureAwait(false);
        var prefix = OfflineSeries.Prefix(pos.Counter.InvoicePrefix(index));
        var next = await NextNumberAsync(pos.Store.Id, prefix, cancellationToken).ConfigureAwait(false);
        var seller = Seller(pos.Business.LegalName, pos.Store, pos.Business.Address, registration);

        var packs = await (
                from vu in db.VariantUnits.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on vu.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                join packUnit in db.Units.AsNoTracking() on vu.UnitId equals packUnit.Id
                join baseUnit in db.Units.AsNoTracking() on p.BaseUnitId equals baseUnit.Id
                where vu.BusinessId == businessId && vu.IsActive && v.IsActive && p.IsActive
                select new { Pack = vu, Variant = v, Product = p, PackUnit = packUnit.Code, BaseUnit = baseUnit })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Only prices any walk-in customer gets at this store, in force now or later.
        var storeId = pos.Store.Id;
        var rules = await db.PriceRules.AsNoTracking()
            .Where(r => r.BusinessId == businessId && r.Status == PriceRuleStatus.Active && (r.ValidToUtc == null || r.ValidToUtc > now)
                        && (r.StoreId == null || r.StoreId == storeId) && r.CustomerGroupId == null && !r.MembersOnly)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var mrps = (await db.VariantMrps.AsNoTracking().Where(m => m.BusinessId == businessId && m.IsActive).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(m => m.VariantUnitId, m => m.Mrp);
        var barcodes = (await db.VariantBarcodes.AsNoTracking().Where(b => b.BusinessId == businessId && b.IsActive).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(b => b.VariantUnitId, b => b.Code);
        var priced = rules.Select(r => r.VariantUnitId).ToHashSet();

        var items = packs.Where(p => priced.Contains(p.Pack.Id)).Select(p => new OfflinePackItem(p.Pack.Id, p.Variant.Id, p.Product.Id, p.Variant.Name, p.PackUnit,
            p.BaseUnit.Code, p.BaseUnit.DecimalPlaces, p.Pack.FactorToBase, p.Product.HsnSac, p.Product.SupplyType, p.Product.GstRatePercent, p.Product.CessRatePercent,
            mrps[p.Pack.Id].Distinct().Order().ToList(), barcodes[p.Pack.Id].ToList())).ToList();
        var itemIds = items.Select(i => i.VariantUnitId).ToHashSet();
        var prices = rules.Where(r => itemIds.Contains(r.VariantUnitId)).Select(r => new OfflinePackPrice(r.Id, r.VariantUnitId, r.RateType, r.Channel, r.Price,
            r.TaxInclusive, r.Mrp, r.StoreId, r.MinQuantity, r.MaxQuantity, r.ValidFromUtc, r.ValidToUtc, r.Priority)).ToList();
        var cashier = await db.Users.AsNoTracking().Where(u => u.Id == currentUser.UserId).Select(u => u.DisplayName).FirstAsync(cancellationToken).ConfigureAwait(false);
        return new OfflinePack(Guid.CreateVersion7(now), now, businessId, storeId, pos.Store.TimeZone, pos.Counter.Id, pos.Counter.Code, pos.Device.Id,
            currentUser.UserId, cashier, shift.Id, registration.Mode, prefix, next, seller, limits, items, prices);
    }

    // Synchronisation

    /// <summary>
    /// Receives the bills a counter issued without the server, in their order. One synchronisation per counter at a time;
    /// each bill is posted (or quarantined, keeping its number) in its own transaction. A resend returns the first result;
    /// a gap stops that series so the counter sends what is missing first.
    /// </summary>
    public async Task<OfflineBillsSyncResponse> SyncAsync(string? deviceToken, OfflineBillsSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Bills);
        if (request.Bills.Count > 200)
        {
            throw AppException.Validation("offline.too_many", "Send at most 200 bills at a time.");
        }

        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var counterId = pos.Counter.Id;
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock(hashtext({"offline-counter|" + counterId}))", cancellationToken).ConfigureAwait(false);
            try
            {
                var results = new List<OfflineBillResult>();
                var stopped = new HashSet<string>(StringComparer.Ordinal);
                foreach (var bill in request.Bills.OrderBy(b => b.NumberPrefix, StringComparer.Ordinal).ThenBy(b => b.Sequence))
                {
                    results.Add(await ReceiveAsync(pos, bill, stopped, cancellationToken).ConfigureAwait(false));
                }

                return new OfflineBillsSyncResponse(results);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtext({"offline-counter|" + counterId}))", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<OfflineBillResult> ReceiveAsync(PosDevice pos, OfflineBill bill, HashSet<string> stopped, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        ArgumentNullException.ThrowIfNull(bill.Lines);
        ArgumentNullException.ThrowIfNull(bill.Payments);
        var payload = JsonSerializer.Serialize(bill, PayloadJson);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var known = await db.OfflineBills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bill.Id, cancellationToken).ConfigureAwait(false);
        if (known is not null)
        {
            return known.PayloadHash == hash
                ? new OfflineBillResult(known.Id, known.Number, "DUPLICATE", known.Reason, known.Review, known.InvoiceId)
                : new OfflineBillResult(bill.Id, bill.Number, "REJECTED", "Sent before with different contents.", null, null);
        }

        // Only this device's bills, in this counter's offline series.
        if (bill.DeviceId != pos.Device.Id || !OfflineSeries.IsOffline(bill.NumberPrefix ?? string.Empty)
            || !bill.NumberPrefix!.StartsWith(pos.Counter.Code, StringComparison.Ordinal) || bill.NumberPrefix.Length > 10 || bill.Sequence <= 0)
        {
            return new OfflineBillResult(bill.Id, bill.Number, "REJECTED", "Not a bill of this counter PC's offline series.", null, null);
        }

        var next = await NextNumberAsync(pos.Store.Id, bill.NumberPrefix, cancellationToken).ConfigureAwait(false);
        if (stopped.Contains(bill.NumberPrefix) || bill.Sequence > next)
        {
            stopped.Add(bill.NumberPrefix);
            return new OfflineBillResult(bill.Id, bill.Number, "NOT_PROCESSED", $"{Counter.InvoiceNumber(bill.NumberPrefix, next)} must come first.", null, null);
        }

        if (bill.Sequence < next)
        {
            return new OfflineBillResult(bill.Id, bill.Number, "REJECTED", "This number was already used by another bill.", null, null);
        }

        var now = clock.GetUtcNow();
        string? reason = null;
        string? review = null;
        Guid? invoiceId = null;
        try
        {
            review = await PrepareAsync(pos.Counter, pos.Store, pos.Business.LegalName, pos.Business.Address, bill, bill.ShiftId, now, cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await TakeNumberAsync(pos.Counter.BusinessId, pos.Store.Id, bill, cancellationToken).ConfigureAwait(false);
            invoiceId = await RecordInvoiceAsync(pos.Counter, pos.Store, bill, bill.ShiftId, hash, now, cancellationToken).ConfigureAwait(false);
            var record = OfflineBillRecord.Receive(pos.Counter.BusinessId, pos.Store.Id, pos.Counter.Id, bill, payload, hash, invoiceId, null, review, currentUser.UserId, now);
            db.OfflineBills.Add(record);
            audit.Record("sales.offline_bill_posted", "offline_bill", record.Id, record.BusinessId, record.StoreId, details: new { record.Number, record.GrandTotal, review });
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AppException or DomainException or DbUpdateException)
        {
            reason = e is DbUpdateException { InnerException: Npgsql.PostgresException pg } ? pg.MessageText : e.Message;
        }

        if (reason is not null)
        {
            // Not posted: the number is still taken, and the bill waits for a manager.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await TakeNumberAsync(pos.Counter.BusinessId, pos.Store.Id, bill, cancellationToken).ConfigureAwait(false);

            var record = OfflineBillRecord.Receive(pos.Counter.BusinessId, pos.Store.Id, pos.Counter.Id, bill, payload, hash, null, reason, null, currentUser.UserId, now);
            db.OfflineBills.Add(record);
            audit.Record("sales.offline_bill_quarantined", "offline_bill", record.Id, record.BusinessId, record.StoreId,
                details: new { record.Number, record.GrandTotal, reason = record.Reason });
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OfflineBillResult(record.Id, record.Number, record.Status, record.Reason, null, null);
        }

        return new OfflineBillResult(bill.Id, bill.Number, OfflineBillStatus.Posted, null, review, invoiceId);
    }

    /// <summary>
    /// Whether the bill can be recorded as the invoice it is (its figures add up, its series and tax mode were in force,
    /// its shift is open, its items are this business's): throws if not. Returns what a manager should review.
    /// </summary>
    private async Task<string?> PrepareAsync(
        Counter counter, Store store, string legalName, string? businessAddress, OfflineBill bill, Guid shiftId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var businessId = counter.BusinessId;
        if (OfflineBilling.Inconsistency(bill) is { } wrong)
        {
            throw new DomainException("offline.inconsistent", wrong);
        }

        var (registration, index) = await BillingService.RegistrationInForceAsync(db, businessId, bill.BusinessDate, cancellationToken).ConfigureAwait(false);
        if (bill.NumberPrefix != OfflineSeries.Prefix(counter.InvoicePrefix(index)) || bill.TaxMode != registration.Mode)
        {
            throw new DomainException("offline.registration", $"On {bill.BusinessDate:yyyy-MM-dd} the business was {registration.Mode}: the bill's series or tax mode is not the one in force.");
        }

        var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shiftId, cancellationToken).ConfigureAwait(false);
        if (shift is null || shift.CounterId != counter.Id || shift.CashierUserId != bill.CashierUserId || shift.Status != ShiftStatus.Open)
        {
            throw new DomainException("offline.shift_closed", "The cashier's shift on this counter is no longer open, so the bill cannot be added to it.");
        }

        // The items must be this business's, as named.
        var packIds = bill.Lines.Select(l => l.VariantUnitId).Distinct().ToList();
        var catalog = await (
                from vu in db.VariantUnits.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on vu.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                where packIds.Contains(vu.Id) && vu.BusinessId == businessId
                select new { Pack = vu, Variant = v, Product = p })
            .ToDictionaryAsync(x => x.Pack.Id, cancellationToken).ConfigureAwait(false);
        if (bill.Lines.Any(l => !catalog.TryGetValue(l.VariantUnitId, out var c) || c.Variant.Id != l.VariantId || c.Product.Id != l.ProductId
                                || l.BaseQuantity != c.Pack.ToBase(l.Quantity)))
        {
            throw new DomainException("offline.items", "An item on the bill is not this business's (or not as described).");
        }

        return await ReviewAsync(bill, store, Seller(legalName, store, businessAddress, registration), catalog.ToDictionary(k => k.Key, k => k.Value.Product),
            now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the bill's number in the offline series (the caller holds the counter's lock and checked it is next).</summary>
    private async Task TakeNumberAsync(Guid businessId, Guid storeId, OfflineBill bill, CancellationToken cancellationToken)
    {
        var taken = await numbers.NextAsync(businessId, storeId, Counter.InvoiceSeries(bill.NumberPrefix), cancellationToken).ConfigureAwait(false);
        if (taken != bill.Sequence)
        {
            throw new InvalidOperationException("The offline series moved while it was locked.");
        }
    }

    /// <summary>Records the bill as the invoice it is, with its own number, time, prices and taxes, in the shift, taking stock out.</summary>
    private async Task<Guid> RecordInvoiceAsync(Counter counter, Store store, OfflineBill bill, Guid shiftId, string hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var businessId = counter.BusinessId;
        var result = new BillResult(bill.Kind, bill.Lines.Select(l => l.Amounts).ToList(), bill.GrossTotal, 0, bill.TaxableTotal, bill.CgstTotal, bill.SgstTotal,
            bill.IgstTotal, bill.CessTotal, bill.RoundOff, bill.GrandTotal);
        var invoice = Valid(() => SalesInvoice.Issue(bill.Id, businessId, store.Id, counter, bill.DeviceId, shiftId, bill.NumberPrefix, bill.Sequence, bill.TaxMode,
            bill.Channel, bill.BusinessDate, bill.CashierUserId, bill.Seller, bill.Buyer, bill.PlaceOfSupply, result, bill.Payments, null, negativeStockOverride: true,
            IdempotencyPrefix + bill.Id.ToString("N"), hash, bill.IssuedAtUtc));

        // The goods have left the store: stock goes out (below zero if need be, which the negative-stock report shows).
        await stock.StartAsync(new StockPostingDocument(businessId, BillingService.LedgerDocumentType, invoice.Id, invoice.Number, true, bill.BusinessDate, now),
            cancellationToken).ConfigureAwait(false);
        await stock.LockAsync(bill.Lines.Select(l => (store.Id, l.VariantId)), cancellationToken).ConfigureAwait(false);
        foreach (var line in bill.Lines)
        {
            var taken = await stock.IssueAsync(store.Id, new StockItem(line.VariantId, line.Description, line.ProductId), line.BaseQuantity, MovementTypes.Sale, null,
                recordsReality: true, cancellationToken).ConfigureAwait(false);
            var cost = taken.Sum(t => StockMath.Value(t.Quantity, t.UnitCost));
            invoice.AddLine(Valid(() => SalesInvoiceLine.Create(businessId, invoice.Id, line.LineNumber,
                new SalesInvoiceLine.Item(line.ProductId, line.VariantId, line.VariantUnitId, line.Description, line.HsnSac, line.UnitCode, line.Quantity, line.BaseQuantity,
                    line.Mrp),
                new SalesInvoiceLine.Pricing(line.PriceRuleId, line.RateType, null, line.UnitPrice, line.TaxInclusive, line.SupplyType, line.GstRatePercent,
                    line.CessRatePercent),
                line.Amounts, cost, now)));
        }

        db.SalesInvoices.Add(invoice);
        stock.Flush();
        audit.Record("sales.invoice_issued", "sales_invoice", invoice.Id, businessId, store.Id, details: new
        {
            invoice.Number,
            invoice.Kind,
            counter = counter.Code,
            offline = true,
            issuedAt = bill.IssuedAtUtc,
            invoice.GrandTotal,
            lines = bill.Lines.Count,
            payments = bill.Payments.Select(p => new { p.Method, p.Amount }),
        });
        return invoice.Id;
    }

    /// <summary>What a manager should check on a bill that is posted anyway: prices and taxes against the records, the clock, the seller.</summary>
    private async Task<string?> ReviewAsync(
        OfflineBill bill, Store store, SalesInvoice.Seller seller, Dictionary<Guid, Product> products, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var ruleIds = bill.Lines.Select(l => l.PriceRuleId).Distinct().ToList();
        var rules = await db.PriceRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);
        var packIds = bill.Lines.Select(l => l.VariantUnitId).ToList();
        var mrps = (await db.VariantMrps.AsNoTracking().Where(m => packIds.Contains(m.VariantUnitId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(m => m.VariantUnitId, m => m.Mrp);
        foreach (var line in bill.Lines)
        {
            var rule = rules.GetValueOrDefault(line.PriceRuleId);
            var product = products[line.VariantUnitId];
            if (rule is null || rule.VariantUnitId != line.VariantUnitId || rule.Price != line.UnitPrice || rule.TaxInclusive != line.TaxInclusive
                || rule.RateType != line.RateType || !rule.WasInForceAt(bill.IssuedAtUtc) || (rule.StoreId is { } s && s != store.Id) || rule.CustomerGroupId is not null
                || rule.MembersOnly || (rule.Channel != SalesChannels.Any && rule.Channel != bill.Channel) || (rule.Mrp is { } m && m != line.Mrp))
            {
                notes.Add($"Line {line.LineNumber} ({line.Description}): Rs. {line.UnitPrice:0.00} is not a price that was in force for it then.");
            }

            if (product.GstRatePercent != line.GstRatePercent || product.CessRatePercent != line.CessRatePercent || product.SupplyType != line.SupplyType
                || product.HsnSac != line.HsnSac)
            {
                notes.Add($"Line {line.LineNumber} ({line.Description}): tax details differ from the product's ({product.SupplyType}, GST {product.GstRatePercent}%, HSN {product.HsnSac}).");
            }

            if (line.Mrp is { } mrp && !mrps[line.VariantUnitId].Contains(mrp))
            {
                notes.Add($"Line {line.LineNumber} ({line.Description}): no MRP of Rs. {mrp:0.00} is recorded for it.");
            }
        }

        if (bill.IssuedAtUtc > now + ClockSkew)
        {
            notes.Add($"Issued at {bill.IssuedAtUtc:yyyy-MM-dd HH:mm} UTC by the counter's clock, which is later than now: check the counter PC's clock.");
        }

        if (bill.BusinessDate != OfflineBilling.BusinessDate(bill.IssuedAtUtc, store.TimeZone))
        {
            notes.Add($"Dated {bill.BusinessDate:yyyy-MM-dd}, which is not the store's date at the time it was issued.");
        }

        if (bill.Seller != seller)
        {
            notes.Add("The seller's name, GSTIN or address on the bill differs from the records.");
        }

        if (bill.Buyer.Gstin is { } gstin && !Gstin.IsValid(gstin))
        {
            notes.Add("The buyer's GSTIN is not valid.");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    // Decisions

    public async Task<IReadOnlyList<OfflineBillDto>> ListAsync(Guid businessId, string? status, Guid? counterId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.SalesView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.OfflineBills.AsNoTracking().Where(b => b.BusinessId == businessId);
        query = status switch
        {
            null or "" => query,
            "REVIEW" => query.Where(b => b.Status == OfflineBillStatus.Posted && b.Review != null && b.ReviewedAtUtc == null),
            _ => query.Where(b => b.Status == status),
        };
        query = counterId is { } c ? query.Where(b => b.CounterId == c) : query;
        var list = await query.OrderByDescending(b => b.IssuedAtUtc).ThenByDescending(b => b.Sequence).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await DtosAsync(list, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A manager decides a quarantined bill: posted now (when what stopped it is fixed, in its cashier's open shift on that
    /// counter) or recorded as not posted, with why. Never by its cashier.
    /// </summary>
    public async Task<OfflineBillDto> ResolveAsync(Guid businessId, Guid billId, ResolveOfflineBillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var record = await LoadAsync(businessId, billId, request.RowVersion, cancellationToken).ConfigureAwait(false);
        if (record.Status != OfflineBillStatus.Quarantined)
        {
            throw AppException.Conflict("offline_bill.not_quarantined", "Only a bill waiting for a decision can be resolved.");
        }

        if (record.CashierUserId == currentUser.UserId)
        {
            throw AppException.Forbidden("The cashier who issued the bill cannot decide on it. Another person must.");
        }

        var now = clock.GetUtcNow();
        if (!request.Post)
        {
            Valid(() => record.Resolve(null, request.Note, currentUser.UserId, now));
            audit.Record("sales.offline_bill_resolved", "offline_bill", record.Id, businessId, record.StoreId, details: new { record.Number, posted = false, request.Note });
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            return (await DtosAsync([record], cancellationToken).ConfigureAwait(false))[0];
        }

        if (string.IsNullOrWhiteSpace(request.Note))
        {
            throw AppException.Validation("offline_bill.note_required", "Say what was decided and why (max 300 characters).");
        }

        var bill = JsonSerializer.Deserialize<OfflineBill>(record.Payload, PayloadJson)!;
        var counter = await db.Counters.AsNoTracking().FirstAsync(c => c.Id == record.CounterId, cancellationToken).ConfigureAwait(false);
        var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == record.StoreId, cancellationToken).ConfigureAwait(false);
        var business = await db.Businesses.AsNoTracking().FirstAsync(b => b.Id == businessId, cancellationToken).ConfigureAwait(false);
        var shift = await db.Shifts.AsNoTracking()
                .FirstOrDefaultAsync(s => s.CounterId == counter.Id && s.CashierUserId == record.CashierUserId && s.Status == ShiftStatus.Open, cancellationToken)
                .ConfigureAwait(false)
            ?? throw AppException.Conflict("offline_bill.no_shift", $"To post it, its cashier must have a shift open on counter {counter.Code}; or record it as not posted.");
        try
        {
            await PrepareAsync(counter, store, business.LegalName, business.Address, bill, shift.Id, now, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException e)
        {
            throw AppException.Conflict(e.Code, e.Message);
        }

        // Its number was taken when it arrived.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            var invoiceId = await RecordInvoiceAsync(counter, store, bill, shift.Id, record.PayloadHash, now, cancellationToken).ConfigureAwait(false);
            Valid(() => record.Resolve(invoiceId, request.Note, currentUser.UserId, now));
            audit.Record("sales.offline_bill_resolved", "offline_bill", record.Id, businessId, record.StoreId, details: new { record.Number, posted = true, request.Note });
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (await DtosAsync([await db.OfflineBills.AsNoTracking().FirstAsync(b => b.Id == billId, cancellationToken).ConfigureAwait(false)], cancellationToken)
            .ConfigureAwait(false))[0];
    }

    /// <summary>A manager has checked what was flagged on a posted offline bill.</summary>
    public async Task<OfflineBillDto> ReviewAsync(Guid businessId, Guid billId, ReviewOfflineBillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var record = await LoadAsync(businessId, billId, request.RowVersion, cancellationToken).ConfigureAwait(false);
        Valid(() => record.Reviewed(request.Note, currentUser.UserId, clock.GetUtcNow()));
        audit.Record("sales.offline_bill_reviewed", "offline_bill", record.Id, businessId, record.StoreId, details: new { record.Number, request.Note });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await DtosAsync([record], cancellationToken).ConfigureAwait(false))[0];
    }

    private async Task<OfflineBillRecord> LoadAsync(Guid businessId, Guid billId, uint rowVersion, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.ShiftsManage, businessId, cancellationToken).ConfigureAwait(false);
        var record = await db.OfflineBills.FirstOrDefaultAsync(b => b.Id == billId && b.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Offline bill");
        return record.RowVersion == rowVersion
            ? record
            : throw AppException.Conflict("concurrency.conflict", "This record was changed by someone else. Reload it and try again.");
    }

    // Helpers

    private async Task<long> NextNumberAsync(Guid storeId, string prefix, CancellationToken cancellationToken)
    {
        var series = Counter.InvoiceSeries(prefix);
        return await db.DocumentSequences.AsNoTracking().Where(s => s.StoreId == storeId && s.Series == series).Select(s => (long?)s.NextNumber)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 1;
    }

    /// <summary>The seller as the server's own billing puts it on an invoice.</summary>
    private static SalesInvoice.Seller Seller(string legalName, Store store, string? businessAddress, TaxRegistration registration)
    {
        var gstin = registration.Mode == TaxRegistrationModes.NotGstRegistered ? null : store.Gstin ?? registration.Gstin;
        if (registration.Mode != TaxRegistrationModes.NotGstRegistered && gstin is null)
        {
            throw AppException.Conflict("gstin.missing", "The store has no GSTIN on record, so a GST invoice cannot be issued.");
        }

        return new SalesInvoice.Seller(legalName, gstin, store.Address ?? businessAddress ?? store.Name, store.StateCode);
    }

    private async Task<List<OfflineBillDto>> DtosAsync(List<OfflineBillRecord> list, CancellationToken cancellationToken)
    {
        var counterIds = list.Select(b => b.CounterId).Distinct().ToList();
        var codes = await db.Counters.AsNoTracking().Where(c => counterIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken).ConfigureAwait(false);
        var deviceIds = list.Select(b => b.DeviceId).Distinct().ToList();
        var devices = await db.CounterDevices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken)
            .ConfigureAwait(false);
        var userIds = list.Select(b => b.CashierUserId).Concat(list.Where(b => b.ReviewedByUserId != null).Select(b => b.ReviewedByUserId!.Value))
            .Concat(list.Where(b => b.ResolvedByUserId != null).Select(b => b.ResolvedByUserId!.Value)).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
        return list.Select(b => new OfflineBillDto(b.Id, b.Number, b.CounterId, codes.GetValueOrDefault(b.CounterId, "?"), devices.GetValueOrDefault(b.DeviceId, "?"),
            names.GetValueOrDefault(b.CashierUserId, "?"), b.IssuedAtUtc, b.ReceivedAtUtc, b.GrandTotal, b.Status, b.Reason, b.Review,
            b.ReviewedByUserId is { } r ? names.GetValueOrDefault(r) : null, b.ReviewedAtUtc, b.InvoiceId, b.ResolvedByUserId is { } u ? names.GetValueOrDefault(u) : null,
            b.ResolvedAtUtc, b.ResolutionNote, b.RowVersion)).ToList();
    }

    private async Task RequireAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        if (!(await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false)).Contains(businessId))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static T Valid<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }
    }

    private static void Valid(Action action) => Valid(() =>
    {
        action();
        return 0;
    });
}
