using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.Domain.Purchases;

/// <summary>How an inward purchase is documented (spec section 7). It decides whether its GST can be recovered.</summary>
public static class PurchaseClassifications
{
    public const string GstTaxInvoice = "GST_TAX_INVOICE";
    public const string BillOfSupply = "BILL_OF_SUPPLY";
    public const string Unregistered = "UNREGISTERED";
    public const string Import = "IMPORT";
    public const string ReverseCharge = "REVERSE_CHARGE";
    public const string PendingDocument = "PENDING_DOCUMENT";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All = [GstTaxInvoice, BillOfSupply, Unregistered, Import, ReverseCharge, PendingDocument, Other];

    /// <summary>
    /// Whether the GST paid can be claimed back as input tax credit (and so is not part of the cost): only a regular
    /// GST business, and only on a tax invoice, an import (IGST on the bill of entry) or reverse charge. Everything
    /// else, including a purchase whose document is still pending, carries its tax as cost.
    /// </summary>
    public static bool TaxRecoverable(string classification, string businessTaxMode) =>
        businessTaxMode == TaxRegistrationModes.GstRegular && classification is GstTaxInvoice or Import or ReverseCharge;

    /// <summary>Whether the supplier's document carries GST at all (a bill of supply or an unregistered supplier's bill does not).</summary>
    public static bool ChargesGst(string classification) => classification is GstTaxInvoice or Import or ReverseCharge or PendingDocument or Other;
}

public static class ExpenseKinds
{
    public static readonly IReadOnlyList<string> All = ["FREIGHT", "LOADING", "INSURANCE", "PACKING", "HANDLING", "TRANSPORT", "CUSTOMS", "OTHER"];
}

public static class AllocationMethods
{
    public const string Quantity = "QUANTITY";
    public const string Value = "VALUE";
    public const string Weight = "WEIGHT";
    public const string Volume = "VOLUME";
    public const string Equal = "EQUAL";
    public const string Manual = "MANUAL";

    public static readonly IReadOnlyList<string> All = [Quantity, Value, Weight, Volume, Equal, Manual];
}

/// <param name="Quantity">Paid quantity, in the pack received.</param>
/// <param name="FreeQuantity">Free quantity in the same pack (shares the cost).</param>
/// <param name="FactorToBase">Stock units per pack.</param>
/// <param name="Rate">Basic cost per pack, before discount and tax.</param>
public sealed record GrnLineInput(
    decimal Quantity, decimal FreeQuantity, decimal FactorToBase, decimal Rate, decimal Discount, decimal GstRatePercent, decimal CessRatePercent,
    decimal? Weight = null, decimal? Volume = null);

/// <param name="Manual">For <see cref="AllocationMethods.Manual"/>: the amount for each line, in line order.</param>
public sealed record GrnExpenseInput(string Kind, decimal Amount, string Method, IReadOnlyList<decimal>? Manual = null);

public sealed record GrnInput(IReadOnlyList<GrnLineInput> Lines, IReadOnlyList<GrnExpenseInput> Expenses, bool InterState, bool ChargesGst, bool TaxRecoverable);

public sealed record GrnLineResult(
    decimal Gross, decimal Discount, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Cess, decimal Total, decimal ExpenseShare,
    decimal NonRecoverableTax, decimal LandedTotal, decimal BaseQuantity, decimal LandedUnitCost)
{
    public decimal Tax => Cgst + Sgst + Igst + Cess;
}

public sealed record GrnResult(
    IReadOnlyList<GrnLineResult> Lines, IReadOnlyList<IReadOnlyList<decimal>> Allocations, decimal Gross, decimal Discount, decimal Taxable, decimal Cgst,
    decimal Sgst, decimal Igst, decimal Cess, decimal InvoiceTotal, decimal Expenses, decimal LandedTotal);

/// <summary>
/// Computes a goods receipt exactly: each line's value after discount, its GST (CGST = SGST, or IGST), each expense
/// shared across the lines by its method (to the paisa, adding up exactly), and the landed cost per stock unit:
/// value + GST that cannot be recovered + share of expenses, spread over everything received including free goods.
/// </summary>
public static class GrnCalculator
{
    public static GrnResult Calculate(GrnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Lines.Count == 0)
        {
            throw new DomainException("grn.lines_required", "A goods receipt needs at least one item.");
        }

        var lines = new List<(decimal Gross, decimal Discount, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Cess, decimal BaseQuantity)>();
        foreach (var l in input.Lines)
        {
            if (l.Quantity < 0 || l.FreeQuantity < 0 || l.Quantity + l.FreeQuantity <= 0 || l.FactorToBase <= 0)
            {
                throw new DomainException("grn.quantity_invalid", "Each item needs a quantity (paid or free) above zero.");
            }

            if (l.Rate < 0 || l.Discount < 0 || l.Rate != StockMath.Cost(l.Rate))
            {
                throw new DomainException("grn.rate_invalid", "Rates and discounts cannot be negative (at most 4 decimals for a rate).");
            }

            var gross = InvoiceCalculator.Money(l.Quantity * l.Rate);
            var discount = InvoiceCalculator.Money(l.Discount);
            if (discount > gross)
            {
                throw new DomainException("grn.discount_too_large", "A discount cannot be more than the line value.");
            }

            var taxable = gross - discount;
            decimal cgst = 0, sgst = 0, igst = 0, cess = 0;
            if (input.ChargesGst)
            {
                if (input.InterState)
                {
                    igst = InvoiceCalculator.Money(taxable * l.GstRatePercent / 100);
                }
                else
                {
                    cgst = sgst = InvoiceCalculator.Money(taxable * l.GstRatePercent / 200);
                }

                cess = InvoiceCalculator.Money(taxable * l.CessRatePercent / 100);
            }

            lines.Add((gross, discount, taxable, cgst, sgst, igst, cess, StockMath.Quantity((l.Quantity + l.FreeQuantity) * l.FactorToBase)));
        }

        var allocations = new List<IReadOnlyList<decimal>>();
        foreach (var expense in input.Expenses)
        {
            allocations.Add(Allocate(expense, input.Lines, lines.Select(l => l.Taxable).ToList(), lines.Select(l => l.BaseQuantity).ToList()));
        }

        var results = new List<GrnLineResult>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var share = allocations.Sum(a => a[i]);
            var tax = l.Cgst + l.Sgst + l.Igst + l.Cess;
            var nonRecoverable = input.TaxRecoverable ? 0 : tax;
            var landed = l.Taxable + nonRecoverable + share;
            results.Add(new GrnLineResult(l.Gross, l.Discount, l.Taxable, l.Cgst, l.Sgst, l.Igst, l.Cess, l.Taxable + tax, share, nonRecoverable, landed,
                l.BaseQuantity, StockMath.Cost(landed / l.BaseQuantity)));
        }

        return new GrnResult(results, allocations, results.Sum(r => r.Gross), results.Sum(r => r.Discount), results.Sum(r => r.Taxable), results.Sum(r => r.Cgst),
            results.Sum(r => r.Sgst), results.Sum(r => r.Igst), results.Sum(r => r.Cess), results.Sum(r => r.Total), input.Expenses.Sum(e => e.Amount),
            results.Sum(r => r.LandedTotal));
    }

    private static decimal[] Allocate(GrnExpenseInput expense, IReadOnlyList<GrnLineInput> lines, List<decimal> values, List<decimal> quantities)
    {
        if (!ExpenseKinds.All.Contains(expense.Kind))
        {
            throw new DomainException("grn.expense_kind_invalid", $"Unknown expense '{expense.Kind}'.");
        }

        if (expense.Amount <= 0 || expense.Amount != InvoiceCalculator.Money(expense.Amount))
        {
            throw new DomainException("grn.expense_amount_invalid", "Each expense must be a positive amount in rupees and paise.");
        }

        IReadOnlyList<decimal> weights = expense.Method switch
        {
            AllocationMethods.Quantity => quantities,
            AllocationMethods.Value => values,
            AllocationMethods.Equal => lines.Select(_ => 1m).ToList(),
            AllocationMethods.Weight => lines.Select(l => l.Weight is > 0 ? l.Weight.Value : throw new DomainException("grn.weight_required", "Allocating by weight needs every item's weight.")).ToList(),
            AllocationMethods.Volume => lines.Select(l => l.Volume is > 0 ? l.Volume.Value : throw new DomainException("grn.volume_required", "Allocating by volume needs every item's volume.")).ToList(),
            AllocationMethods.Manual => expense.Manual is { } manual && manual.Count == lines.Count && manual.All(m => m >= 0 && m == InvoiceCalculator.Money(m))
                ? manual
                : throw new DomainException("grn.manual_allocation_invalid", "Give an amount in rupees and paise for every item."),
            _ => throw new DomainException("grn.allocation_method_invalid", $"Unknown allocation method '{expense.Method}'."),
        };

        if (expense.Method == AllocationMethods.Manual)
        {
            return weights.Sum() == expense.Amount
                ? [.. weights]
                : throw new DomainException("grn.manual_allocation_mismatch", $"The amounts given ({weights.Sum():0.00}) must add up to the expense ({expense.Amount:0.00}).");
        }

        if (weights.Sum() <= 0)
        {
            throw new DomainException("grn.allocation_impossible", "There is nothing to share this expense over (all weights are zero).");
        }

        return InvoiceCalculator.Apportion(expense.Amount, weights);
    }
}
