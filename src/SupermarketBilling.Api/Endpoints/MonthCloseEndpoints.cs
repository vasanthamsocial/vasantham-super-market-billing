using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Archiving;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Monthly close and archive packages (spec section 22, D-040).</summary>
internal static class MonthCloseEndpoints
{
    public static IEndpointRouteBuilder MapMonthCloseEndpoints(this IEndpointRouteBuilder routes)
    {
        var months = routes.MapGroup("/api/v1/businesses/{businessId:guid}/months").WithTags("Month close");
        months.MapGet("/", (Guid businessId, MonthCloseService s, CancellationToken ct) => s.MonthsAsync(businessId, ct))
            .WithSummary("Months from the first with records to now: in progress, open or locked, and their packages.");
        months.MapGet("/{month}/checks", (Guid businessId, string month, MonthCloseService s, CancellationToken ct) => s.ChecksAsync(businessId, month, ct))
            .WithSummary("Whether the month can be locked: shifts, cash, rounds, offline queues, reconciliations, earlier months.");
        months.MapPost("/{month}/lock", (Guid businessId, string month, LockMonthRequest r, MonthCloseService s, CancellationToken ct) => s.LockAsync(businessId, month, r, ct))
            .WithSummary("Lock the month (only if every check passes): nothing dated in it can be posted or changed afterwards.");
        months.MapPost("/{month}/package", async (Guid businessId, string month, MonthCloseService s, CancellationToken ct) =>
            {
                var (content, name) = await s.PackageAsync(businessId, month, ct).ConfigureAwait(false);
                return Results.File(content, "application/octet-stream", name);
            })
            .WithSummary("Make the locked month's archive package (encrypted for the archive server, signed by this server).");
        months.MapGet("/{month}/packages", (Guid businessId, string month, MonthCloseService s, CancellationToken ct) => s.PackagesAsync(businessId, month, ct))
            .WithSummary("The packages made for the month, with their checksums.");

        var archive = routes.MapGroup("/api/v1/businesses/{businessId:guid}/archive").WithTags("Month close");
        archive.MapGet("/keys", (Guid businessId, MonthCloseService s, CancellationToken ct) => s.KeysAsync(businessId, ct))
            .WithSummary("This server's signing key (to register on the archive server) and the archive key packages are encrypted for.");
        archive.MapPut("/recipient", (Guid businessId, SetArchiveRecipientRequest r, MonthCloseService s, CancellationToken ct) => s.SetRecipientAsync(businessId, r, ct))
            .WithSummary("Register the archive server's public key.");
        return routes;
    }
}
