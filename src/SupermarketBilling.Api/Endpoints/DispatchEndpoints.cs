using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Dispatch;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Lorry services, how bills are delivered, and dispatches (the LR/GR register).</summary>
internal static class DispatchEndpoints
{
    public static IEndpointRouteBuilder MapDispatchEndpoints(this IEndpointRouteBuilder routes)
    {
        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}").WithTags("Dispatch");

        business.MapGet("/transporters", (Guid businessId, DispatchService s, CancellationToken ct) => s.TransportersAsync(businessId, ct))
            .WithSummary("Lorry services with their booking offices, destination branches and routes.");
        business.MapPost("/transporters", async (Guid businessId, CreateTransporterRequest r, DispatchService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateTransporterAsync(businessId, r, ct).ConfigureAwait(false)));
        business.MapPut("/transporters/{transporterId:guid}", (Guid businessId, Guid transporterId, UpdateTransporterRequest r, DispatchService s, CancellationToken ct) =>
            s.UpdateTransporterAsync(businessId, transporterId, r, ct));
        business.MapPost("/transporters/{transporterId:guid}/branches", async (Guid businessId, Guid transporterId, TransporterBranchRequest r, DispatchService s,
                CancellationToken ct) =>
            Results.Created(string.Empty, await s.AddBranchAsync(businessId, transporterId, r, ct).ConfigureAwait(false)));
        business.MapPut("/transporters/{transporterId:guid}/branches/{branchId:guid}", (Guid businessId, Guid transporterId, Guid branchId, TransporterBranchRequest r,
                DispatchService s, CancellationToken ct) =>
            s.UpdateBranchAsync(businessId, transporterId, branchId, r, ct));
        business.MapPost("/transporters/{transporterId:guid}/routes", async (Guid businessId, Guid transporterId, CreateTransporterRouteRequest r, DispatchService s,
                CancellationToken ct) =>
            Results.Created(string.Empty, await s.AddRouteAsync(businessId, transporterId, r, ct).ConfigureAwait(false)));
        business.MapPut("/transporters/{transporterId:guid}/routes/{routeId:guid}", (Guid businessId, Guid transporterId, Guid routeId, UpdateTransporterRouteRequest r,
                DispatchService s, CancellationToken ct) =>
            s.UpdateRouteAsync(businessId, transporterId, routeId, r, ct));

        business.MapGet("/invoices/{invoiceId:guid}/fulfilment", (Guid businessId, Guid invoiceId, DispatchService s, CancellationToken ct) =>
            s.FulfilmentAsync(businessId, invoiceId, ct));
        business.MapPut("/invoices/{invoiceId:guid}/fulfilment", (Guid businessId, Guid invoiceId, FulfilmentRequest r, DispatchService s, CancellationToken ct) =>
                s.ChangeFulfilmentAsync(businessId, invoiceId, r, ct))
            .WithSummary("How the bill's goods reach the customer; can be changed until they are dispatched. The invoice itself never changes.");
        business.MapGet("/debtors/{debtorId:guid}/delivery", (Guid businessId, Guid debtorId, DispatchService s, CancellationToken ct) =>
            s.PreferenceAsync(businessId, debtorId, ct));
        business.MapPut("/debtors/{debtorId:guid}/delivery", (Guid businessId, Guid debtorId, DeliveryPreferenceRequest r, DispatchService s, CancellationToken ct) =>
                s.ChangePreferenceAsync(businessId, debtorId, r, ct))
            .WithSummary("How the debtor usually gets their goods (filled in on their bills at the counter).");

        business.MapGet("/dispatch/queue", (Guid businessId, Guid? storeId, DispatchService s, CancellationToken ct) => s.QueueAsync(businessId, storeId, ct))
            .WithSummary("Bills whose goods are waiting to be dispatched.");
        business.MapGet("/consignments", (Guid businessId, DateOnly? from, DateOnly? to, Guid? transporterId, string? search, string? status, DispatchService s,
                CancellationToken ct) => s.ConsignmentsAsync(businessId, from, to, transporterId, search, status, ct))
            .WithSummary("Dispatches (LR/GR register), filtered by date, lorry service, status, or an LR/GR, dispatch, e-way bill or invoice number.");
        business.MapGet("/consignments/{consignmentId:guid}", (Guid businessId, Guid consignmentId, DispatchService s, CancellationToken ct) =>
            s.ConsignmentAsync(businessId, consignmentId, ct));
        business.MapPost("/consignments", async (Guid businessId, RecordConsignmentRequest r, DispatchService s, CancellationToken ct) =>
                Results.Created(string.Empty, await s.RecordAsync(businessId, r, ct).ConfigureAwait(false)))
            .WithSummary("Goods leave the store: one lorry booking (LR/GR) or one trip, for one customer's bills.");
        business.MapPost("/consignments/{consignmentId:guid}/cancel", (Guid businessId, Guid consignmentId, CancelConsignmentRequest r, DispatchService s,
                CancellationToken ct) => s.CancelAsync(businessId, consignmentId, r, ct))
            .WithSummary("Cancels a dispatch recorded in error (kept with the reason); its bills wait for dispatch again.");
        return routes;
    }
}
