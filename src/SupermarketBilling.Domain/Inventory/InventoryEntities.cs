using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Inventory;

public static class MovementTypes
{
    public const string Opening = "OPENING";
    public const string AdjustmentIn = "ADJUSTMENT_IN";
    public const string AdjustmentOut = "ADJUSTMENT_OUT";
    public const string Damage = "DAMAGE";
    public const string Wastage = "WASTAGE";
    public const string TransferOut = "TRANSFER_OUT";
    public const string TransferIn = "TRANSFER_IN";
    public const string CountGain = "COUNT_GAIN";
    public const string CountLoss = "COUNT_LOSS";

    // Used by later stages: goods receipt, purchase return, sale, sales return.
    public const string Receipt = "RECEIPT";
    public const string PurchaseReturn = "PURCHASE_RETURN";
    public const string Sale = "SALE";
    public const string SaleReturn = "SALE_RETURN";

    public static readonly IReadOnlyList<string> All =
        [Opening, AdjustmentIn, AdjustmentOut, Damage, Wastage, TransferOut, TransferIn, CountGain, CountLoss, Receipt, PurchaseReturn, Sale, SaleReturn];
}

public static class StockDocumentTypes
{
    public const string Opening = "OPENING";
    public const string Adjustment = "ADJUSTMENT";
    public const string Damage = "DAMAGE";
    public const string Wastage = "WASTAGE";
    public const string Transfer = "TRANSFER";
    public const string Count = "COUNT";

    public static readonly IReadOnlyList<string> All = [Opening, Adjustment, Damage, Wastage, Transfer, Count];

    /// <summary>Short prefix used in document numbers, for example MAIN/ADJ/000042.</summary>
    public static string Prefix(string type) => type switch
    {
        Opening => "OPN",
        Adjustment => "ADJ",
        Damage => "DMG",
        Wastage => "WST",
        Transfer => "TRF",
        Count => "CNT",
        _ => throw new DomainException("stock_document.type_invalid", $"Unknown stock document type '{type}'."),
    };
}

/// <summary>Per-business stock settings.</summary>
public sealed class InventorySettings : ITenantOwned
{
    private InventorySettings()
    {
        ValuationMethod = string.Empty;
    }

    public Guid BusinessId { get; private set; }

    public string ValuationMethod { get; private set; }

    public uint RowVersion { get; private set; }

    public static InventorySettings Create(Guid businessId, string valuationMethod) => new()
    {
        BusinessId = businessId,
        ValuationMethod = Validate(valuationMethod),
    };

    /// <summary>Only allowed before any stock has moved: changing the method afterwards would distort valuations.</summary>
    public void ChangeValuationMethod(string valuationMethod, bool anyStockMovement)
    {
        if (anyStockMovement)
        {
            throw new DomainException("stock.valuation_locked", "The valuation method cannot change after stock has moved.");
        }

        ValuationMethod = Validate(valuationMethod);
    }

    private static string Validate(string method) =>
        ValuationMethods.All.Contains(method) ? method : throw new DomainException("stock.valuation_invalid", $"Unknown valuation method '{method}'.");
}

/// <summary>
/// A negative-stock rule for the business, a store, a product, or a product in a store. The most specific active
/// rule applies. Rules are not edited: a change adds a new rule and deactivates the old one, so the history of who
/// allowed what, when and why is kept.
/// </summary>
public sealed class NegativeStockRule : ITenantOwned
{
    private NegativeStockRule()
    {
        Mode = Reason = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid? StoreId { get; private set; }

    public Guid? ProductId { get; private set; }

    public string Mode { get; private set; }

    /// <summary>For <see cref="NegativeStockModes.EnabledWithLimit"/>: how far below zero an item may go (stock units).</summary>
    public decimal? LimitQuantity { get; private set; }

    public string Reason { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Guid? ApprovalRequestId { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset? SupersededAtUtc { get; private set; }

    public int Specificity => (StoreId is null ? 0 : 1) + (ProductId is null ? 0 : 2);

    public static NegativeStockRule Create(
        Guid businessId, Guid? storeId, Guid? productId, string mode, decimal? limit, string reason, Guid createdBy, Guid? approvalRequestId,
        bool active, DateTimeOffset now)
    {
        if (!NegativeStockModes.All.Contains(mode))
        {
            throw new DomainException("negative_stock.mode_invalid", $"Unknown negative-stock mode '{mode}'.");
        }

        if (mode == NegativeStockModes.EnabledWithLimit && (limit is null || limit <= 0))
        {
            throw new DomainException("negative_stock.limit_required", "Give a positive limit for how far below zero stock may go.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            throw new DomainException("negative_stock.reason_required", "A reason is required for changing the negative-stock setting.");
        }

        return new NegativeStockRule
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            StoreId = storeId,
            ProductId = productId,
            Mode = mode,
            LimitQuantity = mode == NegativeStockModes.EnabledWithLimit ? StockMath.Quantity(limit!.Value) : null,
            Reason = reason.Trim(),
            CreatedByUserId = createdBy,
            CreatedAtUtc = now,
            ApprovalRequestId = approvalRequestId,
            IsActive = active,
        };
    }

    public void Activate(Guid approvalRequestId)
    {
        ApprovalRequestId = approvalRequestId;
        IsActive = true;
    }

    public void Supersede(DateTimeOffset now)
    {
        IsActive = false;
        SupersededAtUtc ??= now;
    }

    /// <summary>The rule that applies: product+store, then product, then store, then business; otherwise disabled.</summary>
    public static NegativeStockPolicy Resolve(IEnumerable<NegativeStockRule> rules, Guid storeId, Guid productId)
    {
        var rule = rules
            .Where(r => r.IsActive && (r.StoreId is null || r.StoreId == storeId) && (r.ProductId is null || r.ProductId == productId))
            .OrderByDescending(r => r.Specificity)
            .ThenByDescending(r => r.CreatedAtUtc)
            .FirstOrDefault();
        return rule is null ? NegativeStockPolicy.Default : new NegativeStockPolicy(rule.Mode, rule.LimitQuantity);
    }
}

/// <summary>A manufacturing batch or lot of a variant, with its expiry. Batch numbers are unique per variant.</summary>
public sealed partial class Batch : ITenantOwned
{
    private Batch()
    {
        BatchNumber = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid VariantId { get; private set; }

    public string BatchNumber { get; private set; }

    public DateOnly? ManufacturedOn { get; private set; }

    public DateOnly? ExpiresOn { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static Batch Create(Guid businessId, Guid variantId, string batchNumber, DateOnly? manufacturedOn, DateOnly? expiresOn, DateTimeOffset now)
    {
        var normalized = (batchNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (!BatchPattern().IsMatch(normalized))
        {
            throw new DomainException("batch.number_invalid", "Batch number must be 1-30 letters, digits, hyphens, dots or slashes.");
        }

        if (manufacturedOn is { } m && expiresOn is { } e && e <= m)
        {
            throw new DomainException("batch.dates_invalid", "Expiry must be after the manufacturing date.");
        }

        return new Batch
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            VariantId = variantId,
            BatchNumber = normalized,
            ManufacturedOn = manufacturedOn,
            ExpiresOn = expiresOn,
            CreatedAtUtc = now,
        };
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9./-]{0,29}$")]
    private static partial Regex BatchPattern();
}

/// <summary>
/// Stock received at one cost (and batch) in one store, consumed by issues in valuation order. The remaining
/// quantity is a projection of the ledger: it always equals the sum of the ledger entries that reference the layer.
/// </summary>
public sealed class CostLayer : ITenantOwned
{
    private CostLayer()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid? BatchId { get; private set; }

    /// <summary>Copied from the batch so FEFO ordering needs no join.</summary>
    public DateOnly? ExpiresOn { get; private set; }

    public decimal UnitCost { get; private set; }

    public decimal OriginalQuantity { get; private set; }

    public decimal RemainingQuantity { get; private set; }

    /// <summary>Part of this receipt that covered stock previously issued below zero (so it was never available).</summary>
    public decimal SettledShortfall { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    /// <summary>Database sequence of creation, the FIFO order.</summary>
    public long Sequence { get; private set; }

    public static CostLayer Create(Guid businessId, Guid storeId, Guid variantId, Batch? batch, decimal quantity, decimal unitCost, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            throw new DomainException("stock.layer_quantity", "A cost layer needs a positive quantity.");
        }

        if (unitCost < 0)
        {
            throw new DomainException("stock.cost_negative", "Cost cannot be negative.");
        }

        return new CostLayer
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            StoreId = storeId,
            VariantId = variantId,
            BatchId = batch?.Id,
            ExpiresOn = batch?.ExpiresOn,
            UnitCost = StockMath.Cost(unitCost),
            OriginalQuantity = quantity,
            RemainingQuantity = quantity,
            ReceivedAtUtc = now,
        };
    }

    public void Consume(decimal quantity)
    {
        if (quantity <= 0 || quantity > RemainingQuantity)
        {
            throw new DomainException("stock.layer_overdrawn", "Cannot take more than a cost layer holds.");
        }

        RemainingQuantity -= quantity;
    }

    /// <summary>Used when a receipt first covers stock that had gone negative. Invariant: remaining = original - issued - settled.</summary>
    public void SettleShortfall(decimal quantity)
    {
        Consume(quantity);
        SettledShortfall += quantity;
    }

    public LayerSnapshot ToSnapshot() => new(Id, BatchId, ExpiresOn, ReceivedAtUtc, Sequence, RemainingQuantity, UnitCost);
}

/// <summary>Quantity and average cost of a variant in a store. A projection of the ledger, locked while posting.</summary>
public sealed class StockBalance : ITenantOwned
{
    private StockBalance()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid VariantId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal AverageCost { get; private set; }

    /// <summary>Cost of the most recent receipt, used to cost stock issued below zero.</summary>
    public decimal LastCost { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static StockBalance Create(Guid businessId, Guid storeId, Guid variantId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        StoreId = storeId,
        VariantId = variantId,
        UpdatedAtUtc = now,
    };

    public void ApplyReceipt(decimal quantity, decimal unitCost, DateTimeOffset now)
    {
        AverageCost = StockMath.AverageAfterReceipt(Quantity, AverageCost, quantity, unitCost);
        LastCost = StockMath.Cost(unitCost);
        Quantity += quantity;
        UpdatedAtUtc = now;
    }

    public void ApplyIssue(decimal quantity, DateTimeOffset now)
    {
        Quantity -= quantity;
        UpdatedAtUtc = now;
    }
}

/// <summary>
/// One stock movement. Append-only: corrections are new movements. Quantity is signed (in is positive), in stock
/// units; value is quantity x unit cost.
/// </summary>
public sealed class StockLedgerEntry : ITenantOwned
{
    private StockLedgerEntry()
    {
        MovementType = DocumentType = string.Empty;
    }

    public Guid Id { get; private set; }

    public long Sequence { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid? BatchId { get; private set; }

    /// <summary>The cost layer created (receipt) or consumed (issue); null for stock issued below zero.</summary>
    public Guid? LayerId { get; private set; }

    public string MovementType { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitCost { get; private set; }

    public decimal Value { get; private set; }

    /// <summary>Running quantity of the variant in the store after this movement.</summary>
    public decimal BalanceAfter { get; private set; }

    public string DocumentType { get; private set; }

    public Guid DocumentId { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public static StockLedgerEntry Create(
        Guid businessId, Guid storeId, Guid variantId, Guid? batchId, Guid? layerId, string movementType, decimal quantity, decimal unitCost,
        decimal balanceAfter, string documentType, Guid documentId, DateOnly businessDate, Guid createdBy, DateTimeOffset now)
    {
        if (quantity == 0)
        {
            throw new DomainException("stock.zero_movement", "A stock movement cannot be zero.");
        }

        if (!MovementTypes.All.Contains(movementType))
        {
            throw new DomainException("stock.movement_invalid", $"Unknown movement type '{movementType}'.");
        }

        var cost = StockMath.Cost(unitCost);
        return new StockLedgerEntry
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            StoreId = storeId,
            VariantId = variantId,
            BatchId = batchId,
            LayerId = layerId,
            MovementType = movementType,
            Quantity = quantity,
            UnitCost = cost,
            Value = StockMath.Value(quantity, cost),
            BalanceAfter = balanceAfter,
            DocumentType = documentType,
            DocumentId = documentId,
            BusinessDate = businessDate,
            OccurredAtUtc = now,
            CreatedByUserId = createdBy,
        };
    }
}

/// <summary>A posted stock document (opening, adjustment, damage, wastage, transfer or count). Posted documents never change.</summary>
public sealed class StockDocument : ITenantOwned
{
    private StockDocument()
    {
        Type = Number = Reason = IdempotencyKey = RequestHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    /// <summary>Destination store of a transfer.</summary>
    public Guid? TargetStoreId { get; private set; }

    public string Type { get; private set; }

    public string Number { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string Reason { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Client-generated key: retrying the same request returns this document instead of posting again.</summary>
    public string IdempotencyKey { get; private set; }

    /// <summary>Hash of the request, so a retry with different content under the same key is refused.</summary>
    public string RequestHash { get; private set; }

    public bool NegativeStockOverride { get; private set; }

    public Guid PostedByUserId { get; private set; }

    public DateTimeOffset PostedAtUtc { get; private set; }

    public static StockDocument Post(
        Guid businessId, Guid storeId, Guid? targetStoreId, string type, string number, DateOnly businessDate, string reason, string? note,
        string idempotencyKey, string requestHash, bool negativeStockOverride, Guid postedBy, DateTimeOffset now)
    {
        if (!StockDocumentTypes.All.Contains(type))
        {
            throw new DomainException("stock_document.type_invalid", $"Unknown stock document type '{type}'.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw new DomainException("idempotency.key_required", "Each stock posting needs an idempotency key (max 100 characters).");
        }

        if (type == StockDocumentTypes.Transfer && (targetStoreId is null || targetStoreId == storeId))
        {
            throw new DomainException("transfer.target_invalid", "A transfer needs a different destination store.");
        }

        return new StockDocument
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            StoreId = storeId,
            TargetStoreId = type == StockDocumentTypes.Transfer ? targetStoreId : null,
            Type = type,
            Number = number,
            BusinessDate = businessDate,
            Reason = Business.Required(reason, "stock_document.reason_required", "A reason is required (max 200 characters).", 200),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            NegativeStockOverride = negativeStockOverride,
            PostedByUserId = postedBy,
            PostedAtUtc = now,
        };
    }
}

/// <summary>Minimum and reorder quantities of a variant in a store (stock units).</summary>
public sealed class ReorderLevel : ITenantOwned
{
    private ReorderLevel()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid VariantId { get; private set; }

    public decimal MinimumQuantity { get; private set; }

    public decimal ReorderQuantity { get; private set; }

    public static ReorderLevel Create(Guid businessId, Guid storeId, Guid variantId, decimal minimum, decimal reorder, DateTimeOffset now)
    {
        var level = new ReorderLevel { Id = Guid.CreateVersion7(now), BusinessId = businessId, StoreId = storeId, VariantId = variantId };
        level.Update(minimum, reorder);
        return level;
    }

    public void Update(decimal minimum, decimal reorder)
    {
        if (minimum < 0 || reorder < 0)
        {
            throw new DomainException("reorder.invalid", "Reorder quantities cannot be negative.");
        }

        MinimumQuantity = StockMath.Quantity(minimum);
        ReorderQuantity = StockMath.Quantity(reorder);
    }
}

/// <summary>
/// Next number of a document series in a store (for example stock adjustments, later invoices). Numbers are taken
/// inside the posting transaction, so they are gapless: a failed posting returns its number.
/// </summary>
public sealed class DocumentSequence : ITenantOwned
{
    private DocumentSequence()
    {
        Series = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public string Series { get; private set; }

    public long NextNumber { get; private set; }
}
