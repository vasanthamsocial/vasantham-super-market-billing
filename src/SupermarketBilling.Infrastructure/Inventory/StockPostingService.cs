using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Inventory;

/// <summary>
/// Posts stock documents. Every posting runs in one transaction that:
/// <list type="number">
/// <item>serialises retries of the same idempotency key (a retry returns the original document);</item>
/// <item>takes a gapless document number;</item>
/// <item>locks every affected stock balance in a fixed order, so concurrent postings cannot lose or double-count stock;</item>
/// <item>consumes cost layers FIFO, FEFO or at average cost, enforcing the negative-stock rule;</item>
/// <item>writes append-only ledger entries and the audit record.</item>
/// </list>
/// </summary>
public sealed class StockPostingService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    TenantContext tenant,
    DocumentNumbers numbers,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<(Guid Store, Guid Variant), StockBalance> _balances = [];
    private readonly List<StockLedgerEntry> _entries = [];

    public async Task<StockDocumentDto> PostAsync(Guid businessId, PostStockDocumentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await AuthoriseAsync(businessId, request, cancellationToken).ConfigureAwait(false);
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500)
        {
            throw AppException.Validation("stock_document.lines_required", "A stock document needs 1 to 500 lines.");
        }

        var requestHash = Hash(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Same key, same moment: the second waits here, then finds the first's document.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({businessId.ToString() + "|" + request.IdempotencyKey}))", cancellationToken)
            .ConfigureAwait(false);
        var existing = await db.StockDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.BusinessId == businessId && d.IdempotencyKey == request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? await InventoryQueries.DocumentAsync(db, existing.Id, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("idempotency.mismatch", "This idempotency key was already used for a different stock document.");
        }

        var now = clock.GetUtcNow();
        var store = await StoreAsync(businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        var target = request.Type == StockDocumentTypes.Transfer && request.TargetStoreId is { } t
            ? await StoreAsync(businessId, t, cancellationToken).ConfigureAwait(false)
            : null;
        var settings = await db.InventorySettings.AsNoTracking().FirstOrDefaultAsync(s => s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.Conflict("stock.settings_missing", "Inventory settings are missing for this business.");
        var lines = await ResolveLinesAsync(businessId, request.Type, request.Lines, cancellationToken).ConfigureAwait(false);
        var negativeRules = await db.NegativeStockRules.AsNoTracking().Where(r => r.BusinessId == businessId && r.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var number = DocumentNumbers.Format(store.Code, StockDocumentTypes.Prefix(request.Type),
            await numbers.NextAsync(businessId, store.Id, "STK-" + StockDocumentTypes.Prefix(request.Type), cancellationToken).ConfigureAwait(false));
        var businessDate = BusinessCalendar.Today(clock, store.TimeZone);
        var document = StockDocument.Post(businessId, store.Id, target?.Id, request.Type, number, businessDate, request.Reason, request.Note,
            request.IdempotencyKey, requestHash, request.NegativeStockOverride, currentUser.UserId, now);
        db.StockDocuments.Add(document);

        // Lock every affected balance, always in the same order, before reading any quantity.
        var pairs = lines.Select(l => (store.Id, l.Variant.Id))
            .Concat(target is null ? [] : lines.Select(l => (target.Id, l.Variant.Id)))
            .Distinct().OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToList();
        foreach (var (storeId, variantId) in pairs)
        {
            await LockBalanceAsync(businessId, storeId, variantId, now, cancellationToken).ConfigureAwait(false);
        }

        var context = new PostingContext(document, settings.ValuationMethod, negativeRules, businessDate, now);
        foreach (var line in lines)
        {
            await PostLineAsync(context, request, line, store, target, cancellationToken).ConfigureAwait(false);
        }

        db.StockLedger.AddRange(_entries);
        audit.Record("stock.posted", "stock_document", document.Id, businessId, store.Id, details: new
        {
            document.Type,
            document.Number,
            document.Reason,
            target = target?.Code,
            lines = lines.Count,
            movements = _entries.Count,
            value = _entries.Sum(e => e.Value),
            negativeOverride = document.NegativeStockOverride,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await InventoryQueries.DocumentAsync(db, document.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostLineAsync(PostingContext context, PostStockDocumentRequest request, ResolvedLine line, Store store, Store? target, CancellationToken cancellationToken)
    {
        var balance = _balances[(store.Id, line.Variant.Id)];
        switch (request.Type)
        {
            case StockDocumentTypes.Opening:
                if (await db.StockLedger.AnyAsync(e => e.StoreId == store.Id && e.VariantId == line.Variant.Id, cancellationToken).ConfigureAwait(false))
                {
                    throw AppException.Conflict("stock.opening_exists",
                        $"{line.Variant.Name} already has stock movements in {store.Name}. Use an adjustment instead of opening stock.");
                }

                await ReceiveAsync(context, store.Id, line, line.BaseQuantity, RequireCost(line, null), MovementTypes.Opening, null, cancellationToken).ConfigureAwait(false);
                break;

            case StockDocumentTypes.Adjustment when string.Equals(line.Request.Direction, "IN", StringComparison.OrdinalIgnoreCase):
                await ReceiveAsync(context, store.Id, line, line.BaseQuantity, RequireCost(line, balance), MovementTypes.AdjustmentIn, null, cancellationToken).ConfigureAwait(false);
                break;

            case StockDocumentTypes.Adjustment when string.Equals(line.Request.Direction, "OUT", StringComparison.OrdinalIgnoreCase):
                await IssueAsync(context, store.Id, line, line.BaseQuantity, MovementTypes.AdjustmentOut, cancellationToken).ConfigureAwait(false);
                break;

            case StockDocumentTypes.Adjustment:
                throw AppException.Validation("stock.direction_required", "Each adjustment line needs a direction: IN or OUT.");

            case StockDocumentTypes.Damage:
                await IssueAsync(context, store.Id, line, line.BaseQuantity, MovementTypes.Damage, cancellationToken).ConfigureAwait(false);
                break;

            case StockDocumentTypes.Wastage:
                await IssueAsync(context, store.Id, line, line.BaseQuantity, MovementTypes.Wastage, cancellationToken).ConfigureAwait(false);
                break;

            case StockDocumentTypes.Transfer:
            {
                // Stock moves with its own cost and batch, so the destination values it exactly as the source did.
                var issued = await IssueAsync(context, store.Id, line, line.BaseQuantity, MovementTypes.TransferOut, cancellationToken).ConfigureAwait(false);
                foreach (var part in issued)
                {
                    var batch = part.BatchId is { } b ? await db.Batches.FirstAsync(x => x.Id == b, cancellationToken).ConfigureAwait(false) : null;
                    await ReceiveAsync(context, target!.Id, line, part.Quantity, part.UnitCost, MovementTypes.TransferIn, batch, cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            case StockDocumentTypes.Count:
            {
                var difference = StockMath.Quantity(line.BaseQuantity - balance.Quantity);
                if (difference > 0)
                {
                    await ReceiveAsync(context, store.Id, line, difference, CurrentCost(balance, line), MovementTypes.CountGain, null, cancellationToken).ConfigureAwait(false);
                }
                else if (difference < 0)
                {
                    await IssueAsync(context, store.Id, line, -difference, MovementTypes.CountLoss, cancellationToken, countLoss: true).ConfigureAwait(false);
                }

                break;
            }

            default:
                throw AppException.Validation("stock_document.type_invalid", $"Unknown stock document type '{request.Type}'.");
        }
    }

    /// <summary>Adds stock: a new cost layer, a ledger entry, and the new balance. Covers any negative stock first.</summary>
    private async Task ReceiveAsync(
        PostingContext context, Guid storeId, ResolvedLine line, decimal quantity, decimal unitCostPerBase, string movementType, Batch? batch,
        CancellationToken cancellationToken)
    {
        batch ??= await BatchForReceiptAsync(context, line, cancellationToken).ConfigureAwait(false);
        var balance = _balances[(storeId, line.Variant.Id)];
        var layer = CostLayer.Create(context.Document.BusinessId, storeId, line.Variant.Id, batch, quantity, unitCostPerBase, context.Now);
        if (balance.Quantity < 0)
        {
            layer.SettleShortfall(Math.Min(quantity, -balance.Quantity));
        }

        db.CostLayers.Add(layer);
        balance.ApplyReceipt(quantity, unitCostPerBase, context.Now);
        _entries.Add(StockLedgerEntry.Create(context.Document.BusinessId, storeId, line.Variant.Id, batch?.Id, layer.Id, movementType, quantity,
            unitCostPerBase, balance.Quantity, context.Document.Type, context.Document.Id, context.BusinessDate, currentUser.UserId, context.Now));
    }

    /// <summary>Removes stock in valuation order, enforcing the negative-stock rule. Returns what was taken, with costs.</summary>
    private async Task<List<LayerTake>> IssueAsync(
        PostingContext context, Guid storeId, ResolvedLine line, decimal quantity, string movementType, CancellationToken cancellationToken, bool countLoss = false)
    {
        var balance = _balances[(storeId, line.Variant.Id)];
        var layers = await db.CostLayers
            .FromSql($"SELECT * FROM cost_layers WHERE store_id = {storeId} AND variant_id = {line.Variant.Id} AND remaining_quantity > 0 ORDER BY sequence FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var plan = IssuePlanner.Plan(layers.Select(l => l.ToSnapshot()), quantity, context.ValuationMethod, line.Request.BatchId);

        if (plan.Shortfall > 0)
        {
            if (line.Request.BatchId is not null)
            {
                throw AppException.Conflict("stock.batch_insufficient", $"The chosen batch of {line.Variant.Name} has only {plan.Covered} in stock.");
            }

            // A count records reality, so a count loss is never blocked; everything else follows the rule.
            if (!countLoss)
            {
                var policy = NegativeStockRule.Resolve(context.NegativeRules, storeId, line.Product.Id);
                var overrideApproved = context.Document.NegativeStockOverride && policy.Mode == NegativeStockModes.WarnWithOverride;
                if (policy.Check(balance.Quantity - quantity, overrideApproved) is { } problem)
                {
                    throw AppException.Conflict("stock.insufficient", $"{line.Variant.Name}: {problem} In stock: {Math.Max(balance.Quantity, 0)}.");
                }
            }
        }

        var averageCost = balance.AverageCost > 0 ? balance.AverageCost : balance.LastCost;
        var useAverage = context.ValuationMethod == ValuationMethods.WeightedAverage;
        var taken = new List<LayerTake>();
        foreach (var take in plan.Takes)
        {
            layers.Single(l => l.Id == take.LayerId).Consume(take.Quantity);
            var cost = useAverage ? averageCost : take.UnitCost;
            balance.ApplyIssue(take.Quantity, context.Now);
            _entries.Add(StockLedgerEntry.Create(context.Document.BusinessId, storeId, line.Variant.Id, take.BatchId, take.LayerId, movementType, -take.Quantity,
                cost, balance.Quantity, context.Document.Type, context.Document.Id, context.BusinessDate, currentUser.UserId, context.Now));
            taken.Add(take with { UnitCost = cost });
        }

        if (plan.Shortfall > 0)
        {
            // Below zero: no layer to draw from; costed at the average (or last) cost until a receipt covers it.
            balance.ApplyIssue(plan.Shortfall, context.Now);
            _entries.Add(StockLedgerEntry.Create(context.Document.BusinessId, storeId, line.Variant.Id, null, null, movementType, -plan.Shortfall,
                averageCost, balance.Quantity, context.Document.Type, context.Document.Id, context.BusinessDate, currentUser.UserId, context.Now));
            taken.Add(new LayerTake(Guid.Empty, null, plan.Shortfall, averageCost));
            audit.Record("stock.went_negative", "product_variant", line.Variant.Id, context.Document.BusinessId, storeId,
                details: new { document = context.Document.Number, shortfall = plan.Shortfall, balanceAfter = balance.Quantity, negativeOverride = context.Document.NegativeStockOverride });
        }

        return taken;
    }

    private async Task<Batch?> BatchForReceiptAsync(PostingContext context, ResolvedLine line, CancellationToken cancellationToken)
    {
        var request = line.Request;
        if (string.IsNullOrWhiteSpace(request.BatchNumber))
        {
            if (line.Product.TracksBatches)
            {
                throw AppException.Validation("batch.required", $"{line.Variant.Name} is tracked by batch: enter the batch number.");
            }

            return null;
        }

        if (line.Product.TracksExpiry && request.ExpiresOn is null)
        {
            throw AppException.Validation("batch.expiry_required", $"{line.Variant.Name} is tracked by expiry: enter the expiry date.");
        }

        var number = request.BatchNumber.Trim().ToUpperInvariant();
        var batch = db.Batches.Local.FirstOrDefault(b => b.VariantId == line.Variant.Id && b.BatchNumber == number)
            ?? await db.Batches.FirstOrDefaultAsync(b => b.VariantId == line.Variant.Id && b.BatchNumber == number, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            batch = Batch.Create(context.Document.BusinessId, line.Variant.Id, number, request.ManufacturedOn, request.ExpiresOn, context.Now);
            db.Batches.Add(batch);
        }
        else if (request.ExpiresOn is { } expiry && batch.ExpiresOn != expiry)
        {
            throw AppException.Conflict("batch.expiry_mismatch", $"Batch {number} of {line.Variant.Name} is recorded with expiry {batch.ExpiresOn:dd-MMM-yyyy}.");
        }

        return batch;
    }

    private async Task LockBalanceAsync(Guid businessId, Guid storeId, Guid variantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant for stock posting.");
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO stock_balances (id, tenant_id, business_id, store_id, variant_id, quantity, average_cost, last_cost, updated_at_utc)
            VALUES ({Guid.CreateVersion7(now)}, {tenantId}, {businessId}, {storeId}, {variantId}, 0, 0, 0, {now})
            ON CONFLICT (store_id, variant_id) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);
        var balance = (await db.StockBalances
                .FromSql($"SELECT * FROM stock_balances WHERE store_id = {storeId} AND variant_id = {variantId} FOR UPDATE")
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Single();
        _balances[(storeId, variantId)] = balance;
    }

    private async Task<List<ResolvedLine>> ResolveLinesAsync(Guid businessId, string type, IReadOnlyList<StockLineRequest> requests, CancellationToken cancellationToken)
    {
        var packIds = requests.Select(r => r.VariantUnitId).Distinct().ToList();
        var rows = await (
                from vu in db.VariantUnits.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on vu.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                join u in db.Units.AsNoTracking() on p.BaseUnitId equals u.Id
                where packIds.Contains(vu.Id) && v.BusinessId == businessId
                select new { Pack = vu, Variant = v, Product = p, BaseUnit = u })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<ResolvedLine>();
        foreach (var request in requests)
        {
            var row = rows.FirstOrDefault(r => r.Pack.Id == request.VariantUnitId && r.Variant.Id == request.VariantId)
                ?? throw AppException.NotFound("Item or pack");
            // A physical count may legitimately record zero; everything else moves a positive quantity.
            if (request.Quantity < 0 || (request.Quantity == 0 && type != StockDocumentTypes.Count))
            {
                throw AppException.Validation("stock.quantity_invalid", $"{row.Variant.Name}: quantities must be positive (use the IN/OUT direction for adjustments).");
            }

            var baseQuantity = row.Pack.ToBase(request.Quantity);
            if (StockMath.Quantity(baseQuantity) != baseQuantity)
            {
                throw AppException.Validation("stock.quantity_precision", $"{row.Variant.Name}: at most {StockMath.QuantityScale} decimals in the stock unit.");
            }

            row.BaseUnit.ValidateQuantity(baseQuantity);
            if (request.UnitCost is < 0)
            {
                throw AppException.Validation("stock.cost_negative", "Cost cannot be negative.");
            }

            result.Add(new ResolvedLine(request, row.Variant, row.Product, row.Pack, baseQuantity));
        }

        if (result.GroupBy(l => (l.Variant.Id, l.Request.BatchId, l.Request.BatchNumber)).Any(g => g.Count() > 1))
        {
            throw AppException.Validation("stock.duplicate_line", "Each item (and batch) may appear only once in a document.");
        }

        return result;
    }

    /// <summary>Cost per stock unit for incoming stock: the line's cost per pack, or the current cost of the item.</summary>
    private static decimal RequireCost(ResolvedLine line, StockBalance? balance)
    {
        if (line.Request.UnitCost is { } perPack)
        {
            return StockMath.Cost(perPack / line.Pack.FactorToBase);
        }

        var current = balance is null ? 0 : CurrentCost(balance, line);
        return current > 0
            ? current
            : throw AppException.Validation("stock.cost_required", $"{line.Variant.Name}: enter the unit cost (the item has no cost history yet).");
    }

    private static decimal CurrentCost(StockBalance balance, ResolvedLine line) =>
        line.Request.UnitCost is { } perPack ? StockMath.Cost(perPack / line.Pack.FactorToBase)
        : balance.AverageCost > 0 ? balance.AverageCost : balance.LastCost;

    private async Task AuthoriseAsync(Guid businessId, PostStockDocumentRequest request, CancellationToken cancellationToken)
    {
        var permission = request.Type switch
        {
            StockDocumentTypes.Transfer => Permissions.StockTransfer,
            StockDocumentTypes.Count => Permissions.StockCount,
            _ => Permissions.StockAdjust,
        };
        await organisation.RequireAsync(permission, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        if (request.NegativeStockOverride
            && !await access.HasPermissionAsync(Permissions.StockNegativeOverride, businessId, request.StoreId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden("Only a manager can confirm taking stock below zero.");
        }
    }

    private async Task<Store> StoreAsync(Guid businessId, Guid storeId, CancellationToken cancellationToken) =>
        await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId && s.BusinessId == businessId && s.IsActive, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.NotFound("Store");

    private static string Hash(PostStockDocumentRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HashJson))));

    private sealed record ResolvedLine(StockLineRequest Request, ProductVariant Variant, Product Product, VariantUnit Pack, decimal BaseQuantity);

    private sealed record PostingContext(
        StockDocument Document, string ValuationMethod, IReadOnlyList<NegativeStockRule> NegativeRules, DateOnly BusinessDate, DateTimeOffset Now);
}
