using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.Domain.Sales;

/// <summary>What the bill is: which document a sale produces, by the seller's registration and the lines on it.</summary>
public static class InvoiceKinds
{
    /// <summary>GST-registered (regular) seller, at least one taxable line.</summary>
    public const string TaxInvoice = "TAX_INVOICE";

    /// <summary>Composition seller, or a regular seller selling only exempt, nil-rated or non-GST goods.</summary>
    public const string BillOfSupply = "BILL_OF_SUPPLY";

    /// <summary>Seller not registered for GST: a commercial invoice with no tax.</summary>
    public const string Invoice = "INVOICE";

    public static string For(string taxMode, IEnumerable<string> lineSupplyTypes) => taxMode switch
    {
        TaxRegistrationModes.GstRegular => lineSupplyTypes.Any(t => t == SupplyTypes.Taxable) ? TaxInvoice : BillOfSupply,
        TaxRegistrationModes.GstComposition => BillOfSupply,
        TaxRegistrationModes.NotGstRegistered => Invoice,
        _ => throw new DomainException("tax_mode.invalid", $"Unknown tax mode '{taxMode}'."),
    };

    /// <summary>Declaration that a composition taxable person must print on a bill of supply.</summary>
    public const string CompositionDeclaration = "Composition taxable person, not eligible to collect tax on supplies.";
}

/// <summary>One line as priced: quantity in its pack, the price per pack, and the tax rates of the product.</summary>
/// <param name="ItemDiscount">Discount on this line, in the same terms as the price (tax-inclusive if the price is).</param>
public sealed record BillLineInput(
    decimal Quantity, decimal UnitPrice, bool TaxInclusive, string SupplyType, decimal GstRatePercent, decimal CessRatePercent, decimal ItemDiscount);

public sealed record BillInput(string TaxMode, bool InterState, IReadOnlyList<BillLineInput> Lines, decimal BillDiscount);

public sealed record BillLineResult(
    decimal Gross, decimal ItemDiscount, decimal BillDiscount, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Cess, decimal Total)
{
    public decimal Tax => Cgst + Sgst + Igst + Cess;
}

public sealed record BillResult(
    string Kind, IReadOnlyList<BillLineResult> Lines, decimal Gross, decimal Discount, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst,
    decimal Cess, decimal RoundOff, decimal GrandTotal);

/// <summary>
/// Computes a bill exactly, in paise, the same way every time. The server always recomputes; the client's figures
/// are only a preview.
/// <list type="bullet">
/// <item>Line gross = quantity x price, rounded to paise. The item discount comes off the line; the bill discount
/// is shared across lines in proportion to what remains (largest remainder, so the shares add up exactly).</item>
/// <item>Tax-inclusive lines: the tax is carved out of the amount, so the amount the customer pays for the line is
/// exactly the discounted price. Tax-exclusive lines: tax is added to the discounted amount.</item>
/// <item>CGST and SGST are each half the GST rate and are always equal; IGST for inter-state supplies.</item>
/// <item>Only a regular GST seller charges tax, and only on taxable lines. A composition seller and an unregistered
/// seller sell at the price: no tax is shown or collected.</item>
/// <item>The grand total is rounded to the nearest rupee; the difference is shown as round-off.</item>
/// </list>
/// </summary>
public static class InvoiceCalculator
{
    public static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static BillResult Calculate(BillInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Lines.Count == 0)
        {
            throw new DomainException("invoice.lines_required", "A bill needs at least one item.");
        }

        var kind = InvoiceKinds.For(input.TaxMode, input.Lines.Select(l => l.SupplyType));
        var collectsTax = input.TaxMode == TaxRegistrationModes.GstRegular;

        var afterItemDiscount = new decimal[input.Lines.Count];
        var gross = new decimal[input.Lines.Count];
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var line = input.Lines[i];
            if (line.Quantity <= 0)
            {
                throw new DomainException("invoice.quantity_invalid", "Quantities must be more than zero.");
            }

            if (line.UnitPrice < 0 || line.ItemDiscount < 0)
            {
                throw new DomainException("invoice.amount_invalid", "Prices and discounts cannot be negative.");
            }

            gross[i] = Money(line.Quantity * line.UnitPrice);
            if (line.ItemDiscount > gross[i])
            {
                throw new DomainException("invoice.discount_too_large", "A discount cannot be more than the line amount.");
            }

            afterItemDiscount[i] = gross[i] - Money(line.ItemDiscount);
        }

        if (input.BillDiscount < 0 || Money(input.BillDiscount) > afterItemDiscount.Sum())
        {
            throw new DomainException("invoice.discount_too_large", "The bill discount cannot be negative or more than the bill.");
        }

        var shares = Apportion(Money(input.BillDiscount), afterItemDiscount);
        var lines = new List<BillLineResult>(input.Lines.Count);
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var line = input.Lines[i];
            var net = afterItemDiscount[i] - shares[i];
            lines.Add(collectsTax && line.SupplyType == SupplyTypes.Taxable
                ? Taxed(line, gross[i], shares[i], net, input.InterState)
                : new BillLineResult(gross[i], Money(line.ItemDiscount), shares[i], net, 0, 0, 0, 0, net));
        }

        var beforeRounding = lines.Sum(l => l.Total);
        var grandTotal = decimal.Round(beforeRounding, 0, MidpointRounding.AwayFromZero);
        return new BillResult(
            kind,
            lines,
            lines.Sum(l => l.Gross),
            lines.Sum(l => l.ItemDiscount + l.BillDiscount),
            lines.Sum(l => l.Taxable),
            lines.Sum(l => l.Cgst),
            lines.Sum(l => l.Sgst),
            lines.Sum(l => l.Igst),
            lines.Sum(l => l.Cess),
            grandTotal - beforeRounding,
            grandTotal);
    }

    private static BillLineResult Taxed(BillLineInput line, decimal gross, decimal billShare, decimal net, bool interState)
    {
        var gstRate = line.GstRatePercent;
        var cessRate = line.CessRatePercent;
        decimal taxable;
        if (line.TaxInclusive)
        {
            // Carve the tax out of the amount paid, then let the taxable value absorb the paise so that
            // taxable + taxes is exactly the amount paid.
            var estimate = Money(net * 100 / (100 + gstRate + cessRate));
            var (cgst, sgst, igst) = SplitGst(estimate, gstRate, interState);
            var cess = Money(estimate * cessRate / 100);
            taxable = net - cgst - sgst - igst - cess;
            return new BillLineResult(gross, Money(line.ItemDiscount), billShare, taxable, cgst, sgst, igst, cess, net);
        }

        taxable = net;
        var (c, s, ig) = SplitGst(taxable, gstRate, interState);
        var cessAmount = Money(taxable * cessRate / 100);
        return new BillLineResult(gross, Money(line.ItemDiscount), billShare, taxable, c, s, ig, cessAmount, taxable + c + s + ig + cessAmount);
    }

    private static (decimal Cgst, decimal Sgst, decimal Igst) SplitGst(decimal taxable, decimal gstRate, bool interState)
    {
        if (interState)
        {
            return (0, 0, Money(taxable * gstRate / 100));
        }

        var half = Money(taxable * gstRate / 200);
        return (half, half, 0);
    }

    /// <summary>Shares <paramref name="amount"/> in proportion to <paramref name="weights"/>, in paise, adding up exactly.</summary>
    public static decimal[] Apportion(decimal amount, IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var shares = new decimal[weights.Count];
        var total = weights.Sum();
        if (amount == 0 || total == 0)
        {
            return shares;
        }

        var paise = (long)(amount * 100);
        var exact = weights.Select(w => paise * w / total).ToArray();
        var floors = exact.Select(e => decimal.Floor(e)).ToArray();
        var left = paise - (long)floors.Sum();
        foreach (var index in exact.Select((e, i) => (Remainder: e - floors[i], Index: i)).OrderByDescending(x => x.Remainder).ThenBy(x => x.Index).Take((int)left))
        {
            floors[index.Index] += 1;
        }

        for (var i = 0; i < shares.Length; i++)
        {
            shares[i] = floors[i] / 100;
        }

        return shares;
    }
}
