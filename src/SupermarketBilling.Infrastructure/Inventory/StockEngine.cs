using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Inventory;

/// <summary>The document a stock posting belongs to (a stock document, a sales invoice, later a GRN).</summary>
public sealed record StockPostingDocument(
    Guid BusinessId, string DocumentType, Guid DocumentId, string DocumentNumber, bool NegativeStockOverride, DateOnly BusinessDate, DateTimeOffset Now);

/// <summary>An item as the engine needs it: the variant moved and the product whose negative-stock rule applies.</summary>
public sealed record StockItem(Guid VariantId, string Name, Guid ProductId);

/// <summary>
/// Moves stock for one posting, inside the caller's transaction:
/// <list type="number">
/// <item>locks every affected stock balance, always in the same order, before any quantity is read, so concurrent
/// postings can neither lose nor double-count stock;</item>
/// <item>consumes cost layers FIFO, FEFO or at average cost, enforcing the negative-stock rule;</item>
/// <item>writes append-only ledger entries (added to the context by <see cref="Flush"/>).</item>
/// </list>
/// One instance serves one posting (it is scoped to the request).
/// </summary>
public sealed class StockEngine(SupermarketBillingDbContext db, TenantContext tenant, AuditRecorder audit, ICurrentUser currentUser)
{
    private readonly Dictionary<(Guid Store, Guid Variant), StockBalance> _balances = [];
    private readonly List<StockLedgerEntry> _entries = [];
    private StockPostingDocument? _document;
    private string _valuationMethod = ValuationMethods.Fifo;
    private List<NegativeStockRule> _negativeRules = [];

    public IReadOnlyList<StockLedgerEntry> Entries => _entries;

    private StockPostingDocument Document => _document ?? throw new InvalidOperationException("Call StartAsync first.");

    public async Task StartAsync(StockPostingDocument document, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Stock must be posted inside the posting transaction.");
        }

        // A new document starts clean: nothing carried over from one posted (or rolled back) before it in the same request.
        _document = document;
        _entries.Clear();
        _balances.Clear();
        var settings = await db.InventorySettings.AsNoTracking().FirstOrDefaultAsync(s => s.BusinessId == document.BusinessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.Conflict("stock.settings_missing", "Inventory settings are missing for this business.");
        _valuationMethod = settings.ValuationMethod;
        _negativeRules = await db.NegativeStockRules.AsNoTracking().Where(r => r.BusinessId == document.BusinessId && r.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Locks the balances of every (store, item) the posting touches, in a fixed order.</summary>
    public async Task LockAsync(IEnumerable<(Guid StoreId, Guid VariantId)> pairs, CancellationToken cancellationToken)
    {
        foreach (var (storeId, variantId) in pairs.Distinct().OrderBy(p => p.StoreId).ThenBy(p => p.VariantId))
        {
            if (_balances.ContainsKey((storeId, variantId)))
            {
                continue;
            }

            var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant for stock posting.");
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO stock_balances (id, tenant_id, business_id, store_id, variant_id, quantity, average_cost, last_cost, updated_at_utc)
                VALUES ({Guid.CreateVersion7(Document.Now)}, {tenantId}, {Document.BusinessId}, {storeId}, {variantId}, 0, 0, 0, {Document.Now})
                ON CONFLICT (store_id, variant_id) DO NOTHING
                """,
                cancellationToken).ConfigureAwait(false);
            _balances[(storeId, variantId)] = (await db.StockBalances
                    .FromSql($"SELECT * FROM stock_balances WHERE store_id = {storeId} AND variant_id = {variantId} FOR UPDATE")
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Single();
        }
    }

    public StockBalance Balance(Guid storeId, Guid variantId) =>
        _balances.TryGetValue((storeId, variantId), out var balance)
            ? balance
            : throw new InvalidOperationException("The balance was not locked before use.");

    /// <summary>Adds stock: a new cost layer, a ledger entry, and the new balance. Covers any negative stock first.</summary>
    public void Receive(Guid storeId, StockItem item, decimal quantity, decimal unitCostPerBase, string movementType, Batch? batch, string origin = StockOrigins.Other)
    {
        var balance = Balance(storeId, item.VariantId);
        var layer = CostLayer.Create(Document.BusinessId, storeId, item.VariantId, batch, quantity, unitCostPerBase, Document.Now, origin);
        if (balance.Quantity < 0)
        {
            layer.SettleShortfall(Math.Min(quantity, -balance.Quantity));
        }

        db.CostLayers.Add(layer);
        balance.ApplyReceipt(quantity, unitCostPerBase, Document.Now);
        _entries.Add(StockLedgerEntry.Create(Document.BusinessId, storeId, item.VariantId, batch?.Id, layer.Id, movementType, quantity,
            unitCostPerBase, balance.Quantity, Document.DocumentType, Document.DocumentId, Document.BusinessDate, currentUser.UserId, Document.Now));
    }

    /// <summary>The origin of the lots taken (for goods that move on, such as a transfer).</summary>
    public async Task<string> OriginOfAsync(IEnumerable<Guid> layerIds, CancellationToken cancellationToken)
    {
        var ids = layerIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var origins = await db.CostLayers.Where(l => ids.Contains(l.Id)).Select(l => l.Origin).ToListAsync(cancellationToken).ConfigureAwait(false);
        return origins.Count == 0 ? StockOrigins.Other : StockOrigins.Of(origins);
    }

    /// <summary>
    /// Removes stock in valuation order, enforcing the negative-stock rule, except when the movement records what has
    /// already happened (a count loss, or a sale made at a counter without the server): that is never blocked. Returns
    /// what was taken, with costs.
    /// </summary>
    public Task<IReadOnlyList<LayerTake>> IssueAsync(
        Guid storeId, StockItem item, decimal quantity, string movementType, Guid? batchId, bool recordsReality, CancellationToken cancellationToken) =>
        IssueCoreAsync(storeId, item, quantity, movementType, batchId, recordsReality, null, cancellationToken);

    /// <summary>
    /// Returns goods to their supplier: taken first from the cost layer the receipt created (while any of it is left),
    /// the rest in valuation order.
    /// </summary>
    public Task<IReadOnlyList<LayerTake>> IssueFromLayerFirstAsync(
        Guid storeId, StockItem item, decimal quantity, string movementType, Guid? batchId, Guid? layerId, CancellationToken cancellationToken) =>
        IssueCoreAsync(storeId, item, quantity, movementType, batchId, false, layerId, cancellationToken);

    private async Task<IReadOnlyList<LayerTake>> IssueCoreAsync(
        Guid storeId, StockItem item, decimal quantity, string movementType, Guid? batchId, bool recordsReality, Guid? preferredLayerId, CancellationToken cancellationToken)
    {
        var balance = Balance(storeId, item.VariantId);
        var layers = await db.CostLayers
            .FromSql($"SELECT * FROM cost_layers WHERE store_id = {storeId} AND variant_id = {item.VariantId} AND remaining_quantity > 0 ORDER BY sequence FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var preferred = layers.FirstOrDefault(l => l.Id == preferredLayerId && (batchId is null || l.BatchId == batchId));
        var first = preferred is null ? 0 : Math.Min(quantity, preferred.RemainingQuantity);
        var plan = quantity - first > 0
            ? IssuePlanner.Plan(
                layers.Select(l => l.ToSnapshot()).Select(s => s.LayerId == preferred?.Id ? s with { Remaining = s.Remaining - first } : s).Where(s => s.Remaining > 0),
                quantity - first, _valuationMethod, batchId)
            : new IssuePlan([], 0);
        if (first > 0)
        {
            plan = plan with { Takes = [new LayerTake(preferred!.Id, preferred.BatchId, first, preferred.UnitCost), .. plan.Takes] };
        }

        if (plan.Shortfall > 0)
        {
            if (batchId is not null)
            {
                throw AppException.Conflict("stock.batch_insufficient", $"The chosen batch of {item.Name} has only {plan.Covered} in stock.");
            }

            if (!recordsReality)
            {
                var policy = NegativeStockRule.Resolve(_negativeRules, storeId, item.ProductId);
                var overrideApproved = Document.NegativeStockOverride && policy.Mode == NegativeStockModes.WarnWithOverride;
                if (policy.Check(balance.Quantity - quantity, overrideApproved) is { } problem)
                {
                    throw AppException.Conflict("stock.insufficient", $"{item.Name}: {problem} In stock: {Math.Max(balance.Quantity, 0)}.");
                }
            }
        }

        var averageCost = balance.AverageCost > 0 ? balance.AverageCost : balance.LastCost;
        var useAverage = _valuationMethod == ValuationMethods.WeightedAverage;
        var taken = new List<LayerTake>();
        foreach (var take in plan.Takes)
        {
            layers.Single(l => l.Id == take.LayerId).Consume(take.Quantity);
            var cost = useAverage ? averageCost : take.UnitCost;
            balance.ApplyIssue(take.Quantity, Document.Now);
            _entries.Add(StockLedgerEntry.Create(Document.BusinessId, storeId, item.VariantId, take.BatchId, take.LayerId, movementType, -take.Quantity,
                cost, balance.Quantity, Document.DocumentType, Document.DocumentId, Document.BusinessDate, currentUser.UserId, Document.Now));
            taken.Add(take with { UnitCost = cost });
        }

        if (plan.Shortfall > 0)
        {
            // Below zero: no layer to draw from; costed at the average (or last) cost until a receipt covers it.
            balance.ApplyIssue(plan.Shortfall, Document.Now);
            _entries.Add(StockLedgerEntry.Create(Document.BusinessId, storeId, item.VariantId, null, null, movementType, -plan.Shortfall,
                averageCost, balance.Quantity, Document.DocumentType, Document.DocumentId, Document.BusinessDate, currentUser.UserId, Document.Now));
            taken.Add(new LayerTake(Guid.Empty, null, plan.Shortfall, averageCost));
            audit.Record("stock.went_negative", "product_variant", item.VariantId, Document.BusinessId, storeId, details: new
            {
                document = Document.DocumentNumber,
                shortfall = plan.Shortfall,
                balanceAfter = balance.Quantity,
                negativeOverride = Document.NegativeStockOverride,
            });
        }

        return taken;
    }

    /// <summary>Adds the ledger entries to the context; the caller saves and commits.</summary>
    public void Flush() => db.StockLedger.AddRange(_entries);
}
