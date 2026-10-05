using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Sales;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Offline counter billing (D-039): the device's limits, the pack for its counter agent, receiving its bills, and decisions.</summary>
internal static class OfflineBillingEndpoints
{
    public static IEndpointRouteBuilder MapOfflineBillingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPut("/api/v1/businesses/{businessId:guid}/counters/{counterId:guid}/devices/{deviceId:guid}/offline",
                (Guid businessId, Guid counterId, Guid deviceId, CounterOfflineRequest r, OfflineBillingService s, CancellationToken ct) =>
                    s.SetOfflineAsync(businessId, counterId, deviceId, r, ct))
            .WithTags("Counters")
            .WithSummary("Let this device bill without the server within limits (all empty: stop).");

        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}/offline-bills").WithTags("POS");
        business.MapGet("/", (Guid businessId, string? status, Guid? counterId, OfflineBillingService s, CancellationToken ct) =>
                s.ListAsync(businessId, status, counterId, ct))
            .WithSummary("Bills counters issued without the server (status QUARANTINED, REVIEW, POSTED...).");
        business.MapPost("/{billId:guid}/resolve", (Guid businessId, Guid billId, ResolveOfflineBillRequest r, OfflineBillingService s, CancellationToken ct) =>
                s.ResolveAsync(businessId, billId, r, ct))
            .WithSummary("Post a quarantined offline bill now, or record it as not posted, with why.");
        business.MapPost("/{billId:guid}/review", (Guid businessId, Guid billId, ReviewOfflineBillRequest r, OfflineBillingService s, CancellationToken ct) =>
                s.ReviewAsync(businessId, billId, r, ct))
            .WithSummary("Record that what was flagged on a posted offline bill has been checked.");

        var pos = routes.MapGroup("/api/v1/pos/offline").WithTags("POS");
        pos.MapGet("/pack", (HttpContext http, OfflineBillingService s, CancellationToken ct) => s.PackAsync(DeviceToken(http), ct))
            .WithSummary("Items, prices, series and limits for this device's counter agent to bill offline.");
        pos.MapPost("/sync", (OfflineBillsSyncRequest r, HttpContext http, OfflineBillingService s, CancellationToken ct) => s.SyncAsync(DeviceToken(http), r, ct))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4 * 1024 * 1024))
            .WithSummary("Bills issued without the server, in order: each is posted (or kept for a manager); a resend gets its first result.");
        return routes;
    }

    private static string? DeviceToken(HttpContext http) =>
        http.Request.Cookies.TryGetValue(SalesEndpoints.DeviceCookie, out var token) ? token : null;
}
