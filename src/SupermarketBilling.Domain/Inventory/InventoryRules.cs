using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.Domain.Inventory;

public static class ValuationMethods
{
    /// <summary>First in, first out: the oldest receipt is issued first (owner's default).</summary>
    public const string Fifo = "FIFO";

    /// <summary>First expiry, first out: the batch that expires soonest is issued first; then oldest receipt.</summary>
    public const string Fefo = "FEFO";

    /// <summary>Weighted average: every issue is costed at the running average cost of the item in the store.</summary>
    public const string WeightedAverage = "WEIGHTED_AVERAGE";

    public static readonly IReadOnlyList<string> All = [Fifo, Fefo, WeightedAverage];
}

public static class NegativeStockModes
{
    /// <summary>Stock can never go below zero.</summary>
    public const string Disabled = "DISABLED";

    /// <summary>Going below zero needs an authorised override on the document.</summary>
    public const string WarnWithOverride = "WARN_OVERRIDE";

    /// <summary>Allowed, down to a configured limit per item (in stock units).</summary>
    public const string EnabledWithLimit = "ENABLED_WITH_LIMIT";

    public static readonly IReadOnlyList<string> All = [Disabled, WarnWithOverride, EnabledWithLimit];

    /// <summary>How permissive a mode is, for deciding whether a change loosens control (and so needs approval).</summary>
    public static int Strictness(string mode) => mode switch
    {
        Disabled => 2,
        WarnWithOverride => 1,
        _ => 0,
    };
}

/// <summary>Quantities are held in stock units with 3 decimals; costs per stock unit with 4 decimals.</summary>
public static class StockMath
{
    public const int QuantityScale = 3;
    public const int CostScale = 4;

    public static decimal Quantity(decimal value) => decimal.Round(value, QuantityScale, MidpointRounding.AwayFromZero);

    public static decimal Cost(decimal value) => decimal.Round(value, CostScale, MidpointRounding.AwayFromZero);

    /// <summary>Value of a movement, rounded to 4 decimals (money is rounded to paise only on documents and reports).</summary>
    public static decimal Value(decimal quantity, decimal unitCost) => decimal.Round(quantity * unitCost, CostScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Running average after a receipt. When stock was negative, the average restarts at the receipt cost, because
    /// the negative quantity had no real cost of its own.
    /// </summary>
    public static decimal AverageAfterReceipt(decimal onHand, decimal averageCost, decimal receivedQuantity, decimal receivedCost)
    {
        if (receivedQuantity <= 0)
        {
            throw new DomainException("stock.receipt_quantity", "A receipt must add a positive quantity.");
        }

        if (onHand <= 0)
        {
            return Cost(receivedCost);
        }

        return Cost(((onHand * averageCost) + (receivedQuantity * receivedCost)) / (onHand + receivedQuantity));
    }
}

/// <summary>A cost layer as seen by the issue planner.</summary>
public sealed record LayerSnapshot(Guid LayerId, Guid? BatchId, DateOnly? ExpiryDate, DateTimeOffset ReceivedAtUtc, long ReceiptSequence, decimal Remaining, decimal UnitCost);

public sealed record LayerTake(Guid LayerId, Guid? BatchId, decimal Quantity, decimal UnitCost);

/// <summary>How an issue will be satisfied: quantities taken from layers, and any shortfall that would go negative.</summary>
public sealed record IssuePlan(IReadOnlyList<LayerTake> Takes, decimal Shortfall)
{
    public decimal Covered => Takes.Sum(t => t.Quantity);
}

/// <summary>
/// Decides which cost layers an issue uses. FIFO takes the oldest receipts first; FEFO takes the earliest expiry
/// first (undated batches last), then the oldest. When a batch is named, only that batch's layers are used.
/// Weighted-average businesses still consume layers FIFO for quantities and batches; the cost comes from the average.
/// </summary>
public static class IssuePlanner
{
    public static IssuePlan Plan(IEnumerable<LayerSnapshot> layers, decimal quantity, string valuationMethod, Guid? batchId)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (quantity <= 0)
        {
            throw new DomainException("stock.issue_quantity", "An issue must remove a positive quantity.");
        }

        var candidates = layers.Where(l => l.Remaining > 0 && (batchId is null || l.BatchId == batchId));
        var ordered = valuationMethod == ValuationMethods.Fefo
            ? candidates.OrderBy(l => l.ExpiryDate is null).ThenBy(l => l.ExpiryDate).ThenBy(l => l.ReceiptSequence)
            : candidates.OrderBy(l => l.ReceiptSequence);

        var takes = new List<LayerTake>();
        var needed = quantity;
        foreach (var layer in ordered)
        {
            if (needed == 0)
            {
                break;
            }

            var take = Math.Min(layer.Remaining, needed);
            takes.Add(new LayerTake(layer.LayerId, layer.BatchId, take, layer.UnitCost));
            needed -= take;
        }

        return new IssuePlan(takes, needed);
    }
}

/// <summary>The effective negative-stock rule for one item in one store.</summary>
public sealed record NegativeStockPolicy(string Mode, decimal? Limit)
{
    public static readonly NegativeStockPolicy Default = new(NegativeStockModes.Disabled, null);

    /// <summary>
    /// Checks whether the balance may become <paramref name="balanceAfter"/>. Returns null when allowed, otherwise the
    /// reason. An override applies only in <see cref="NegativeStockModes.WarnWithOverride"/> mode.
    /// </summary>
    public string? Check(decimal balanceAfter, bool overrideApproved)
    {
        if (balanceAfter >= 0)
        {
            return null;
        }

        return Mode switch
        {
            NegativeStockModes.Disabled => "There is not enough stock and negative stock is not allowed.",
            NegativeStockModes.WarnWithOverride when !overrideApproved =>
                "There is not enough stock. A person allowed to override negative stock must confirm it.",
            NegativeStockModes.EnabledWithLimit when Limit is { } limit && -balanceAfter > limit =>
                $"Stock would go below the allowed negative limit of {limit}.",
            _ => null,
        };
    }
}
