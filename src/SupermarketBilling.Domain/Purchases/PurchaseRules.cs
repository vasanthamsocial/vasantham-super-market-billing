using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.Domain.Purchases;

/// <summary>A change of cost against the last receipt of the same item and MRP (spec section 12).</summary>
public sealed record CostChange(decimal PreviousUnitCost, decimal NewUnitCost, decimal Difference, decimal PercentChange, bool NeedsReason, bool NeedsApproval);

/// <summary>Selling below the landed cost: what each pack would lose, and the margin.</summary>
public sealed record BelowCost(decimal CostPerPack, decimal SellingNetPerPack, decimal LossPerPack, decimal MarginPercent);

public static class PurchaseRules
{
    /// <summary>
    /// Compares the new landed cost per stock unit with the previous one. Above <paramref name="reasonThresholdPercent"/>
    /// (either way) the receiver must give a reason; above <paramref name="approvalThresholdPercent"/> a manager approves.
    /// </summary>
    public static CostChange? CompareCost(decimal? previousUnitCost, decimal newUnitCost, decimal reasonThresholdPercent, decimal approvalThresholdPercent)
    {
        if (previousUnitCost is not { } previous || previous <= 0 || previous == newUnitCost)
        {
            return null;
        }

        var difference = newUnitCost - previous;
        var percent = decimal.Round(difference / previous * 100, 2, MidpointRounding.AwayFromZero);
        var size = Math.Abs(percent);
        return new CostChange(previous, newUnitCost, difference, percent, size >= reasonThresholdPercent, size >= approvalThresholdPercent);
    }

    /// <summary>
    /// The selling price against the landed cost, per pack. For a regular GST seller the price includes output GST,
    /// which is not the seller's money, so the price is compared without it.
    /// </summary>
    public static BelowCost? CheckSellingPrice(decimal landedUnitCost, decimal factorToBase, decimal sellingPriceInclusive, decimal outputTaxRatePercent, bool sellerCollectsTax)
    {
        var cost = InvoiceCalculator.Money(landedUnitCost * factorToBase);
        var net = sellerCollectsTax ? InvoiceCalculator.Money(sellingPriceInclusive * 100 / (100 + outputTaxRatePercent)) : sellingPriceInclusive;
        if (net >= cost)
        {
            return null;
        }

        var margin = net == 0 ? -100m : decimal.Round((net - cost) / net * 100, 2, MidpointRounding.AwayFromZero);
        return new BelowCost(cost, net, cost - net, margin);
    }

    /// <summary>Validates the thresholds a business sets.</summary>
    public static void ValidateThresholds(decimal reasonPercent, decimal approvalPercent)
    {
        if (reasonPercent < 0 || approvalPercent < reasonPercent || approvalPercent > 1000)
        {
            throw new DomainException("purchase_settings.thresholds_invalid", "The approval threshold must be at least the reason threshold (both 0-1000%).");
        }
    }
}
