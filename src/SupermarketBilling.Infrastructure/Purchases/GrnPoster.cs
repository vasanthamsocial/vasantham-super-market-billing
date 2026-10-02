using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Purchases;

/// <summary>
/// Puts a receipt's goods into stock (used when it is saved, or when its approval is given): one cost layer per line at
/// the landed cost per stock unit, in its batch, under the same locks as every other stock movement. A new MRP on the
/// pack is added to the catalogue, lines marked so set the retail selling price, and an order that posted receipts have
/// fully covered is closed. Runs inside the caller's transaction.
/// </summary>
public sealed class GrnPoster(SupermarketBillingDbContext db, StockEngine stock, PricingService pricing, AuditRecorder audit)
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

        // New selling prices follow the business's price-approval rules, as if the receiver had entered them.
        foreach (var line in lines.Where(l => l.UpdateSellingPrice && l.SellingPrice is not null).OrderBy(l => l.LineNumber))
        {
            try
            {
                await pricing.CreateCoreAsync(grn.BusinessId, line.VariantId, new CreatePriceRuleRequest(line.VariantUnitId, RateTypes.Standard, SalesChannels.Retail,
                        line.SellingPrice!.Value, TaxInclusive: true, line.Mrp, StoreId: null, CustomerGroupId: null, MembersOnly: false, MinQuantity: 0, MaxQuantity: null,
                        ValidFromUtc: now, ValidToUtc: null, Priority: null, Note: $"From goods receipt {grn.Number}"),
                    grn.ReceivedByUserId, cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException e)
            {
                throw AppException.Validation(e.Code, $"{line.Description}: {e.Message}");
            }
        }

        await CloseOrderIfReceivedAsync(grn, lines, now, cancellationToken).ConfigureAwait(false);
        audit.Record("grn.posted", "grn", grn.Id, grn.BusinessId, grn.StoreId, details: new { grn.Number, grn.LandedTotal, lines = lines.Count });
    }

    private async Task CloseOrderIfReceivedAsync(Grn grn, IReadOnlyList<GrnLine> lines, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (grn.PurchaseOrderId is not { } orderId)
        {
            return;
        }

        var order = (await db.PurchaseOrders.FromSql($"SELECT *, xmin FROM purchase_orders WHERE id = {orderId} FOR UPDATE").ToListAsync(cancellationToken)
            .ConfigureAwait(false)).Single();
        if (order.Status != PurchaseOrderStatus.Open)
        {
            return;
        }

        // Only posted receipts count: one waiting for approval may still be rejected.
        var posted = await (from l in db.GrnLines.AsNoTracking()
                            join g in db.Grns.AsNoTracking() on l.GrnId equals g.Id
                            where g.PurchaseOrderId == orderId && g.Status == GrnStatus.Posted && g.Id != grn.Id
                            group l.Quantity by l.VariantUnitId into x
                            select new { x.Key, Quantity = x.Sum() })
            .ToDictionaryAsync(x => x.Key, x => x.Quantity, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            posted[line.VariantUnitId] = posted.GetValueOrDefault(line.VariantUnitId) + line.Quantity;
        }

        var ordered = await db.PurchaseOrderLines.AsNoTracking().Where(l => l.PurchaseOrderId == orderId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (ordered.All(o => posted.GetValueOrDefault(o.VariantUnitId) >= o.Quantity))
        {
            order.Close(now);
            audit.Record("purchase_order.closed", "purchase_order", order.Id, grn.BusinessId, grn.StoreId, details: new { order.Number, via = grn.Number, reason = "fully_received" });
        }
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
