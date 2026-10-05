using SupermarketBilling.Api.Security;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Accounts;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Offline collections: trusted collection phones, synchronising what they recorded without signal, and the quarantine.</summary>
internal static class OfflineEndpoints
{
    /// <summary>Marks this browser as a trusted collection phone. HttpOnly: page scripts cannot read or leak it.</summary>
    public const string DeviceCookie = "sb_collection_device";

    public static IEndpointRouteBuilder MapOfflineEndpoints(this IEndpointRouteBuilder routes)
    {
        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}/collections").WithTags("Collections");
        business.MapGet("/devices", (Guid businessId, OfflineCollectionService s, CancellationToken ct) => s.DevicesAsync(businessId, ct))
            .WithSummary("Phones trusted for offline collection, with their limits.");
        business.MapPost("/devices", async (Guid businessId, EnrolCollectionDeviceRequest r, HttpContext http, OfflineCollectionService s, CancellationToken ct) =>
            {
                var (device, token) = await s.EnrolAsync(businessId, r, DeviceToken(http), ct).ConfigureAwait(false);
                http.Response.Cookies.Append(DeviceCookie, token, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = AuthCookies.Options(http).SecureCookies,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    IsEssential = true,
                    MaxAge = TimeSpan.FromDays(365),
                });
                return Results.Created(string.Empty, device);
            })
            .WithSummary("Enrol this browser (the collector's phone) for offline collection (sets an HttpOnly device cookie).");
        business.MapPut("/devices/{deviceId:guid}", (Guid businessId, Guid deviceId, CollectionDeviceLimitsRequest r, OfflineCollectionService s, CancellationToken ct) =>
            s.SetLimitsAsync(businessId, deviceId, r, ct));
        business.MapPost("/devices/{deviceId:guid}/revoke", async (Guid businessId, Guid deviceId, OfflineCollectionService s, CancellationToken ct) =>
        {
            await s.RevokeAsync(businessId, deviceId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        business.MapGet("/offline", (Guid businessId, string? status, Guid? collectorUserId, OfflineCollectionService s, CancellationToken ct) =>
                s.SubmissionsAsync(businessId, status, collectorUserId, ct))
            .WithSummary("Collections received from phones (a collector sees their own), for example status=QUARANTINED.");
        business.MapPost("/offline/{submissionId:guid}/resolve", (Guid businessId, Guid submissionId, ResolveOfflineRequest r, OfflineCollectionService s,
                CancellationToken ct) => s.ResolveAsync(businessId, submissionId, r, ct))
            .WithSummary("A manager posts a quarantined collection as a receipt, or decides it is not posted, with why.");

        var phone = routes.MapGroup("/api/v1/collections/device").WithTags("Collections");
        phone.MapGet("/", async (HttpContext http, OfflineCollectionService s, CancellationToken ct) =>
                await s.MineAsync(DeviceToken(http), ct).ConfigureAwait(false) is { } mine ? Results.Ok(mine) : Results.NoContent())
            .WithSummary("This phone, if it is enrolled for the signed-in collector: its limits and last sequence.");
        phone.MapPost("/sync", (OfflineSyncRequest r, HttpContext http, OfflineCollectionService s, CancellationToken ct) => s.SyncAsync(DeviceToken(http), r, ct))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024 * 1024))
            .WithSummary("Collections recorded on this phone without signal, in order: each is posted or quarantined; a resend gets its first result.");
        return routes;
    }

    private static string? DeviceToken(HttpContext http) => http.Request.Cookies.TryGetValue(DeviceCookie, out var token) ? token : null;
}
