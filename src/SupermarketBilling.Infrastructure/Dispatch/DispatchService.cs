using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Dispatch;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Inventory;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Sales;

namespace SupermarketBilling.Infrastructure.Dispatch;

/// <summary>
/// Lorry service and dispatch (spec section 19): the lorry-service list (transporters, their booking offices and
/// destination branches, and the routes between them), how each bill's goods reach the customer, and the dispatches
/// themselves (LR/GR, vehicle, freight, e-way bill). The invoice is never changed by any of this.
/// </summary>
public sealed class DispatchService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    CounterService counters,
    IAccessControl access,
    DocumentNumbers numbers,
    PackingService packing,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string Series = "DSP";
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    // Lorry services

    public async Task<IReadOnlyList<TransporterDto>> TransportersAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DispatchView, businessId, cancellationToken).ConfigureAwait(false);
        var transporters = await db.Transporters.AsNoTracking().Where(t => t.BusinessId == businessId).OrderBy(t => t.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var branches = await db.TransporterBranches.AsNoTracking().Where(b => b.BusinessId == businessId).OrderBy(b => b.City).ThenBy(b => b.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var routes = await db.TransporterRoutes.AsNoTracking().Where(r => r.BusinessId == businessId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = branches.ToDictionary(b => b.Id, b => $"{b.Name}, {b.City}");
        return transporters.Select(t => new TransporterDto(t.Id, t.Code, t.Name, t.Gstin, t.Phone, t.Address, t.IsActive,
                branches.Where(b => b.TransporterId == t.Id).Select(Branch).ToList(),
                routes.Where(r => r.TransporterId == t.Id).Select(r => new TransporterRouteDto(r.Id, r.FromBranchId, names[r.FromBranchId], r.ToBranchId, names[r.ToBranchId],
                    r.TransitDays, r.IsActive, r.RowVersion)).OrderBy(r => r.FromBranch, StringComparer.Ordinal).ThenBy(r => r.ToBranch, StringComparer.Ordinal).ToList(),
                t.RowVersion))
            .ToList();
    }

    public async Task<TransporterDto> CreateTransporterAsync(Guid businessId, CreateTransporterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var transporter = Valid(() => Transporter.Create(businessId, request.Code, request.Name, request.Gstin, request.Phone, request.Address, clock.GetUtcNow()));
        if (await db.Transporters.AnyAsync(t => t.BusinessId == businessId && t.Code == transporter.Code, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("transporter.code_taken", $"Lorry service code {transporter.Code} is already used.");
        }

        db.Transporters.Add(transporter);
        audit.Record("transporter.created", "transporter", transporter.Id, businessId, details: new { transporter.Code, transporter.Name, transporter.Gstin });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await TransporterAsync(businessId, transporter.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransporterDto> UpdateTransporterAsync(Guid businessId, Guid transporterId, UpdateTransporterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var transporter = await FindTransporterAsync(businessId, transporterId, cancellationToken).ConfigureAwait(false);
        db.Entry(transporter).Property(t => t.RowVersion).OriginalValue = request.RowVersion;
        Valid(() => { transporter.Update(request.Name, request.Gstin, request.Phone, request.Address, request.IsActive); return transporter; });
        audit.Record("transporter.updated", "transporter", transporter.Id, businessId, details: new { transporter.Name, transporter.Gstin, transporter.IsActive });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await TransporterAsync(businessId, transporterId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransporterDto> AddBranchAsync(Guid businessId, Guid transporterId, TransporterBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        await FindTransporterAsync(businessId, transporterId, cancellationToken).ConfigureAwait(false);
        var branch = Valid(() => TransporterBranch.Create(businessId, transporterId, request.Name, request.City, request.Address, request.Phone, request.IsBookingOffice,
            request.IsDestination, clock.GetUtcNow()));
        await RequireUniqueBranchAsync(branch, cancellationToken).ConfigureAwait(false);
        db.TransporterBranches.Add(branch);
        audit.Record("transporter.branch_added", "transporter", transporterId, businessId, details: new { branch.Name, branch.City, branch.IsBookingOffice, branch.IsDestination });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await TransporterAsync(businessId, transporterId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransporterDto> UpdateBranchAsync(Guid businessId, Guid transporterId, Guid branchId, TransporterBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var branch = await db.TransporterBranches.FirstOrDefaultAsync(b => b.Id == branchId && b.TransporterId == transporterId && b.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Branch");
        db.Entry(branch).Property(b => b.RowVersion).OriginalValue = request.RowVersion;
        Valid(() => { branch.Update(request.Name, request.City, request.Address, request.Phone, request.IsBookingOffice, request.IsDestination, request.IsActive); return branch; });
        await RequireUniqueBranchAsync(branch, cancellationToken).ConfigureAwait(false);
        // Routes need a booking office at one end and a destination at the other.
        if ((!branch.IsBookingOffice && await db.TransporterRoutes.AnyAsync(r => r.FromBranchId == branchId, cancellationToken).ConfigureAwait(false)) ||
            (!branch.IsDestination && await db.TransporterRoutes.AnyAsync(r => r.ToBranchId == branchId, cancellationToken).ConfigureAwait(false)))
        {
            throw AppException.Conflict("branch.in_use", "Routes use this branch as it is; change or stop those routes first.");
        }

        audit.Record("transporter.branch_updated", "transporter", transporterId, businessId, details: new { branch.Id, branch.Name, branch.City, branch.IsActive });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await TransporterAsync(businessId, transporterId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransporterDto> AddRouteAsync(Guid businessId, Guid transporterId, CreateTransporterRouteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var branches = await db.TransporterBranches.AsNoTracking()
            .Where(b => b.TransporterId == transporterId && b.BusinessId == businessId && (b.Id == request.FromBranchId || b.Id == request.ToBranchId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var from = branches.FirstOrDefault(b => b.Id == request.FromBranchId) ?? throw AppException.NotFound("Booking office");
        var to = branches.FirstOrDefault(b => b.Id == request.ToBranchId) ?? throw AppException.NotFound("Destination branch");
        var route = Valid(() => TransporterRoute.Create(businessId, from, to, request.TransitDays, clock.GetUtcNow()));
        if (await db.TransporterRoutes.AnyAsync(r => r.TransporterId == transporterId && r.FromBranchId == from.Id && r.ToBranchId == to.Id, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("route.exists", "This route is already on the list.");
        }

        db.TransporterRoutes.Add(route);
        audit.Record("transporter.route_added", "transporter", transporterId, businessId, details: new { from = from.Name, to = to.Name, route.TransitDays });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await TransporterAsync(businessId, transporterId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransporterDto> UpdateRouteAsync(Guid businessId, Guid transporterId, Guid routeId, UpdateTransporterRouteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var route = await db.TransporterRoutes.FirstOrDefaultAsync(r => r.Id == routeId && r.TransporterId == transporterId && r.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Route");
        db.Entry(route).Property(r => r.RowVersion).OriginalValue = request.RowVersion;
        Valid(() => { route.Update(request.TransitDays, request.IsActive); return route; });
        audit.Record("transporter.route_updated", "transporter", transporterId, businessId, details: new { route.Id, route.TransitDays, route.IsActive });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await TransporterAsync(businessId, transporterId, cancellationToken).ConfigureAwait(false);
    }

    // How bills are delivered

    /// <summary>The counter's choices: active lorry services with their destinations, and the customer's usual way (if any).</summary>
    public async Task<CounterDeliveryOptionsDto> CounterOptionsAsync(string? deviceToken, Guid? debtorId, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var businessId = pos.Counter.BusinessId;
        var transporters = await db.Transporters.AsNoTracking().Where(t => t.BusinessId == businessId && t.IsActive).OrderBy(t => t.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var destinations = await db.TransporterBranches.AsNoTracking().Where(b => b.BusinessId == businessId && b.IsActive && b.IsDestination)
            .OrderBy(b => b.City).ThenBy(b => b.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var options = transporters.Select(t => new CounterTransporterDto(t.Id, t.Code, t.Name,
            destinations.Where(b => b.TransporterId == t.Id).Select(b => new CounterBranchDto(b.Id, b.Name, b.City)).ToList())).ToList();
        if (debtorId is not { } id)
        {
            return new CounterDeliveryOptionsDto(options, null, null);
        }

        var debtor = await db.Debtors.AsNoTracking().Where(d => d.Id == id && d.BusinessId == businessId).Select(d => new { d.Address })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Debtor");
        var preference = await PreferenceDtoAsync(id, cancellationToken).ConfigureAwait(false);
        return new CounterDeliveryOptionsDto(options, preference, preference?.DeliveryAddress ?? debtor.Address);
    }

    /// <summary>
    /// Records how a bill being issued is delivered, with its packing challan, in the bill's own transaction (nothing for
    /// pickup).
    /// </summary>
    internal async Task ChooseWithBillAsync(Guid businessId, Domain.Sales.SalesInvoice invoice, string partyName, FulfilmentRequest request, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (request.Mode == FulfilmentModes.Pickup)
        {
            return;
        }

        var choice = await ChoiceAsync(businessId, request, cancellationToken).ConfigureAwait(false);
        db.InvoiceFulfilments.Add(Valid(() => InvoiceFulfilment.Choose(businessId, invoice.StoreId, invoice.Id, choice, currentUser.UserId, now)));
        await packing.CreateAsync(businessId, invoice.StoreId, invoice.Id, partyName, invoice.Lines, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FulfilmentDto> FulfilmentAsync(Guid businessId, Guid invoiceId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DispatchView, businessId, cancellationToken).ConfigureAwait(false);
        if (!await db.SalesInvoices.AnyAsync(i => i.Id == invoiceId && i.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Invoice");
        }

        return await FulfilmentReader.ReadAsync(db, invoiceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes how a bill is delivered (for example the customer phones later), until its goods are dispatched.</summary>
    public async Task<FulfilmentDto> ChangeFulfilmentAsync(Guid businessId, Guid invoiceId, FulfilmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var invoice = await db.SalesInvoices.AsNoTracking().Where(i => i.Id == invoiceId && i.BusinessId == businessId)
            .Select(i => new { i.Id, i.StoreId, i.Number, i.DebtorId, i.BuyerName }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Invoice");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var fulfilment = (await db.InvoiceFulfilments.FromSql($"SELECT *, xmin FROM invoice_fulfilments WHERE invoice_id = {invoiceId} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (fulfilment is not null)
        {
            if (fulfilment.RowVersion != request.RowVersion)
            {
                throw AppException.Conflict("concurrency.conflict", "This record was changed by someone else. Reload it and try again.");
            }

            if (await ActiveConsignmentsAsync(invoiceId, cancellationToken).ConfigureAwait(false) > 0)
            {
                throw AppException.Conflict("fulfilment.dispatched", "The goods on this bill have been dispatched; cancel the dispatch first.");
            }
        }

        var choice = await ChoiceAsync(businessId, request, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (fulfilment is null)
        {
            db.InvoiceFulfilments.Add(Valid(() => InvoiceFulfilment.Choose(businessId, invoice.StoreId, invoiceId, choice, currentUser.UserId, now)));
        }
        else
        {
            Valid(() => { fulfilment.Change(choice, currentUser.UserId, now); return fulfilment; });
        }

        // The packing challan follows: cancelled when the customer collects after all, created when delivery is chosen later.
        var challan = (await db.PackingChallans.FromSql($"SELECT *, xmin FROM packing_challans WHERE invoice_id = {invoiceId} AND status = 'OPEN' FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (choice.Mode == FulfilmentModes.Pickup && challan is not null)
        {
            Valid(() => { challan.Cancel("Changed to pickup"); return challan; });
            db.PackingEvents.Add(PackingEvent.Record(businessId, challan.Id, "CANCELLED", "Changed to pickup", currentUser.UserId, now));
        }
        else if (choice.Mode != FulfilmentModes.Pickup && challan is null)
        {
            var lines = await db.SalesInvoices.Where(i => i.Id == invoiceId).SelectMany(i => i.Lines).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var party = invoice.DebtorId is { } debtorId
                ? (await PartyNamesAsync([debtorId], cancellationToken).ConfigureAwait(false))[debtorId]
                : invoice.BuyerName ?? "Walk-in customer";
            await packing.CreateAsync(businessId, invoice.StoreId, invoiceId, party, lines, now, cancellationToken).ConfigureAwait(false);
        }

        audit.Record("dispatch.fulfilment_changed", "sales_invoice", invoiceId, businessId, invoice.StoreId,
            details: new { invoice.Number, choice.Mode, choice.TransporterId, choice.DestinationBranchId });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await FulfilmentReader.ReadAsync(db, invoiceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeliveryPreferenceDto> PreferenceAsync(Guid businessId, Guid debtorId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DebtorsView, businessId, cancellationToken).ConfigureAwait(false);
        await RequireDebtorAsync(businessId, debtorId, cancellationToken).ConfigureAwait(false);
        return await PreferenceDtoAsync(debtorId, cancellationToken).ConfigureAwait(false)
            ?? new DeliveryPreferenceDto(FulfilmentModes.Pickup, null, null, null, null, null, 0);
    }

    public async Task<DeliveryPreferenceDto> ChangePreferenceAsync(Guid businessId, Guid debtorId, DeliveryPreferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DebtorsManage, businessId, cancellationToken).ConfigureAwait(false);
        await RequireDebtorAsync(businessId, debtorId, cancellationToken).ConfigureAwait(false);
        var choice = await ChoiceAsync(businessId, new FulfilmentRequest(request.Mode, request.DeliveryAddress, null, request.TransporterId, request.DestinationBranchId),
            cancellationToken).ConfigureAwait(false);
        var preference = await db.DeliveryPreferences.FirstOrDefaultAsync(p => p.DebtorId == debtorId, cancellationToken).ConfigureAwait(false);
        if (preference is null)
        {
            preference = DeliveryPreference.For(businessId, debtorId);
            db.DeliveryPreferences.Add(preference);
        }
        else
        {
            db.Entry(preference).Property(p => p.RowVersion).OriginalValue = request.RowVersion;
        }

        Valid(() => { preference.Change(choice.Mode, choice.TransporterId, choice.DestinationBranchId, choice.DeliveryAddress); return preference; });
        audit.Record("debtor.delivery_changed", "debtor", debtorId, businessId, details: new { preference.Mode, preference.TransporterId, preference.DestinationBranchId });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await PreferenceDtoAsync(debtorId, cancellationToken).ConfigureAwait(false))!;
    }

    // Dispatches

    /// <summary>Bills whose packed goods are ready to leave the store (packed, not yet sent, not credited).</summary>
    public async Task<IReadOnlyList<DispatchQueueItemDto>> QueueAsync(Guid businessId, Guid? storeId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DispatchView, businessId, cancellationToken).ConfigureAwait(false);
        var challans = await db.PackingChallans.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.BusinessId == businessId && c.Status == ChallanStatus.Open && c.PackageCount > 0 && (storeId == null || c.StoreId == storeId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var figures = await packing.FiguresAsync(challans.SelectMany(c => c.Lines).ToList(), cancellationToken).ConfigureAwait(false);
        var ready = challans.Where(c => c.Lines.Any(l => PackingService.ReadyToSend(l, figures[l.Id]) > 0)).ToDictionary(c => c.InvoiceId);
        var readyIds = ready.Keys.ToList();
        var rows = await (from f in db.InvoiceFulfilments.AsNoTracking()
                          join i in db.SalesInvoices.AsNoTracking() on f.InvoiceId equals i.Id
                          where readyIds.Contains(f.InvoiceId)
                          orderby i.IssuedAtUtc
                          select new { f, i.Number, i.IssuedAtUtc, i.DebtorId, i.BuyerName, i.GrandTotal })
            .Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var parties = await PartyNamesAsync(rows.Where(r => r.DebtorId != null).Select(r => r.DebtorId!.Value), cancellationToken).ConfigureAwait(false);
        var names = await TransportNamesAsync(rows.Select(r => r.f.TransporterId), rows.Select(r => r.f.DestinationBranchId), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new DispatchQueueItemDto(r.f.InvoiceId, r.Number, r.IssuedAtUtc, r.f.StoreId, r.DebtorId,
                Party(r.DebtorId, r.BuyerName, parties), r.GrandTotal, r.f.Mode, r.f.DeliveryAddress, r.f.TransporterId, Name(names, r.f.TransporterId), r.f.DestinationBranchId,
                Name(names, r.f.DestinationBranchId), ready[r.f.InvoiceId].Id, ready[r.f.InvoiceId].Number))
            .ToList();
    }

    /// <summary>
    /// Records goods leaving the store for one customer: one lorry booking (LR/GR) or one trip, carrying packed goods of
    /// one or more bills (all that is ready, or the quantities given: a bill can go in parts). The bills' delivery
    /// records and challans are locked, so nothing is sent twice; an LR/GR number is used once per lorry service.
    /// </summary>
    public async Task<ConsignmentDto> RecordAsync(Guid businessId, RecordConsignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.InvoiceIds);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HashJson))));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"consignment|" + businessId + "|" + request.IdempotencyKey}))", cancellationToken)
            .ConfigureAwait(false);
        var existing = await db.Consignments.AsNoTracking().Where(c => c.BusinessId == businessId && c.IdempotencyKey == request.IdempotencyKey)
            .Select(c => new { c.Id, c.RequestHash }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? await ConsignmentAsync(businessId, existing.Id, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("idempotency.mismatch", "This dispatch was already recorded with different details.");
        }

        var ids = request.InvoiceIds.Distinct().Order().ToArray();
        if (ids.Length is 0 or > 50)
        {
            throw AppException.Validation("consignment.no_invoices", "Choose 1 to 50 bills for this dispatch.");
        }

        // Lock the bills' delivery records in a fixed order: two people dispatching the same bill wait for each other.
        var fulfilments = await db.InvoiceFulfilments.FromSql($"SELECT *, xmin FROM invoice_fulfilments WHERE invoice_id = ANY({ids}) ORDER BY invoice_id FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var invoices = await db.SalesInvoices.AsNoTracking().Where(i => ids.Contains(i.Id) && i.BusinessId == businessId)
            .Select(i => new { i.Id, i.Number, i.StoreId, i.DebtorId, i.BuyerName, i.GrandTotal, i.BusinessDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (invoices.Count != ids.Length || fulfilments.Count != ids.Length || fulfilments.Any(f => f.BusinessId != businessId))
        {
            throw AppException.Validation("consignment.not_for_delivery", "Choose bills of this business whose delivery has been chosen (not pickup).");
        }

        var first = fulfilments[0];
        var firstInvoice = invoices.First(i => i.Id == first.InvoiceId);
        if (fulfilments.Any(f => f.Mode != first.Mode || f.TransporterId != first.TransporterId || f.StoreId != first.StoreId) ||
            invoices.Any(i => i.DebtorId != firstInvoice.DebtorId || (i.DebtorId is null && !string.Equals(i.BuyerName, firstInvoice.BuyerName, StringComparison.OrdinalIgnoreCase))))
        {
            throw AppException.Validation("consignment.mixed", "One dispatch is for one customer, from one store, by one way of delivery (and one lorry service).");
        }

        if (fulfilments.Any(f => f.Mode == FulfilmentModes.Pickup))
        {
            throw AppException.Validation("consignment.pickup", "Goods picked up by the customer are not dispatched.");
        }

        var challans = await db.PackingChallans
            .FromSql($"SELECT *, xmin FROM packing_challans WHERE invoice_id = ANY({ids}) AND status = 'OPEN' ORDER BY invoice_id FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (challans.Count != ids.Length)
        {
            throw AppException.Validation("consignment.no_challan", "Every bill in a dispatch needs its packing challan.");
        }

        var challanIds = challans.Select(c => c.Id).ToList();
        var challanLines = await db.PackingChallanLines.AsNoTracking().Where(l => challanIds.Contains(l.ChallanId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var figures = await packing.FiguresAsync(challanLines, cancellationToken).ConfigureAwait(false);
        var ready = challanLines.ToDictionary(l => l.Id, l => PackingService.ReadyToSend(l, figures[l.Id]));
        Dictionary<Guid, decimal> carry;
        if (request.Lines is null)
        {
            carry = ready.Where(r => r.Value > 0).ToDictionary(r => r.Key, r => r.Value);
        }
        else
        {
            carry = request.Lines.GroupBy(l => l.ChallanLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            foreach (var (lineId, quantity) in carry)
            {
                if (!ready.TryGetValue(lineId, out var available))
                {
                    throw AppException.Validation("consignment.line_unknown", "That item is not on these bills' challans.");
                }

                if (quantity > available)
                {
                    var line = challanLines.First(l => l.Id == lineId);
                    throw AppException.Conflict("consignment.not_ready", $"{line.ItemName}: only {available:0.###} {line.UnitCode} is packed and ready to send.");
                }
            }
        }

        if (carry.Count == 0 || carry.Values.All(q => q == 0))
        {
            throw AppException.Conflict("consignment.nothing_packed", "Nothing packed is waiting to go on these bills: pack the goods first.");
        }

        var carriedLines = challanLines.Where(l => carry.GetValueOrDefault(l.Id) > 0).ToList();
        var carriedInvoices = challans.Where(c => carriedLines.Any(l => l.ChallanId == c.Id)).Select(c => c.InvoiceId).Order().ToArray();
        var invoiceLineIds = carriedLines.Select(l => l.InvoiceLineId).ToList();
        var invoiceLines = await db.SalesInvoices.AsNoTracking().SelectMany(i => i.Lines).Where(l => invoiceLineIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => new { l.Total, l.Quantity }, cancellationToken).ConfigureAwait(false);
        // The value of what goes, line by line, as billed (for the e-way bill).
        var goodsValue = carriedLines.Sum(l => decimal.Round(invoiceLines[l.InvoiceLineId].Total * carry[l.Id] / invoiceLines[l.InvoiceLineId].Quantity, 2,
            MidpointRounding.AwayFromZero));

        if (request.DispatchDate < invoices.Max(i => i.BusinessDate) || request.DispatchDate > BusinessCalendar.Today(clock).AddDays(1))
        {
            throw AppException.Validation("consignment.date_invalid", "The dispatch date is between the bill date and tomorrow.");
        }

        var lorry = first.Mode == FulfilmentModes.Lorry ? await LorryAsync(businessId, first, request, cancellationToken).ConfigureAwait(false) : null;
        var expected = request.ExpectedDeliveryDate;
        if (expected is null && lorry is not null)
        {
            var transit = await db.TransporterRoutes.AsNoTracking()
                .Where(r => r.FromBranchId == lorry.Booking.Id && r.ToBranchId == lorry.Destination.Id && r.IsActive).Select(r => (int?)r.TransitDays)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            expected = transit is { } days ? request.DispatchDate.AddDays(days) : null;
        }

        var store = await db.Stores.AsNoTracking().Where(s => s.Id == first.StoreId).Select(s => new { s.Id, s.Code }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var partyName = firstInvoice.DebtorId is { } debtorId
            ? (await PartyNamesAsync([debtorId], cancellationToken).ConfigureAwait(false))[debtorId]
            : firstInvoice.BuyerName ?? "Walk-in customer";
        var now = clock.GetUtcNow();
        var details = new Consignment.Details(first.Mode, request.VehicleNumber, request.DriverName, request.DriverPhone, request.LrNumber, request.LrDate, request.PackageCount,
            request.WeightKg, request.FreightTerms, request.FreightAmount, request.DispatchDate, expected, request.EwayBillNumber);
        // Validate before taking a number, so a refused dispatch leaves no gap in the series.
        var draft = Valid(() => Consignment.Record(businessId, store.Id, "-", details, new Consignment.Party(partyName, first.DeliveryAddress ?? string.Empty), lorry,
            goodsValue, carriedInvoices, (request.IdempotencyKey, requestHash), currentUser.UserId, now, carry));
        if (lorry is not null && await db.Consignments.AnyAsync(c => c.BusinessId == businessId && c.TransporterId == lorry.Transporter.Id &&
                c.LrNumber == draft.LrNumber && c.Status == ConsignmentStatus.Dispatched, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("consignment.lr_taken", $"LR/GR {draft.LrNumber} of {lorry.Transporter.Name} is already recorded.");
        }

        var sequence = await numbers.NextAsync(businessId, store.Id, Series, cancellationToken).ConfigureAwait(false);
        var consignment = Valid(() => Consignment.Record(businessId, store.Id, DocumentNumbers.Format(store.Code, Series, sequence), details,
            new Consignment.Party(partyName, first.DeliveryAddress ?? string.Empty), lorry, goodsValue, carriedInvoices, (request.IdempotencyKey, requestHash),
            currentUser.UserId, now, carry));
        db.Consignments.Add(consignment);
        db.ConsignmentInvoices.AddRange(carriedInvoices.Select(id => new ConsignmentInvoice { ConsignmentId = consignment.Id, InvoiceId = id, BusinessId = businessId }));
        foreach (var challan in challans.Where(c => carriedInvoices.Contains(c.InvoiceId)))
        {
            var sent = string.Join(", ", carriedLines.Where(l => l.ChallanId == challan.Id).Select(l => $"{l.ItemName} {carry[l.Id]:0.###}"));
            db.PackingEvents.Add(PackingEvent.Record(businessId, challan.Id, "DISPATCHED",
                $"{consignment.Number}{(consignment.LrNumber is { } lrNo ? $" (LR {lrNo})" : string.Empty)}: {sent}", currentUser.UserId, now));
        }

        audit.Record("dispatch.recorded", "consignment", consignment.Id, businessId, store.Id, details: new
        {
            consignment.Number,
            consignment.Mode,
            invoices = invoices.Where(i => carriedInvoices.Contains(i.Id)).Select(i => i.Number),
            transporter = lorry?.Transporter.Code,
            consignment.LrNumber,
            consignment.VehicleNumber,
            consignment.FreightTerms,
            consignment.FreightAmount,
            consignment.EwayBillNumber,
            consignment.EwayBillMissing,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ConsignmentAsync(businessId, consignment.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels a dispatch recorded in error; its bills wait for dispatch again and its LR/GR number is free.</summary>
    public async Task<ConsignmentDto> CancelAsync(Guid businessId, Guid consignmentId, CancelConsignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var consignment = await db.Consignments.FirstOrDefaultAsync(c => c.Id == consignmentId && c.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Dispatch");
        db.Entry(consignment).Property(c => c.RowVersion).OriginalValue = request.RowVersion;
        Valid(() => { consignment.Cancel(request.Reason, currentUser.UserId, clock.GetUtcNow()); return consignment; });
        await ChallanEventsAsync(consignment, "DISPATCH_CANCELLED", $"{consignment.Number}: {consignment.CancelReason}", cancellationToken).ConfigureAwait(false);
        audit.Record("dispatch.cancelled", "consignment", consignment.Id, businessId, consignment.StoreId, details: new { consignment.Number, consignment.LrNumber, consignment.CancelReason });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ConsignmentAsync(businessId, consignmentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What reached the customer, once per dispatch. Anything not delivered needs the reason; the bill is not changed.</summary>
    public async Task<ConsignmentDto> ReportDeliveryAsync(Guid businessId, Guid consignmentId, ReportDeliveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Lines);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var consignment = await TrackedAsync(businessId, consignmentId, request.RowVersion, cancellationToken).ConfigureAwait(false);
        if (request.DeliveredOn > BusinessCalendar.Today(clock))
        {
            throw AppException.Validation("delivery.date_invalid", "A delivery is reported once it has happened.");
        }

        var delivered = request.Lines.GroupBy(l => l.ChallanLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        Valid(() => { consignment.ReportDelivery(delivered, request.DeliveredOn, request.Note, currentUser.UserId, clock.GetUtcNow()); return consignment; });
        await ChallanEventsAsync(consignment, consignment.DeliveryOutcome!,
            $"{consignment.Number} on {request.DeliveredOn:dd-MM-yyyy}{(consignment.DeliveryNote is { } note ? $": {note}" : string.Empty)}", cancellationToken).ConfigureAwait(false);
        audit.Record("dispatch.delivery_reported", "consignment", consignment.Id, businessId, consignment.StoreId,
            details: new { consignment.Number, consignment.DeliveryOutcome, request.DeliveredOn, consignment.DeliveryNote, lines = delivered });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ConsignmentAsync(businessId, consignmentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Goods not delivered that came back to the store; they are ready to be sent again (or settled by a credit note).</summary>
    public async Task<ConsignmentDto> RecordReturnAsync(Guid businessId, Guid consignmentId, RecordReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Lines);
        await RequireAsync(Permissions.DispatchManage, businessId, cancellationToken).ConfigureAwait(false);
        var consignment = await TrackedAsync(businessId, consignmentId, request.RowVersion, cancellationToken).ConfigureAwait(false);
        var returned = request.Lines.GroupBy(l => l.ChallanLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        Valid(() => { consignment.RecordReturn(returned, currentUser.UserId, clock.GetUtcNow()); return consignment; });
        var names = await db.PackingChallanLines.AsNoTracking().Where(l => returned.Keys.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.ItemName, cancellationToken)
            .ConfigureAwait(false);
        await ChallanEventsAsync(consignment, "RETURNED",
            $"{consignment.Number}: {string.Join(", ", returned.Where(r => r.Value > 0).Select(r => $"{names.GetValueOrDefault(r.Key, "?")} {r.Value:0.###}"))} back in the store",
            cancellationToken).ConfigureAwait(false);
        audit.Record("dispatch.goods_returned", "consignment", consignment.Id, businessId, consignment.StoreId, details: new { consignment.Number, lines = returned });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ConsignmentAsync(businessId, consignmentId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Consignment> TrackedAsync(Guid businessId, Guid consignmentId, uint rowVersion, CancellationToken cancellationToken)
    {
        var consignment = await db.Consignments.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == consignmentId && c.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Dispatch");
        db.Entry(consignment).Property(c => c.RowVersion).OriginalValue = rowVersion;
        return consignment;
    }

    /// <summary>Notes a dispatch's news on the challan of each bill it carried.</summary>
    private async Task ChallanEventsAsync(Consignment consignment, string kind, string detail, CancellationToken cancellationToken)
    {
        var challanIds = await (from cl in db.ConsignmentLines.AsNoTracking()
                                join l in db.PackingChallanLines.AsNoTracking() on cl.ChallanLineId equals l.Id
                                where cl.ConsignmentId == consignment.Id
                                select l.ChallanId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        db.PackingEvents.AddRange(challanIds.Select(id => PackingEvent.Record(consignment.BusinessId, id, kind, detail, currentUser.UserId, now)));
    }

    /// <summary>Dispatches (the LR/GR register), newest first, by date and optionally lorry service, LR/GR, bill or status.</summary>
    public async Task<IReadOnlyList<ConsignmentDto>> ConsignmentsAsync(Guid businessId, DateOnly? from, DateOnly? to, Guid? transporterId, string? search, string? status,
        CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DispatchView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.Consignments.AsNoTracking().Where(c => c.BusinessId == businessId);
        query = from is { } f ? query.Where(c => c.DispatchDate >= f) : query;
        query = to is { } t ? query.Where(c => c.DispatchDate <= t) : query;
        query = transporterId is { } tr ? query.Where(c => c.TransporterId == tr) : query;
        query = string.IsNullOrWhiteSpace(status) ? query : query.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            var invoiceIds = db.SalesInvoices.Where(i => i.BusinessId == businessId && i.Number == term).Select(i => i.Id);
            query = query.Where(c => c.LrNumber == term || c.Number == term || c.EwayBillNumber == term ||
                                     db.ConsignmentInvoices.Any(ci => ci.ConsignmentId == c.Id && invoiceIds.Contains(ci.InvoiceId)));
        }

        var consignments = await query.OrderByDescending(c => c.DispatchDate).ThenByDescending(c => c.CreatedAtUtc).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await DtosAsync(consignments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConsignmentDto> ConsignmentAsync(Guid businessId, Guid consignmentId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DispatchView, businessId, cancellationToken).ConfigureAwait(false);
        var consignment = await db.Consignments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == consignmentId && c.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Dispatch");
        return (await DtosAsync([consignment], cancellationToken).ConfigureAwait(false))[0];
    }

    // Helpers

    private async Task<List<ConsignmentDto>> DtosAsync(List<Consignment> consignments, CancellationToken cancellationToken)
    {
        var ids = consignments.Select(c => c.Id).ToList();
        var links = await (from ci in db.ConsignmentInvoices.AsNoTracking()
                           join i in db.SalesInvoices.AsNoTracking() on ci.InvoiceId equals i.Id
                           where ids.Contains(ci.ConsignmentId)
                           orderby i.Number
                           select new { ci.ConsignmentId, i.Id, i.Number, i.GrandTotal }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var carried = await (from cl in db.ConsignmentLines.AsNoTracking()
                             join l in db.PackingChallanLines.AsNoTracking() on cl.ChallanLineId equals l.Id
                             join ch in db.PackingChallans.AsNoTracking() on l.ChallanId equals ch.Id
                             where ids.Contains(cl.ConsignmentId)
                             orderby ch.Number, l.LineNumber
                             select new { cl.ConsignmentId, Line = new ConsignmentLineDto(cl.ChallanLineId, ch.InvoiceId, l.ItemName, l.UnitCode, cl.Quantity,
                                 cl.DeliveredQuantity, cl.ReturnedQuantity) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var userIds = consignments.Select(c => c.CreatedByUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
        return consignments.Select(c => new ConsignmentDto(c.Id, c.Number, c.StoreId, c.Mode, c.Status, c.PartyName, c.DeliveryAddress,
                links.Where(l => l.ConsignmentId == c.Id).Select(l => new ConsignmentInvoiceDto(l.Id, l.Number, l.GrandTotal)).ToList(), c.TransporterId, c.TransporterName,
                c.TransporterGstin, c.BookingOffice, c.DestinationBranch, c.VehicleNumber, c.DriverName, c.DriverPhone, c.LrNumber, c.LrDate, c.PackageCount, c.WeightKg,
                c.FreightTerms, c.FreightAmount, c.DispatchDate, c.ExpectedDeliveryDate, c.EwayBillNumber, c.GoodsValue, c.EwayBillMissing, c.CancelReason,
                users.GetValueOrDefault(c.CreatedByUserId, "?"), c.CreatedAtUtc, c.RowVersion,
                carried.Where(x => x.ConsignmentId == c.Id).Select(x => x.Line).ToList(), c.DeliveryOutcome, c.DeliveredOn, c.DeliveryNote, c.ReturnRecordedAtUtc is not null))
            .ToList();
    }

    private async Task<Consignment.Lorry> LorryAsync(Guid businessId, InvoiceFulfilment fulfilment, RecordConsignmentRequest request, CancellationToken cancellationToken)
    {
        var transporter = await db.Transporters.AsNoTracking().FirstAsync(t => t.Id == fulfilment.TransporterId && t.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);
        if (!transporter.IsActive)
        {
            throw AppException.Validation("transporter.inactive", $"{transporter.Name} is no longer used; change the delivery on the bill first.");
        }

        var destinationId = request.DestinationBranchId ?? fulfilment.DestinationBranchId;
        var branches = await db.TransporterBranches.AsNoTracking()
            .Where(b => b.TransporterId == transporter.Id && b.IsActive && (b.Id == request.BookingBranchId || b.Id == destinationId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var booking = branches.FirstOrDefault(b => b.Id == request.BookingBranchId && b.IsBookingOffice)
            ?? throw AppException.Validation("consignment.booking_required", $"Choose the {transporter.Name} office the goods were booked at.");
        var destination = branches.FirstOrDefault(b => b.Id == destinationId && b.IsDestination)
            ?? throw AppException.Validation("consignment.destination_required", $"Choose the {transporter.Name} branch the goods go to.");
        return new Consignment.Lorry(transporter, booking, destination);
    }

    private async Task<InvoiceFulfilment.Choice> ChoiceAsync(Guid businessId, FulfilmentRequest request, CancellationToken cancellationToken)
    {
        var mode = Valid(() => FulfilmentModes.Require(request.Mode));
        if (mode == FulfilmentModes.Lorry && request.TransporterId is { } transporterId)
        {
            if (!await db.Transporters.AnyAsync(t => t.Id == transporterId && t.BusinessId == businessId && t.IsActive, cancellationToken).ConfigureAwait(false))
            {
                throw AppException.Validation("fulfilment.transporter_invalid", "Choose a lorry service from the list.");
            }

            if (request.DestinationBranchId is { } branchId &&
                !await db.TransporterBranches.AnyAsync(b => b.Id == branchId && b.TransporterId == transporterId && b.IsActive && b.IsDestination, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw AppException.Validation("fulfilment.destination_invalid", "Choose a destination branch of that lorry service.");
            }
        }

        return new InvoiceFulfilment.Choice(mode, request.DeliveryAddress, request.ContactPhone, request.TransporterId, request.DestinationBranchId, request.Note);
    }

    private async Task<DeliveryPreferenceDto?> PreferenceDtoAsync(Guid debtorId, CancellationToken cancellationToken)
    {
        var preference = await db.DeliveryPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.DebtorId == debtorId, cancellationToken).ConfigureAwait(false);
        if (preference is null)
        {
            return null;
        }

        var names = await TransportNamesAsync([preference.TransporterId], [preference.DestinationBranchId], cancellationToken).ConfigureAwait(false);
        return new DeliveryPreferenceDto(preference.Mode, preference.TransporterId, Name(names, preference.TransporterId), preference.DestinationBranchId,
            Name(names, preference.DestinationBranchId), preference.DeliveryAddress, preference.RowVersion);
    }

    private async Task<Dictionary<Guid, string>> TransportNamesAsync(IEnumerable<Guid?> transporterIds, IEnumerable<Guid?> branchIds, CancellationToken cancellationToken)
    {
        var tIds = transporterIds.OfType<Guid>().Distinct().ToList();
        var bIds = branchIds.OfType<Guid>().Distinct().ToList();
        var names = await db.Transporters.AsNoTracking().Where(t => tIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken).ConfigureAwait(false);
        foreach (var b in await db.TransporterBranches.AsNoTracking().Where(b => bIds.Contains(b.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[b.Id] = $"{b.Name}, {b.City}";
        }

        return names;
    }

    private async Task<Dictionary<Guid, string>> PartyNamesAsync(IEnumerable<Guid> debtorIds, CancellationToken cancellationToken)
    {
        var ids = debtorIds.Distinct().ToList();
        var debtors = await db.Debtors.AsNoTracking().Where(d => ids.Contains(d.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return debtors.ToDictionary(d => d.Id, d => d.DisplayName);
    }

    private static string? Name(Dictionary<Guid, string> names, Guid? id) => id is { } key ? names.GetValueOrDefault(key) : null;

    private static string Party(Guid? debtorId, string? buyerName, Dictionary<Guid, string> parties) =>
        debtorId is { } id ? parties.GetValueOrDefault(id, "?") : buyerName ?? "Walk-in customer";

    private Task<int> ActiveConsignmentsAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.ConsignmentInvoices.Where(ci => ci.InvoiceId == invoiceId)
            .CountAsync(ci => db.Consignments.Any(c => c.Id == ci.ConsignmentId && c.Status == ConsignmentStatus.Dispatched), cancellationToken);

    private async Task<TransporterDto> TransporterAsync(Guid businessId, Guid transporterId, CancellationToken cancellationToken) =>
        (await TransportersAsync(businessId, cancellationToken).ConfigureAwait(false)).First(t => t.Id == transporterId);

    private async Task<Transporter> FindTransporterAsync(Guid businessId, Guid transporterId, CancellationToken cancellationToken) =>
        await db.Transporters.FirstOrDefaultAsync(t => t.Id == transporterId && t.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.NotFound("Lorry service");

    private async Task RequireUniqueBranchAsync(TransporterBranch branch, CancellationToken cancellationToken)
    {
        if (await db.TransporterBranches.AnyAsync(b => b.TransporterId == branch.TransporterId && b.Id != branch.Id && b.Name == branch.Name && b.City == branch.City,
                cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("branch.exists", $"{branch.Name}, {branch.City} is already a branch of this lorry service.");
        }
    }

    private async Task RequireDebtorAsync(Guid businessId, Guid debtorId, CancellationToken cancellationToken)
    {
        if (!await db.Debtors.AnyAsync(d => d.Id == debtorId && d.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Debtor");
        }
    }

    private static TransporterBranchDto Branch(TransporterBranch b) =>
        new(b.Id, b.Name, b.City, b.Address, b.Phone, b.IsBookingOffice, b.IsDestination, b.IsActive, b.RowVersion);

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

/// <summary>Reads a bill's delivery for the invoice view (shared with billing).</summary>
internal static class FulfilmentReader
{
    public static async Task<FulfilmentDto> ReadAsync(SupermarketBillingDbContext db, Guid invoiceId, CancellationToken cancellationToken)
    {
        var fulfilment = await db.InvoiceFulfilments.AsNoTracking().FirstOrDefaultAsync(f => f.InvoiceId == invoiceId, cancellationToken).ConfigureAwait(false);
        if (fulfilment is null)
        {
            return new FulfilmentDto(FulfilmentModes.Pickup, null, null, null, null, null, null, null, "PICKUP", [], 0);
        }

        var consignments = await (from ci in db.ConsignmentInvoices.AsNoTracking()
                                  join c in db.Consignments.AsNoTracking() on ci.ConsignmentId equals c.Id
                                  where ci.InvoiceId == invoiceId && c.Status == ConsignmentStatus.Dispatched
                                  orderby c.Number
                                  select c.LrNumber == null ? c.Number : c.Number + " (LR " + c.LrNumber + ")").ToListAsync(cancellationToken).ConfigureAwait(false);
        var transporter = fulfilment.TransporterId is { } t
            ? await db.Transporters.AsNoTracking().Where(x => x.Id == t).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var branch = fulfilment.DestinationBranchId is { } b
            ? await db.TransporterBranches.AsNoTracking().Where(x => x.Id == b).Select(x => x.Name + ", " + x.City).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var status = fulfilment.Mode == FulfilmentModes.Pickup ? "PICKUP" : consignments.Count > 0 ? "DISPATCHED" : "AWAITING_DISPATCH";
        var challan = await db.PackingChallans.AsNoTracking().Where(c => c.InvoiceId == invoiceId && c.Status == ChallanStatus.Open)
            .Select(c => new { c.Id, c.Number }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new FulfilmentDto(fulfilment.Mode, fulfilment.DeliveryAddress, fulfilment.ContactPhone, fulfilment.TransporterId, transporter, fulfilment.DestinationBranchId,
            branch, fulfilment.Note, status, consignments, fulfilment.RowVersion, challan?.Id, challan?.Number);
    }
}
