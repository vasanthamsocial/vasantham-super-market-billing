using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Purchases;

/// <summary>Purchase orders, and how much of each has been received (by goods receipts that were not rejected).</summary>
public sealed class PurchaseOrderService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    DocumentNumbers numbers,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public async Task<PurchaseOrderDto> CreateAsync(Guid businessId, CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.PurchasesManage, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500)
        {
            throw AppException.Validation("purchase_order.lines_required", "An order needs 1 to 500 items.");
        }

        if (request.Lines.GroupBy(l => l.VariantUnitId).Any(g => g.Count() > 1))
        {
            throw AppException.Validation("purchase_order.duplicate_line", "Each pack may appear only once on an order.");
        }

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StoreId && s.BusinessId == businessId && s.IsActive, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Store");
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.SupplierId && s.BusinessId == businessId && s.IsActive, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Supplier");
        var packIds = request.Lines.Select(l => l.VariantUnitId).ToList();
        var packs = await db.VariantUnits.AsNoTracking().Where(v => packIds.Contains(v.Id) && v.BusinessId == businessId).ToDictionaryAsync(v => v.Id, cancellationToken)
            .ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sequence = await numbers.NextAsync(businessId, store.Id, "PO", cancellationToken).ConfigureAwait(false);
        PurchaseOrder order;
        try
        {
            order = PurchaseOrder.Place(businessId, store.Id, supplier.Id, DocumentNumbers.Format(store.Code, "PO", sequence), sequence,
                BusinessCalendar.Today(clock, store.TimeZone), request.ExpectedDate, request.Notes, currentUser.UserId, now);
            db.PurchaseOrders.Add(order);
            var number = 0;
            foreach (var line in request.Lines)
            {
                var pack = packs.GetValueOrDefault(line.VariantUnitId) ?? throw AppException.NotFound("Item pack");
                db.PurchaseOrderLines.Add(PurchaseOrderLine.Create(businessId, order.Id, ++number, pack.VariantId, pack.Id, line.Quantity, line.Rate, now));
            }
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        audit.Record("purchase_order.placed", "purchase_order", order.Id, businessId, store.Id, details: new { order.Number, supplier = supplier.Code, lines = request.Lines.Count });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(order.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PurchaseOrderDto>> ListAsync(Guid businessId, Guid storeId, string? status, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.PurchaseOrders.AsNoTracking().Where(o => o.StoreId == storeId);
        query = string.IsNullOrWhiteSpace(status) ? query : query.Where(o => o.Status == status);
        var ids = await query.OrderByDescending(o => o.CreatedAtUtc).Select(o => o.Id).Take(200).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PurchaseOrderDto>();
        foreach (var id in ids)
        {
            result.Add(await GetCoreAsync(id, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<PurchaseOrderDto> GetAsync(Guid businessId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await OrderAsync(businessId, orderId, cancellationToken).ConfigureAwait(false);
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, order.StoreId, cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(orderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurchaseOrderDto> CloseAsync(Guid businessId, Guid orderId, bool cancel, CancellationToken cancellationToken)
    {
        var order = await OrderAsync(businessId, orderId, cancellationToken).ConfigureAwait(false);
        await organisation.RequireAsync(Permissions.PurchasesManage, businessId, order.StoreId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        try
        {
            if (cancel)
            {
                order.Cancel(await db.Grns.AnyAsync(g => g.PurchaseOrderId == orderId && g.Status != GrnStatus.Rejected, cancellationToken).ConfigureAwait(false), now);
            }
            else
            {
                order.Close(now);
            }
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        audit.Record(cancel ? "purchase_order.cancelled" : "purchase_order.closed", "purchase_order", order.Id, businessId, order.StoreId, details: new { order.Number });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(orderId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Paid quantity received per pack against an order, by receipts that were not rejected (optionally leaving one out).</summary>
    internal static async Task<Dictionary<Guid, decimal>> ReceivedAsync(SupermarketBillingDbContext db, Guid orderId, Guid? exceptGrnId, CancellationToken cancellationToken) =>
        await (from l in db.GrnLines.AsNoTracking()
               join g in db.Grns.AsNoTracking() on l.GrnId equals g.Id
               where g.PurchaseOrderId == orderId && g.Status != GrnStatus.Rejected && g.Id != exceptGrnId
               group l.Quantity by l.VariantUnitId into x
               select new { x.Key, Quantity = x.Sum() })
            .ToDictionaryAsync(x => x.Key, x => x.Quantity, cancellationToken).ConfigureAwait(false);

    private async Task<PurchaseOrder> OrderAsync(Guid businessId, Guid orderId, CancellationToken cancellationToken) =>
        await db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.NotFound("Purchase order");

    private async Task<PurchaseOrderDto> GetCoreAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var row = await (from o in db.PurchaseOrders.AsNoTracking()
                         join s in db.Suppliers.AsNoTracking() on o.SupplierId equals s.Id
                         where o.Id == orderId
                         select new { Order = o, Supplier = s.Name }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var lines = await (from l in db.PurchaseOrderLines.AsNoTracking()
                           join v in db.ProductVariants.AsNoTracking() on l.VariantId equals v.Id
                           join vu in db.VariantUnits.AsNoTracking() on l.VariantUnitId equals vu.Id
                           join u in db.Units.AsNoTracking() on vu.UnitId equals u.Id
                           where l.PurchaseOrderId == orderId
                           orderby l.LineNumber
                           select new { Line = l, v.Name, UnitCode = u.Code }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var received = await ReceivedAsync(db, orderId, null, cancellationToken).ConfigureAwait(false);
        var receipts = await db.Grns.AsNoTracking().Where(g => g.PurchaseOrderId == orderId && g.Status != GrnStatus.Rejected).OrderBy(g => g.ReceivedAtUtc)
            .Select(g => g.Number).ToListAsync(cancellationToken).ConfigureAwait(false);
        var dtoLines = lines.Select(x =>
        {
            var got = received.GetValueOrDefault(x.Line.VariantUnitId);
            return new PurchaseOrderLineDto(x.Line.LineNumber, x.Line.VariantId, x.Line.VariantUnitId, x.Name, x.UnitCode, x.Line.Quantity, got,
                Math.Max(0, x.Line.Quantity - got), x.Line.Rate);
        }).ToList();
        var progress = dtoLines.All(l => l.Received == 0) ? "NOT_RECEIVED" : dtoLines.All(l => l.Outstanding == 0) ? "RECEIVED" : "PARTLY_RECEIVED";
        var po = row.Order;
        return new PurchaseOrderDto(po.Id, po.Number, po.Status, progress, po.StoreId, po.SupplierId, row.Supplier, po.OrderDate, po.ExpectedDate, po.Notes, dtoLines, receipts, po.RowVersion);
    }
}

/// <summary>Files kept with goods receipts (the scanned supplier invoice).</summary>
public sealed class AttachmentService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string GrnOwner = "GRN";

    public async Task<AttachmentDto> AddToGrnAsync(Guid businessId, Guid grnId, string? fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var storeId = await GrnStoreAsync(businessId, grnId, cancellationToken).ConfigureAwait(false);
        await organisation.RequireAsync(Permissions.PurchasesManage, businessId, storeId, cancellationToken).ConfigureAwait(false);

        // Read at most one byte more than allowed, so an oversized upload is refused without reading it all.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > Attachment.MaxBytes)
            {
                throw AppException.Validation("attachment.size_invalid", "A file must be at most 10 MB.");
            }
        }

        Attachment attachment;
        try
        {
            attachment = Attachment.Create(businessId, GrnOwner, grnId, fileName, buffer.ToArray(), currentUser.UserId, clock.GetUtcNow());
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.Attachments.Add(attachment);
        audit.Record("attachment.added", "attachment", attachment.Id, businessId, storeId,
            details: new { owner = GrnOwner, grnId, attachment.FileName, attachment.ContentType, attachment.Size, attachment.Sha256 });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await ListForGrnAsync(businessId, grnId, cancellationToken).ConfigureAwait(false)).First(a => a.Id == attachment.Id);
    }

    public async Task<IReadOnlyList<AttachmentDto>> ListForGrnAsync(Guid businessId, Guid grnId, CancellationToken cancellationToken)
    {
        var storeId = await GrnStoreAsync(businessId, grnId, cancellationToken).ConfigureAwait(false);
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        return await (from a in db.Attachments.AsNoTracking()
                      join u in db.Users.AsNoTracking() on a.UploadedByUserId equals u.Id
                      where a.OwnerType == GrnOwner && a.OwnerId == grnId
                      orderby a.UploadedAtUtc
                      select new AttachmentDto(a.Id, a.FileName, a.ContentType, a.Size, a.Sha256, u.DisplayName, a.UploadedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> DownloadAsync(Guid businessId, Guid grnId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var storeId = await GrnStoreAsync(businessId, grnId, cancellationToken).ConfigureAwait(false);
        await organisation.RequireAsync(Permissions.PurchasesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var file = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.OwnerType == GrnOwner && a.OwnerId == grnId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Attachment");
        return (file.Content, file.ContentType, file.FileName);
    }

    private async Task<Guid> GrnStoreAsync(Guid businessId, Guid grnId, CancellationToken cancellationToken) =>
        await db.Grns.AsNoTracking().Where(g => g.Id == grnId && g.BusinessId == businessId).Select(g => (Guid?)g.StoreId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Goods receipt");
}
