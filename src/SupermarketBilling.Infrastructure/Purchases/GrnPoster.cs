using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Purchases;

/// <summary>
/// Puts a receipt's goods into stock (used when it is saved, or when its approval is given): one cost layer per line at
/// the landed cost per stock unit, in its batch, under the same locks as every other stock movement. A new MRP on the
/// pack is added to the catalogue. Runs inside the caller's transaction.
/// </summary>
public sealed class GrnPoster(SupermarketBillingDbContext db, StockEngine stock, AuditRecorder audit)
{
    public const string LedgerDocumentType = "GRN";

    public async Task PostAsync(Grn grn, IReadOnlyList<GrnLine> lines, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grn);
        ArgumentNullException.ThrowIfNull(lines);
        grn.Post(now);
        await stock.StartAsync(new StockPostingDocument(grn.BusinessId, LedgerDocumentType, grn.Id, grn.Number, false, grn.BusinessDate, now), cancellationToken)
            .ConfigureAwait(false);
        await stock.LockAsync(lines.Select(l => (grn.StoreId, l.VariantId)), cancellationToken).ConfigureAwait(false);
        foreach (var line in lines.OrderBy(l => l.LineNumber))
        {
            var batch = await BatchAsync(grn.BusinessId, line, now, cancellationToken).ConfigureAwait(false);
            stock.Receive(grn.StoreId, new StockItem(line.VariantId, line.Description, line.ProductId), line.BaseQuantity, line.LandedUnitCost, MovementTypes.Receipt, batch);
            if (line.Mrp is { } mrp && !await db.VariantMrps.AnyAsync(m => m.VariantUnitId == line.VariantUnitId && m.Mrp == mrp && m.IsActive, cancellationToken).ConfigureAwait(false)
                && !db.VariantMrps.Local.Any(m => m.VariantUnitId == line.VariantUnitId && m.Mrp == mrp))
            {
                var added = VariantMrp.Create(grn.BusinessId, line.VariantId, line.VariantUnitId, mrp, grn.BusinessDate, now);
                db.VariantMrps.Add(added);
                audit.Record("catalog.mrp_added", "variant_mrp", added.Id, grn.BusinessId, grn.StoreId, details: new { mrp, via = grn.Number });
            }
        }

        stock.Flush();
        audit.Record("grn.posted", "grn", grn.Id, grn.BusinessId, grn.StoreId, details: new { grn.Number, grn.LandedTotal, lines = lines.Count });
    }

    private async Task<Batch?> BatchAsync(Guid businessId, GrnLine line, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (line.BatchNumber is not { } number)
        {
            return null;
        }

        var batch = db.Batches.Local.FirstOrDefault(b => b.VariantId == line.VariantId && b.BatchNumber == number)
            ?? await db.Batches.FirstOrDefaultAsync(b => b.VariantId == line.VariantId && b.BatchNumber == number, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            batch = Batch.Create(businessId, line.VariantId, number, line.ManufacturedOn, line.ExpiresOn, now);
            db.Batches.Add(batch);
        }
        else if (line.ExpiresOn is { } expiry && batch.ExpiresOn != expiry)
        {
            throw AppException.Conflict("batch.expiry_mismatch", $"Batch {number} of {line.Description} is recorded with expiry {batch.ExpiresOn:dd-MMM-yyyy}.");
        }

        return batch;
    }
}
