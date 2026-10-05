using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Dispatch;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Dispatch;

/// <summary>
/// Packing challans (spec section 20): one per bill sent by delivery or lorry, created with the delivery choice.
/// Goods are picked, checked by a second person, and packed (in parts if needed) before they can be dispatched.
/// Short picks and goods not delivered are differences on the challan; the invoice is never changed by them.
/// </summary>
public sealed class PackingService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    DocumentNumbers numbers,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string Series = "PCH";

    /// <summary>The figures of each challan line from its dispatches and the bill's credit notes.</summary>
    internal sealed record Figures(decimal InTransit, decimal Delivered, decimal Returned, decimal Lost, decimal Out, decimal Credited);

    /// <summary>Adds the challan of a bill (in the caller's transaction); the bill's lines may not be saved yet.</summary>
    internal async Task<PackingChallan> CreateAsync(Guid businessId, Guid storeId, Guid invoiceId, string partyName, IReadOnlyList<SalesInvoiceLine> lines,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var productIds = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken)
            .ConfigureAwait(false);
        var store = await db.Stores.AsNoTracking().Where(s => s.Id == storeId).Select(s => s.Code).FirstAsync(cancellationToken).ConfigureAwait(false);
        var sequence = await numbers.NextAsync(businessId, storeId, Series, cancellationToken).ConfigureAwait(false);
        var challan = PackingChallan.Create(businessId, storeId, invoiceId, DocumentNumbers.Format(store, Series, sequence), partyName,
            lines.OrderBy(l => l.LineNumber).Select(l => new PackingChallanLine.Item(l.Id, l.LineNumber, products.GetValueOrDefault(l.ProductId, l.Description), l.Description,
                l.UnitCode, l.Quantity)), now);
        db.PackingChallans.Add(challan);
        db.PackingEvents.Add(PackingEvent.Record(businessId, challan.Id, "CREATED", null, currentUser.UserId, now));
        return challan;
    }

    public async Task<IReadOnlyList<ChallanSummaryDto>> ListAsync(Guid businessId, Guid? storeId, bool openOnly, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DispatchView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.PackingChallans.AsNoTracking().Include(c => c.Lines).Where(c => c.BusinessId == businessId);
        query = storeId is { } s ? query.Where(c => c.StoreId == s) : query;
        query = openOnly ? query.Where(c => c.Status == ChallanStatus.Open) : query;
        var challans = await query.OrderByDescending(c => c.CreatedAtUtc).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var figures = await FiguresAsync(challans.SelectMany(c => c.Lines).ToList(), cancellationToken).ConfigureAwait(false);
        var heads = await HeadsAsync(challans.Select(c => c.InvoiceId).ToList(), cancellationToken).ConfigureAwait(false);
        var summaries = challans.Select(c =>
        {
            var head = heads[c.InvoiceId];
            var lines = c.Lines.Select(l => (Line: l, Figures: figures[l.Id])).ToList();
            var progress = Progress(c, lines);
            return new ChallanSummaryDto(c.Id, c.Number, c.StoreId, c.InvoiceId, head.Number, c.PartyName, head.Mode, head.TransporterName, head.DestinationBranch,
                progress, c.Status == ChallanStatus.Open && lines.Any(x => ReadyToSend(x.Line, x.Figures) > 0), lines.Any(x => Difference(x.Line, x.Figures) > 0),
                c.CreatedAtUtc);
        });
        // Finished challans (everything delivered, nothing to settle) drop off the working list.
        return openOnly ? summaries.Where(c => c.Progress != ChallanProgress.Delivered || c.HasDifference).ToList() : summaries.ToList();
    }

    public async Task<ChallanDto> GetAsync(Guid businessId, Guid challanId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DispatchView, businessId, cancellationToken).ConfigureAwait(false);
        var challan = await db.PackingChallans.AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == challanId && c.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Packing challan");
        return await DtoAsync(challan, cancellationToken).ConfigureAwait(false);
    }

    public Task<ChallanDto> PickAsync(Guid businessId, Guid challanId, CountChallanRequest request, CancellationToken cancellationToken) =>
        StepAsync(businessId, challanId, request?.RowVersion ?? 0, (challan, now) =>
        {
            ArgumentNullException.ThrowIfNull(request);
            challan.Pick(Counts(request.Lines), currentUser.UserId, now);
            var shorts = challan.Lines.Where(l => l.PickedQuantity < l.Quantity).Select(l => $"{l.ItemName} {l.PickedQuantity:0.###} of {l.Quantity:0.###}").ToList();
            return ("PICKED", shorts.Count == 0 ? "All picked" : "Short: " + string.Join(", ", shorts));
        }, cancellationToken);

    public Task<ChallanDto> CheckAsync(Guid businessId, Guid challanId, CountChallanRequest request, CancellationToken cancellationToken) =>
        StepAsync(businessId, challanId, request?.RowVersion ?? 0, (challan, now) =>
        {
            ArgumentNullException.ThrowIfNull(request);
            challan.Check(Counts(request.Lines), currentUser.UserId, now);
            var differences = challan.Lines.Where(l => l.CheckedQuantity < l.PickedQuantity).Select(l => $"{l.ItemName} {l.CheckedQuantity:0.###} of {l.PickedQuantity:0.###}").ToList();
            return ("CHECKED", differences.Count == 0 ? "Checked as picked" : "Found less: " + string.Join(", ", differences));
        }, cancellationToken);

    public Task<ChallanDto> PackAsync(Guid businessId, Guid challanId, PackChallanRequest request, CancellationToken cancellationToken) =>
        StepAsync(businessId, challanId, request?.RowVersion ?? 0, (challan, now) =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var quantities = request.Lines.GroupBy(l => l.LineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            challan.Pack(quantities, request.Packages, currentUser.UserId);
            var packed = string.Join(", ", quantities.Where(q => q.Value > 0).Select(q => $"{challan.Line(q.Key).ItemName} {q.Value:0.###}"));
            return ("PACKED", $"{request.Packages} package(s): {packed}");
        }, cancellationToken);

    /// <summary>Locks the challan, applies a step with its row version, and records it.</summary>
    private async Task<ChallanDto> StepAsync(Guid businessId, Guid challanId, uint rowVersion, Func<PackingChallan, DateTimeOffset, (string Kind, string Detail)> step,
        CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM packing_challans WHERE id = {challanId} FOR UPDATE", cancellationToken).ConfigureAwait(false);
        var challan = await db.PackingChallans.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == challanId && c.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Packing challan");
        if (challan.RowVersion != rowVersion)
        {
            throw AppException.Conflict("concurrency.conflict", "This challan was changed by someone else. Reload it and try again.");
        }

        var now = clock.GetUtcNow();
        var (kind, detail) = Valid(() => step(challan, now));
        db.PackingEvents.Add(PackingEvent.Record(businessId, challan.Id, kind, detail, currentUser.UserId, now));
        audit.Record($"packing.{kind.ToLowerInvariant()}", "packing_challan", challan.Id, businessId, challan.StoreId, details: new { challan.Number, detail });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return await GetAsync(businessId, challanId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(byte[] Content, string FileName)> ChallanPdfAsync(Guid businessId, Guid challanId, CancellationToken cancellationToken)
    {
        var challan = await GetAsync(businessId, challanId, cancellationToken).ConfigureAwait(false);
        var store = await db.Stores.AsNoTracking().Where(s => s.Id == challan.StoreId).Select(s => s.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
        return (Documents.ChallanPdf.Render(challan, store), $"{challan.Number.Replace('/', '-')}.pdf");
    }

    public async Task<(byte[] Content, string FileName)> LabelsPdfAsync(Guid businessId, Guid challanId, CancellationToken cancellationToken)
    {
        var challan = await GetAsync(businessId, challanId, cancellationToken).ConfigureAwait(false);
        var lr = challan.Dispatches.LastOrDefault(d => d.Status == ConsignmentStatus.Dispatched && d.LrNumber is not null)?.LrNumber;
        return (Documents.ChallanPdf.Labels(challan, lr), $"{challan.Number.Replace('/', '-')}-labels.pdf");
    }

    // Figures

    /// <summary>
    /// Per challan line: in dispatches that stand, what is in transit (not yet reported), delivered, back in the store and
    /// lost; and what credit notes took back on the bill line.
    /// </summary>
    internal async Task<Dictionary<Guid, Figures>> FiguresAsync(IReadOnlyCollection<PackingChallanLine> lines, CancellationToken cancellationToken)
    {
        var ids = lines.Select(l => l.Id).ToList();
        var sent = await (from cl in db.ConsignmentLines.AsNoTracking()
                          join c in db.Consignments.AsNoTracking() on cl.ConsignmentId equals c.Id
                          where ids.Contains(cl.ChallanLineId) && c.Status == ConsignmentStatus.Dispatched
                          select new { cl.ChallanLineId, cl.Quantity, cl.DeliveredQuantity, cl.ReturnedQuantity }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var invoiceLineIds = lines.Select(l => l.InvoiceLineId).ToList();
        var credited = await db.SalesReturnLines.AsNoTracking().Where(r => invoiceLineIds.Contains(r.OriginalLineId)).GroupBy(r => r.OriginalLineId)
            .Select(g => new { g.Key, Quantity = g.Sum(r => r.Quantity) }).ToDictionaryAsync(g => g.Key, g => g.Quantity, cancellationToken).ConfigureAwait(false);
        return lines.ToDictionary(l => l.Id, l =>
        {
            var mine = sent.Where(s => s.ChallanLineId == l.Id).ToList();
            var inTransit = mine.Where(s => s.DeliveredQuantity is null).Sum(s => s.Quantity);
            var delivered = mine.Sum(s => s.DeliveredQuantity ?? 0);
            var returned = mine.Sum(s => s.ReturnedQuantity ?? 0);
            var lost = mine.Where(s => s.DeliveredQuantity is not null).Sum(s => s.Quantity - s.DeliveredQuantity!.Value - (s.ReturnedQuantity ?? 0));
            return new Figures(inTransit, delivered, returned, lost, mine.Sum(s => s.Quantity - (s.ReturnedQuantity ?? 0)), credited.GetValueOrDefault(l.InvoiceLineId));
        });
    }

    /// <summary>Packed and not out, never more than is still owed to the customer after credit notes.</summary>
    internal static decimal ReadyToSend(PackingChallanLine line, Figures f) =>
        Math.Max(0, Math.Min(line.PackedQuantity - f.Out, line.Quantity - f.Credited - f.Out));

    /// <summary>Billed but not sent (short) or lost on the way, less what credit notes took back.</summary>
    internal static decimal Difference(PackingChallanLine line, Figures f) =>
        line.CheckedQuantity is null ? 0 : Math.Max(0, line.Quantity - line.CheckedQuantity.Value + f.Lost - f.Credited);

    internal static string Progress(PackingChallan challan, IEnumerable<(PackingChallanLine Line, Figures Figures)> lines) =>
        ChallanProgress.Of(challan.Status, lines.Select(x => new ChallanProgress.LineState(x.Line.Quantity, x.Line.PickedQuantity,
            x.Line.CheckedQuantity is { } c ? Math.Max(0, c - x.Figures.Credited) : null, x.Line.PackedQuantity, x.Figures.Out - x.Figures.Delivered,
            x.Figures.Delivered)).ToList());

    // Reading

    private sealed record Head(string Number, DateOnly Date, Guid? DebtorId, string Mode, string? DeliveryAddress, string? ContactPhone, string? TransporterName,
        string? DestinationBranch);

    private async Task<Dictionary<Guid, Head>> HeadsAsync(List<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        var rows = await (from i in db.SalesInvoices.AsNoTracking()
                          join f in db.InvoiceFulfilments.AsNoTracking() on i.Id equals f.InvoiceId into fs
                          from f in fs.DefaultIfEmpty()
                          join t in db.Transporters.AsNoTracking() on f.TransporterId equals t.Id into ts
                          from t in ts.DefaultIfEmpty()
                          join b in db.TransporterBranches.AsNoTracking() on f.DestinationBranchId equals b.Id into bs
                          from b in bs.DefaultIfEmpty()
                          where invoiceIds.Contains(i.Id)
                          select new
                          {
                              i.Id,
                              i.Number,
                              i.BusinessDate,
                              i.DebtorId,
                              Mode = f == null ? FulfilmentModes.Pickup : f.Mode,
                              Address = f == null ? null : f.DeliveryAddress,
                              Phone = f == null ? null : f.ContactPhone,
                              Transporter = t == null ? null : t.Name,
                              Branch = b == null ? null : b.Name + ", " + b.City,
                          }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => new Head(r.Number, r.BusinessDate, r.DebtorId, r.Mode, r.Address, r.Phone, r.Transporter, r.Branch));
    }

    private async Task<ChallanDto> DtoAsync(PackingChallan challan, CancellationToken cancellationToken)
    {
        var head = (await HeadsAsync([challan.InvoiceId], cancellationToken).ConfigureAwait(false))[challan.InvoiceId];
        var figures = await FiguresAsync(challan.Lines, cancellationToken).ConfigureAwait(false);
        var route = head.DebtorId is { } debtorId
            ? await (from p in db.CollectionPlans.AsNoTracking()
                     join r in db.Routes.AsNoTracking() on p.RouteId equals r.Id
                     where p.DebtorId == debtorId
                     select r.Code + " - " + r.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var batches = await BatchesAsync(challan, cancellationToken).ConfigureAwait(false);
        var userIds = new[] { challan.PickedByUserId, challan.CheckedByUserId, challan.PackedByUserId }.OfType<Guid>().ToList();
        var events = await db.PackingEvents.AsNoTracking().Where(e => e.ChallanId == challan.Id).OrderBy(e => e.AtUtc).ThenBy(e => e.Id).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        userIds.AddRange(events.Select(e => e.UserId));
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
        var lineIds = challan.Lines.Select(l => l.Id).ToList();
        var dispatches = await (from c in db.Consignments.AsNoTracking()
                                where db.ConsignmentLines.Any(cl => cl.ConsignmentId == c.Id && lineIds.Contains(cl.ChallanLineId))
                                orderby c.CreatedAtUtc
                                select new ChallanDispatchDto(c.Id, c.Number, c.Status, c.DispatchDate, c.LrNumber, c.DeliveryOutcome)).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var lines = challan.Lines.OrderBy(l => l.LineNumber).Select(l => (Line: l, Figures: figures[l.Id])).ToList();
        string? Name(Guid? id) => id is { } key ? users.GetValueOrDefault(key) : null;
        return new ChallanDto(challan.Id, challan.Number, challan.StoreId, challan.InvoiceId, head.Number, head.Date, head.DebtorId, challan.PartyName, head.Mode,
            head.DeliveryAddress, head.ContactPhone, route, head.TransporterName, head.DestinationBranch, challan.Status, Progress(challan, lines), Name(challan.PickedByUserId),
            Name(challan.CheckedByUserId), Name(challan.PackedByUserId), challan.PackageCount, challan.CancelReason,
            lines.Select(x => new ChallanLineDto(x.Line.Id, x.Line.LineNumber, x.Line.ItemName, x.Line.VariantName, x.Line.UnitCode, x.Line.Quantity, x.Line.FreeQuantity,
                batches.GetValueOrDefault(x.Line.InvoiceLineId), x.Line.PickedQuantity, x.Line.CheckedQuantity, x.Line.ShortReason, x.Line.PackedQuantity,
                x.Figures.InTransit, x.Figures.Delivered, x.Figures.Returned, x.Figures.Lost, ReadyToSend(x.Line, x.Figures), x.Figures.Credited,
                Difference(x.Line, x.Figures))).ToList(),
            dispatches, events.Select(e => new ChallanEventDto(e.Kind, e.Detail, users.GetValueOrDefault(e.UserId, "?"), e.AtUtc)).ToList(), challan.RowVersion);
    }

    /// <summary>The batches the sale took each line's stock from (from the stock ledger), for the picker.</summary>
    private async Task<Dictionary<Guid, string>> BatchesAsync(PackingChallan challan, CancellationToken cancellationToken)
    {
        var invoiceLines = await db.SalesInvoices.AsNoTracking().Where(i => i.Id == challan.InvoiceId).SelectMany(i => i.Lines)
            .Select(l => new { l.Id, l.VariantId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var taken = await (from e in db.StockLedger.AsNoTracking()
                           join b in db.Batches.AsNoTracking() on e.BatchId equals b.Id
                           where e.DocumentId == challan.InvoiceId
                           select new { e.VariantId, b.BatchNumber, b.ExpiresOn }).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return invoiceLines.Where(l => taken.Any(t => t.VariantId == l.VariantId)).ToDictionary(l => l.Id, l => string.Join(", ", taken.Where(t => t.VariantId == l.VariantId)
            .OrderBy(t => t.BatchNumber, StringComparer.Ordinal)
            .Select(t => t.ExpiresOn is { } x ? $"{t.BatchNumber} (exp {x.ToString("MM/yyyy", CultureInfo.InvariantCulture)})" : t.BatchNumber)));
    }

    private static Dictionary<Guid, (decimal Quantity, string? Reason)> Counts(IReadOnlyList<CountedLineRequest> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.GroupBy(l => l.LineId).Count() == lines.Count
            ? lines.ToDictionary(l => l.LineId, l => (l.Quantity, l.Reason))
            : throw AppException.Validation("challan.lines_duplicated", "Give each item once.");
    }

    private async Task RequireAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        if (!(await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false)).Contains(businessId))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static T Valid<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }
    }
}
