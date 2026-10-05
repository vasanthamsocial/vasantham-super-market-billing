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
    DocumentNumbers numbers,
    StockEngine engine,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

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
        var lines = await ResolveLinesAsync(businessId, request.Type, request.Lines, cancellationToken).ConfigureAwait(false);

        var number = DocumentNumbers.Format(store.Code, StockDocumentTypes.Prefix(request.Type),
            await numbers.NextAsync(businessId, store.Id, "STK-" + StockDocumentTypes.Prefix(request.Type), cancellationToken).ConfigureAwait(false));
        var businessDate = BusinessCalendar.Today(clock, store.TimeZone);
        var document = StockDocument.Post(businessId, store.Id, target?.Id, request.Type, number, businessDate, request.Reason, request.Note,
            request.IdempotencyKey, requestHash, request.NegativeStockOverride, currentUser.UserId, now);
        db.StockDocuments.Add(document);

        await engine.StartAsync(
            new StockPostingDocument(businessId, document.Type, document.Id, document.Number, document.NegativeStockOverride, businessDate, now),
            cancellationToken).ConfigureAwait(false);
        await engine.LockAsync(
            lines.Select(l => (store.Id, l.Variant.Id)).Concat(target is null ? [] : lines.Select(l => (target.Id, l.Variant.Id))),
            cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            await PostLineAsync(document, request, line, store, target, now, cancellationToken).ConfigureAwait(false);
        }

        engine.Flush();
        audit.Record("stock.posted", "stock_document", document.Id, businessId, store.Id, details: new
        {
            document.Type,
            document.Number,
            document.Reason,
            target = target?.Code,
            lines = lines.Count,
            movements = engine.Entries.Count,
            value = engine.Entries.Sum(e => e.Value),
            negativeOverride = document.NegativeStockOverride,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await InventoryQueries.DocumentAsync(db, document.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostLineAsync(
        StockDocument document, PostStockDocumentRequest request, ResolvedLine line, Store store, Store? target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var balance = engine.Balance(store.Id, line.Variant.Id);
        var item = new StockItem(line.Variant.Id, line.Variant.Name, line.Product.Id);
        switch (request.Type)
        {
            case StockDocumentTypes.Opening:
                if (await db.StockLedger.AnyAsync(e => e.StoreId == store.Id && e.VariantId == line.Variant.Id, cancellationToken).ConfigureAwait(false))
                {
                    throw AppException.Conflict("stock.opening_exists",
                        $"{line.Variant.Name} already has stock movements in {store.Name}. Use an adjustment instead of opening stock.");
                }

                engine.Receive(store.Id, item, line.BaseQuantity, RequireCost(line, null), MovementTypes.Opening,
                    await BatchForReceiptAsync(document.BusinessId, line, now, cancellationToken).ConfigureAwait(false));
                break;

            case StockDocumentTypes.Adjustment when string.Equals(line.Request.Direction, "IN", StringComparison.OrdinalIgnoreCase):
                engine.Receive(store.Id, item, line.BaseQuantity, RequireCost(line, balance), MovementTypes.AdjustmentIn,
                    await BatchForReceiptAsync(document.BusinessId, line, now, cancellationToken).ConfigureAwait(false));
                break;

            case StockDocumentTypes.Adjustment when string.Equals(line.Request.Direction, "OUT", StringComparison.OrdinalIgnoreCase):
                await engine.IssueAsync(store.Id, item, line.BaseQuantity, MovementTypes.AdjustmentOut, line.Request.BatchId, recordsReality: false, cancellationToken).ConfigureAwait(false);
                break;

            case StockDocumentTypes.Adjustment:
                throw AppException.Validation("stock.direction_required", "Each adjustment line needs a direction: IN or OUT.");

            case StockDocumentTypes.Damage:
                await engine.IssueAsync(store.Id, item, line.BaseQuantity, MovementTypes.Damage, line.Request.BatchId, recordsReality: false, cancellationToken).ConfigureAwait(false);
                break;

            case StockDocumentTypes.Wastage:
                await engine.IssueAsync(store.Id, item, line.BaseQuantity, MovementTypes.Wastage, line.Request.BatchId, recordsReality: false, cancellationToken).ConfigureAwait(false);
                break;

            case StockDocumentTypes.Transfer:
            {
                // Stock moves with its own cost and batch, so the destination values it exactly as the source did.
                var issued = await engine.IssueAsync(store.Id, item, line.BaseQuantity, MovementTypes.TransferOut, line.Request.BatchId, recordsReality: false, cancellationToken).ConfigureAwait(false);
                foreach (var part in issued)
                {
                    var batch = part.BatchId is { } b ? await db.Batches.FirstAsync(x => x.Id == b, cancellationToken).ConfigureAwait(false) : null;
                    engine.Receive(target!.Id, item, part.Quantity, part.UnitCost, MovementTypes.TransferIn,
                        batch ?? await BatchForReceiptAsync(document.BusinessId, line, now, cancellationToken).ConfigureAwait(false),
                        await engine.OriginOfAsync([part.LayerId], cancellationToken).ConfigureAwait(false));
                }

                break;
            }

            case StockDocumentTypes.Count:
            {
                var difference = StockMath.Quantity(line.BaseQuantity - balance.Quantity);
                if (difference > 0)
                {
                    engine.Receive(store.Id, item, difference, CurrentCost(balance, line), MovementTypes.CountGain,
                        await BatchForReceiptAsync(document.BusinessId, line, now, cancellationToken).ConfigureAwait(false));
                }
                else if (difference < 0)
                {
                    await engine.IssueAsync(store.Id, item, -difference, MovementTypes.CountLoss, line.Request.BatchId, recordsReality: true, cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            default:
                throw AppException.Validation("stock_document.type_invalid", $"Unknown stock document type '{request.Type}'.");
        }
    }

    private async Task<Batch?> BatchForReceiptAsync(Guid businessId, ResolvedLine line, DateTimeOffset now, CancellationToken cancellationToken)
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
            batch = Batch.Create(businessId, line.Variant.Id, number, request.ManufacturedOn, request.ExpiresOn, now);
            db.Batches.Add(batch);
        }
        else if (request.ExpiresOn is { } expiry && batch.ExpiresOn != expiry)
        {
            throw AppException.Conflict("batch.expiry_mismatch", $"Batch {number} of {line.Variant.Name} is recorded with expiry {batch.ExpiresOn:dd-MMM-yyyy}.");
        }

        return batch;
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
}
