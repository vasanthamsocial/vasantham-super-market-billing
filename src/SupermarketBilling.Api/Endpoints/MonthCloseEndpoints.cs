using Microsoft.AspNetCore.Mvc;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Archiving;
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

        // Retention (D-044): the gates and a dry run; off unless Retention:Enabled. Writes are the owner's only.
        var retention = routes.MapGroup("/api/v1/businesses/{businessId:guid}/retention").WithTags("Month close");
        retention.MapGet("/", (Guid businessId, RetentionService s, CancellationToken ct) => s.StatusAsync(businessId, ct))
            .WithSummary("Whether retention is on, the months always kept, and the older months with their archive receipts.");
        retention.MapPut("/receipt-key", (Guid businessId, SetReceiptKeyRequest r, RetentionService s, CancellationToken ct) => s.SetReceiptKeyAsync(businessId, r, ct))
            .WithSummary("Register the archive server's receipt key (owner only).");
        retention.MapPost("/receipts", async (Guid businessId, HttpRequest request, RetentionService s, CancellationToken ct) =>
            {
                if (!request.HasFormContentType)
                {
                    throw AppException.Validation("retention.form_required", "Send the receipt as multipart form data in a field named 'file'.");
                }

                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                var file = form.Files.GetFile("file") ?? throw AppException.Validation("retention.file_required", "Choose the archive receipt (.sbreceipt).");
                if (file.Length > ArchiveReceipt.MaxSize)
                {
                    throw AppException.Validation("retention.receipt_invalid", "This is not an archive receipt.");
                }

                using var buffer = new MemoryStream();
                await using (var content = file.OpenReadStream())
                {
                    await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
                }

                return await s.RegisterReceiptAsync(businessId, buffer.ToArray(), ct).ConfigureAwait(false);
            })
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("Register the archive's receipt for a month (owner only); answers with what removing the month would do.");
        retention.MapGet("/months/{month}", (Guid businessId, string month, RetentionService s, CancellationToken ct) => s.PlanAsync(businessId, month, ct))
            .WithSummary("What removing the month would do: gates, what stops it, what would go and what would stay.");
        retention.MapPost("/months/{month}/remove", (Guid businessId, string month, RemoveMonthRequest r, RetentionService s, CancellationToken ct) =>
                s.RemoveAsync(businessId, month, r, ct))
            .WithSummary("Remove an archived month from this server for good (owner only, every gate passing, confirmed by typing REMOVE and the month).");
        return routes;
    }
}
