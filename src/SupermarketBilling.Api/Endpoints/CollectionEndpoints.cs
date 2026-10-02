using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Accounts;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Routes, collection plans, assigned visits, promises to pay, collector absences and the collector's day.</summary>
internal static class CollectionEndpoints
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder routes)
    {
        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}").WithTags("Collections");

        business.MapGet("/routes", (Guid businessId, CollectionService s, CancellationToken ct) => s.RoutesAsync(businessId, ct));
        business.MapPost("/routes", async (Guid businessId, CreateRouteRequest r, CollectionService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateRouteAsync(businessId, r, ct).ConfigureAwait(false)));
        business.MapPut("/routes/{routeId:guid}", (Guid businessId, Guid routeId, UpdateRouteRequest r, CollectionService s, CancellationToken ct) =>
            s.UpdateRouteAsync(businessId, routeId, r, ct));
        business.MapGet("/collectors", (Guid businessId, CollectionService s, CancellationToken ct) => s.CollectorsAsync(businessId, ct))
            .WithSummary("People who can collect in the business.");

        business.MapGet("/collection-plans", (Guid businessId, Guid? routeId, Guid? collectorUserId, CollectionService s, CancellationToken ct) =>
            s.PlansAsync(businessId, routeId, collectorUserId, ct));
        business.MapGet("/debtors/{debtorId:guid}/collection-plan", (Guid businessId, Guid debtorId, CollectionService s, CancellationToken ct) =>
            s.PlanAsync(businessId, debtorId, ct));
        business.MapPut("/debtors/{debtorId:guid}/collection-plan", (Guid businessId, Guid debtorId, SetCollectionPlanRequest r, CollectionService s, CancellationToken ct) =>
                s.SetPlanAsync(businessId, debtorId, r, ct))
            .WithSummary("Route, visit sequence, collectors, preferred time and schedule of a debtor.");

        business.MapGet("/collection-visits", (Guid businessId, DateOnly date, Guid? collectorUserId, CollectionService s, CancellationToken ct) =>
            s.VisitsAsync(businessId, date, collectorUserId, ct));
        business.MapPost("/collection-visits", async (Guid businessId, AssignVisitRequest r, CollectionService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.AssignVisitAsync(businessId, r, ct).ConfigureAwait(false)));
        business.MapPost("/collection-visits/{visitId:guid}/cancel", async (Guid businessId, Guid visitId, CollectionService s, CancellationToken ct) =>
        {
            await s.CancelVisitAsync(businessId, visitId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        business.MapGet("/debtors/{debtorId:guid}/promises", (Guid businessId, Guid debtorId, CollectionService s, CancellationToken ct) =>
            s.PromisesAsync(businessId, debtorId, ct));
        business.MapPost("/debtors/{debtorId:guid}/promises", async (Guid businessId, Guid debtorId, RecordPromiseRequest r, CollectionService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.RecordPromiseAsync(businessId, debtorId, r, ct).ConfigureAwait(false)));
        business.MapPost("/promises/{promiseId:guid}/cancel", async (Guid businessId, Guid promiseId, CollectionService s, CancellationToken ct) =>
        {
            await s.CancelPromiseAsync(businessId, promiseId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        business.MapGet("/collector-absences", (Guid businessId, DateOnly since, CollectionService s, CancellationToken ct) => s.AbsencesAsync(businessId, since, ct));
        business.MapPost("/collector-absences", async (Guid businessId, RecordAbsenceRequest r, CollectionService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.RecordAbsenceAsync(businessId, r, ct).ConfigureAwait(false)));
        business.MapDelete("/collector-absences/{absenceId:guid}", async (Guid businessId, Guid absenceId, CollectionService s, CancellationToken ct) =>
        {
            await s.RemoveAbsenceAsync(businessId, absenceId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        business.MapGet("/collections/day", (Guid businessId, Guid? collectorUserId, DateOnly? date, bool? includeOverdue, CollectionService s, CancellationToken ct) =>
                s.DayAsync(businessId, collectorUserId, date, includeOverdue ?? false, ct))
            .WithSummary("The parties a collector visits on a day, with why and their balances (one's own day without a collector).");
        return routes;
    }
}
