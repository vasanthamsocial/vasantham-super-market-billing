using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.Domain.Sales;

/// <summary>
/// The invoice series a counter issues from while it cannot reach the server (D-039): the counter's prefix followed by
/// "/OF" (<c>C1/OF-000001</c>). The slash cannot appear in a counter code, so this series can never be another
/// counter's; and only the counter agent of the one device allowed to bill offline numbers it, so it never clashes
/// with the numbers the server gives.
/// </summary>
public static class OfflineSeries
{
    public const string Marker = "/OF";

    public static string Prefix(string counterPrefix) => counterPrefix + Marker;

    public static bool IsOffline(string numberPrefix) => numberPrefix.EndsWith(Marker, StringComparison.Ordinal);

    /// <summary>GST allows at most 16 characters: a counter whose prefix is too long cannot have an offline series.</summary>
    public static bool Fits(string counterPrefix) => Counter.InvoiceNumber(Prefix(counterPrefix), 1).Length <= 16;
}

/// <summary>How much a counter may bill without the server, set by a manager on its device.</summary>
public sealed record OfflineLimits(int MaxBills, decimal MaxAmount, int MaxHours)
{
    public void Validate()
    {
        if (MaxBills is < 1 or > 2000)
        {
            throw new DomainException("offline.bills_invalid", "Allow 1 to 2,000 bills without the server.");
        }

        if (MaxAmount <= 0 || MaxAmount > 10_000_000m || decimal.Round(MaxAmount, 2) != MaxAmount)
        {
            throw new DomainException("offline.amount_invalid", "The most a counter may bill without the server is a positive amount in rupees and paise (at most Rs. 1 crore).");
        }

        if (MaxHours is < 1 or > 72)
        {
            throw new DomainException("offline.hours_invalid", "Allow 1 to 72 hours without the server.");
        }
    }
}

/// <summary>A pack (unit of a variant) as the counter may sell it offline.</summary>
public sealed record OfflinePackItem(
    Guid VariantUnitId, Guid VariantId, Guid ProductId, string Name, string UnitCode, string BaseUnitCode, int BaseDecimals, decimal FactorToBase,
    string HsnSac, string SupplyType, decimal GstRatePercent, decimal CessRatePercent, IReadOnlyList<decimal> Mrps, IReadOnlyList<string> Barcodes);

/// <summary>A price rule as handed to the counter (only rules any walk-in customer can get at this store).</summary>
public sealed record OfflinePackPrice(
    Guid Id, Guid VariantUnitId, string RateType, string Channel, decimal Price, bool TaxInclusive, decimal? Mrp, Guid? StoreId, decimal MinQuantity,
    decimal? MaxQuantity, DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, int Priority);

/// <summary>
/// Everything a counter agent needs to bill on its own for a while: who and where, the series and its next number,
/// the seller's details and registration, the limits, and the items with their prices. Made by the server for one
/// device, cashier and shift; the agent uses only the newest it was given.
/// </summary>
public sealed record OfflinePack(
    Guid PackId, DateTimeOffset CreatedAtUtc, Guid BusinessId, Guid StoreId, string StoreTimeZone, Guid CounterId, string CounterCode, Guid DeviceId,
    Guid CashierUserId, string CashierName, Guid ShiftId, string TaxMode, string NumberPrefix, long NextSequence, SalesInvoice.Seller Seller,
    OfflineLimits Limits, IReadOnlyList<OfflinePackItem> Items, IReadOnlyList<OfflinePackPrice> Prices);

public sealed record OfflineCartLine(Guid VariantUnitId, decimal Quantity, decimal? Mrp = null);

public sealed record OfflineBuyer(string? Name, string? Gstin, string? Phone, string? Address, string? StateCode);

public sealed record OfflineCart(string Channel, IReadOnlyList<OfflineCartLine> Lines, OfflineBuyer? Buyer = null);

/// <summary>A line as issued offline: the pack, the rule that priced it, the tax rates, and the amounts.</summary>
public sealed record OfflineBillLine(
    int LineNumber, Guid VariantUnitId, Guid VariantId, Guid ProductId, string Description, string HsnSac, string UnitCode, decimal Quantity, decimal BaseQuantity,
    decimal? Mrp, Guid PriceRuleId, string RateType, decimal UnitPrice, bool TaxInclusive, string SupplyType, decimal GstRatePercent, decimal CessRatePercent,
    BillLineResult Amounts);

/// <summary>A cart priced from the pack: what the counter shows and, once paid, issues.</summary>
public sealed record OfflineDraft(
    string Channel, string Kind, string PlaceOfSupply, bool IsInterState, SalesInvoice.Buyer Buyer, IReadOnlyList<OfflineBillLine> Lines, BillResult Result);

/// <summary>
/// An invoice issued by the counter agent without the server: its id (also the idempotency key), its number in the
/// offline series, when and by whom, and exactly what was printed for the customer.
/// </summary>
public sealed record OfflineBill(
    Guid Id, Guid PackId, Guid DeviceId, string NumberPrefix, long Sequence, string Number, DateTimeOffset IssuedAtUtc, DateOnly BusinessDate,
    Guid CashierUserId, Guid ShiftId, string TaxMode, string Kind, string Channel, SalesInvoice.Seller Seller, SalesInvoice.Buyer Buyer, string PlaceOfSupply,
    IReadOnlyList<OfflineBillLine> Lines, decimal GrossTotal, decimal TaxableTotal, decimal CgstTotal, decimal SgstTotal, decimal IgstTotal, decimal CessTotal,
    decimal RoundOff, decimal GrandTotal, IReadOnlyList<PaymentInput> Payments, decimal ChangeDue);

/// <summary>
/// Offline counter billing (D-039), shared by the counter agent (which prices and issues) and the server (which checks
/// what arrives): the same price resolution and invoice arithmetic as the server's own billing, from the pack.
/// No discounts, overrides, credit or store credit offline: cash, card or UPI only.
/// </summary>
public static class OfflineBilling
{
    public static readonly IReadOnlyList<string> Methods = [PaymentMethods.Cash, PaymentMethods.Card, PaymentMethods.Upi];

    public static DateOnly BusinessDate(DateTimeOffset at, string timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);

    /// <summary>Prices a cart from the pack at a moment, as the server would with the same rules.</summary>
    public static OfflineDraft Price(OfflinePack pack, OfflineCart cart, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(cart);
        var channel = (cart.Channel ?? string.Empty).Trim().ToUpperInvariant();
        if (channel is not (SalesChannels.Retail or SalesChannels.Wholesale))
        {
            throw new DomainException("invoice.channel_invalid", "Choose retail or wholesale billing.");
        }

        if (cart.Lines is null || cart.Lines.Count == 0 || cart.Lines.Count > 300)
        {
            throw new DomainException("invoice.lines_required", "A bill needs 1 to 300 items.");
        }

        var (buyer, placeOfSupply) = Buyer(cart.Buyer, pack.Seller.StateCode);
        if (pack.TaxMode == TaxRegistrationModes.GstComposition && placeOfSupply != pack.Seller.StateCode)
        {
            throw new DomainException("composition.inter_state", "A composition dealer cannot sell to another state.");
        }

        var items = pack.Items.ToDictionary(i => i.VariantUnitId);
        var lines = new List<OfflineBillLine>();
        var inputs = new List<BillLineInput>();
        foreach (var request in cart.Lines)
        {
            var item = items.GetValueOrDefault(request.VariantUnitId) ?? throw new DomainException("item.unknown", "This item is not in the offline price list.");
            if (request.Quantity <= 0)
            {
                throw new DomainException("invoice.quantity_invalid", $"{item.Name}: the quantity must be more than zero.");
            }

            var baseQuantity = request.Quantity * item.FactorToBase;
            if (StockMath.Quantity(baseQuantity) != baseQuantity || decimal.Round(baseQuantity, item.BaseDecimals) != baseQuantity)
            {
                throw new DomainException("invoice.quantity_precision", $"{item.Name}: at most {item.BaseDecimals} decimals in {item.BaseUnitCode}.");
            }

            var mrp = ChooseMrp(item.Name, request.Mrp, item.Mrps);
            var taxRate = item.GstRatePercent + item.CessRatePercent;
            var rules = pack.Prices.Where(p => p.VariantUnitId == item.VariantUnitId)
                .Select(p => PriceRule.Restore(p.Id, p.VariantUnitId, p.RateType, p.Channel, p.Price, p.TaxInclusive, p.Mrp, p.StoreId, p.MinQuantity, p.MaxQuantity,
                    p.ValidFromUtc, p.ValidToUtc, p.Priority));
            var rule = PriceResolver.Resolve(rules, new PriceQuery(item.VariantUnitId, request.Quantity, channel, pack.StoreId, null, false, mrp, taxRate, now)).Rule
                ?? throw new DomainException("price.missing", $"{item.Name} has no price for {channel.ToLowerInvariant()} billing in the offline price list.");
            var collectsTax = pack.TaxMode == TaxRegistrationModes.GstRegular && item.SupplyType == SupplyTypes.Taxable;
            var inclusive = collectsTax ? PriceMath.InclusiveOf(rule.Price, rule.TaxInclusive, taxRate) : rule.Price;
            if (mrp is { } cap && inclusive > cap)
            {
                throw new DomainException("price.above_mrp", $"{item.Name}: Rs. {inclusive:0.00} is above the MRP of Rs. {cap:0.00}.");
            }

            var input = new BillLineInput(request.Quantity, rule.Price, rule.TaxInclusive, item.SupplyType, item.GstRatePercent, item.CessRatePercent, 0);
            inputs.Add(input);
            lines.Add(new OfflineBillLine(lines.Count + 1, item.VariantUnitId, item.VariantId, item.ProductId, item.Name, item.HsnSac, item.UnitCode, request.Quantity,
                baseQuantity, mrp, rule.Id, rule.RateType, rule.Price, rule.TaxInclusive, item.SupplyType, item.GstRatePercent, item.CessRatePercent, null!));
        }

        var result = InvoiceCalculator.Calculate(new BillInput(pack.TaxMode, placeOfSupply != pack.Seller.StateCode, inputs, 0));
        var priced = lines.Select((l, i) => l with { Amounts = result.Lines[i] }).ToList();
        return new OfflineDraft(channel, result.Kind, placeOfSupply, placeOfSupply != pack.Seller.StateCode, buyer, priced, result);
    }

    /// <summary>Issues the priced cart as an invoice in the offline series, paid in cash, card or UPI.</summary>
    public static OfflineBill Issue(OfflinePack pack, OfflineDraft draft, IReadOnlyList<PaymentInput> payments, Guid id, long sequence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(payments);
        if (payments.Any(p => !Methods.Contains(p.Method)))
        {
            throw new DomainException("offline.method_not_allowed", "Without the server, bills are paid in cash, by card or by UPI only.");
        }

        if (payments.Any(p => p.Method != PaymentMethods.Cash && string.IsNullOrWhiteSpace(p.Reference)))
        {
            throw new DomainException("offline.reference_required", "Enter the card or UPI reference: without the server it is the only record of the payment.");
        }

        var change = PaymentRules.ChangeDue(draft.Result.GrandTotal, payments);
        var r = draft.Result;
        return new OfflineBill(id, pack.PackId, pack.DeviceId, pack.NumberPrefix, sequence, Counter.InvoiceNumber(pack.NumberPrefix, sequence), now,
            BusinessDate(now, pack.StoreTimeZone), pack.CashierUserId, pack.ShiftId, pack.TaxMode, r.Kind, draft.Channel, pack.Seller, draft.Buyer, draft.PlaceOfSupply,
            draft.Lines, r.Gross, r.Taxable, r.Cgst, r.Sgst, r.Igst, r.Cess, r.RoundOff, r.GrandTotal, payments, change);
    }

    /// <summary>
    /// Whether another bill of this amount may be issued now: the pack is recent enough and within the limits on bills,
    /// amount, and how long the oldest unsent bill has waited. Returns why not, or null.
    /// </summary>
    public static string? Refusal(OfflinePack pack, IReadOnlyCollection<OfflineBill> pending, decimal amount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(pending);
        var limits = pack.Limits;
        var hours = TimeSpan.FromHours(limits.MaxHours);
        if (now - pack.CreatedAtUtc > hours)
        {
            return $"The offline price list is more than {limits.MaxHours} hours old. Bill again when the server is back.";
        }

        if (pending.Count >= limits.MaxBills)
        {
            return $"This counter may hold at most {limits.MaxBills} bills without the server. Wait for the server.";
        }

        var held = pending.Sum(b => b.GrandTotal);
        if (held + amount > limits.MaxAmount)
        {
            return $"This counter may hold at most Rs. {limits.MaxAmount:0.00} in bills without the server (Rs. {held:0.00} is waiting).";
        }

        var oldest = pending.Count == 0 ? (DateTimeOffset?)null : pending.Min(b => b.IssuedAtUtc);
        return oldest is { } first && now - first > hours
            ? $"Bills have waited more than {limits.MaxHours} hours for the server. Bill again when it is back."
            : null;
    }

    /// <summary>
    /// The bill's arithmetic redone from its own lines (quantity, price, tax rates): the server posts a bill only if this
    /// gives exactly what was printed. Returns why not, or null.
    /// </summary>
    public static string? Inconsistency(OfflineBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Lines.Count is 0 or > 300)
        {
            return "The bill has no items (or too many).";
        }

        if (bill.Number != Counter.InvoiceNumber(bill.NumberPrefix, bill.Sequence) || !OfflineSeries.IsOffline(bill.NumberPrefix))
        {
            return "The number is not in the counter's offline series.";
        }

        if (bill.Lines.Select((l, i) => l.LineNumber != i + 1).Any(wrong => wrong))
        {
            return "The lines are not numbered 1, 2, 3...";
        }

        BillResult result;
        try
        {
            result = InvoiceCalculator.Calculate(new BillInput(bill.TaxMode, bill.PlaceOfSupply != bill.Seller.StateCode,
                bill.Lines.Select(l => new BillLineInput(l.Quantity, l.UnitPrice, l.TaxInclusive, l.SupplyType, l.GstRatePercent, l.CessRatePercent, 0)).ToList(), 0));
        }
        catch (DomainException e)
        {
            return e.Message;
        }

        var linesMatch = bill.Lines.Select((l, i) => l.Amounts == result.Lines[i]).All(same => same);
        var totalsMatch = (bill.Kind, bill.GrossTotal, bill.TaxableTotal, bill.CgstTotal, bill.SgstTotal, bill.IgstTotal, bill.CessTotal, bill.RoundOff, bill.GrandTotal)
                          == (result.Kind, result.Gross, result.Taxable, result.Cgst, result.Sgst, result.Igst, result.Cess, result.RoundOff, result.GrandTotal);
        if (!linesMatch || !totalsMatch)
        {
            return "The bill's amounts do not add up to what its items and prices give.";
        }

        if (bill.Payments.Any(p => !Methods.Contains(p.Method)))
        {
            return "A payment method other than cash, card or UPI.";
        }

        try
        {
            return PaymentRules.ChangeDue(result.GrandTotal, bill.Payments) == bill.ChangeDue ? null : "The change given does not match the payments.";
        }
        catch (DomainException e)
        {
            return e.Message;
        }
    }

    public static decimal? ChooseMrp(string name, decimal? requested, IReadOnlyList<decimal> active)
    {
        ArgumentNullException.ThrowIfNull(active);
        if (requested is { } mrp)
        {
            return active.Contains(mrp) ? mrp : throw new DomainException("mrp.unknown", $"{name} has no MRP of Rs. {mrp:0.00}.");
        }

        return active.Distinct().Count() switch
        {
            0 => null,
            1 => active[0],
            _ => throw new DomainException("mrp.choose", $"{name} has more than one MRP ({string.Join(", ", active.Distinct().Order().Select(m => $"Rs. {m:0.00}"))}). Choose the one on the pack."),
        };
    }

    private static (SalesInvoice.Buyer Buyer, string PlaceOfSupply) Buyer(OfflineBuyer? request, string storeState)
    {
        if (request is null)
        {
            return (new SalesInvoice.Buyer(null, null, null, null), storeState);
        }

        string? gstin = null;
        var state = string.IsNullOrWhiteSpace(request.StateCode) ? storeState : request.StateCode.Trim();
        if (!string.IsNullOrWhiteSpace(request.Gstin))
        {
            gstin = Gstin.Normalize(request.Gstin);
            if (!Gstin.IsValid(gstin))
            {
                throw new DomainException("buyer.gstin_invalid", "The buyer's GSTIN is not valid (check the 15 characters).");
            }

            state = Gstin.StateCode(gstin);
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                throw new DomainException("buyer.name_required", "A buyer with a GSTIN needs their name on the invoice.");
            }
        }

        return state.Length == 2 && state.All(char.IsAsciiDigit)
            ? (new SalesInvoice.Buyer(request.Name, gstin, request.Phone, request.Address), state)
            : throw new DomainException("buyer.state_invalid", "The place of supply is a two-digit state code.");
    }
}
