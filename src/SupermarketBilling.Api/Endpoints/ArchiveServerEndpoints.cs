using Microsoft.AspNetCore.Mvc;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Archiving;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>The archive server's endpoints (D-042): served only in archive mode with ARCHIVE_WEB_ENABLED=true.</summary>
internal static class ArchiveServerEndpoints
{
    /// <summary>A month's package of a busy supermarket stays well under this.</summary>
    private const long PackageUploadLimit = 120L * 1024 * 1024;

    public static IEndpointRouteBuilder MapArchiveServerEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/archive/setup", async (ArchiveSetupRequest r, ArchiveServerService s, CancellationToken ct) =>
            {
                await s.SetupAsync(r, ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .RequireRateLimiting(AuthEndpoints.AuthRateLimitPolicy)
            .WithTags("Archive")
            .WithSummary("Creates the archive's company and owner administrator. Requires the one-time setup code from the archive server.");

        var archive = routes.MapGroup("/api/v1/archive").WithTags("Archive");
        archive.MapGet("/me", (ArchiveServerService s, CancellationToken ct) => s.MeAsync(ct)).WithSummary("The signed-in user's archive roles and permissions.");
        archive.MapGet("/roles", () => ArchiveServerService.Roles());

        var users = archive.MapGroup("/users");
        users.MapGet("/", (ArchiveServerService s, CancellationToken ct) => s.UsersAsync(ct));
        users.MapPost("/", async (CreateArchiveUserRequest r, ArchiveServerService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateUserAsync(r, ct).ConfigureAwait(false)));
        users.MapPost("/{userId:guid}/grants", (Guid userId, ArchiveGrantRequest r, ArchiveServerService s, CancellationToken ct) => s.GrantAsync(userId, r, ct));
        users.MapDelete("/{userId:guid}/grants/{grantId:guid}", async (Guid userId, Guid grantId, ArchiveServerService s, CancellationToken ct) =>
        {
            await s.RevokeAsync(userId, grantId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        users.MapPut("/{userId:guid}/active", (Guid userId, SetUserActiveRequest r, ArchiveServerService s, CancellationToken ct) => s.SetActiveAsync(userId, r.IsActive, ct));
        users.MapPost("/{userId:guid}/unlock", (Guid userId, ArchiveServerService s, CancellationToken ct) => s.UnlockAsync(userId, ct));
        users.MapPost("/{userId:guid}/password-reset", (Guid userId, ArchiveServerService s, CancellationToken ct) => s.IssuePasswordResetAsync(userId, ct))
            .WithSummary("Issues a one-time reset code. It is shown once; hand it to the user in person.");

        archive.MapGet("/keys", (ArchiveServerService s, CancellationToken ct) => s.KeysAsync(ct))
            .WithSummary("This archive's public key, to register on each store server (its packages are encrypted for it).");
        archive.MapGet("/sources", (ArchiveServerService s, CancellationToken ct) => s.SourcesAsync(ct));
        archive.MapPost("/sources", async (RegisterArchiveSourceRequest r, ArchiveServerService s, CancellationToken ct) =>
                Results.Created(string.Empty, await s.RegisterSourceAsync(r, ct).ConfigureAwait(false)))
            .WithSummary("Trust a store server's packages (its signing public key from its Month close screen).");
        archive.MapPost("/sources/{sourceId:guid}/revoke", async (Guid sourceId, ArchiveServerService s, CancellationToken ct) =>
        {
            await s.RevokeSourceAsync(sourceId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        archive.MapGet("/businesses", (ArchiveServerService s, CancellationToken ct) => s.BusinessesAsync(ct));
        archive.MapGet("/imports", (ArchiveServerService s, CancellationToken ct) => s.ImportsAsync(ct));
        archive.MapPost("/imports", async (HttpRequest request, ArchiveServerService s, CancellationToken ct) =>
            {
                if (!request.HasFormContentType)
                {
                    throw AppException.Validation("archive.form_required", "Send the package as multipart form data in a field named 'file'.");
                }

                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                var file = form.Files.GetFile("file") ?? throw AppException.Validation("archive.file_required", "Choose the package (.sbarc) to import.");
                using var buffer = new MemoryStream();
                await using (var content = file.OpenReadStream())
                {
                    await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
                }

                var import = await s.ImportAsync(buffer.ToArray(), ct).ConfigureAwait(false);
                return import.AlreadyImported ? Results.Ok(import) : Results.Created(string.Empty, import);
            })
            .WithMetadata(new RequestSizeLimitAttribute(PackageUploadLimit))
            .WithSummary("Imports a month's package from a trusted store server, verifying it fully. The same month again adds nothing.");
        archive.MapPost("/imports/{importId:guid}/approve-accounts", (Guid importId, ArchiveApprovalRequest r, ArchiveServerService s, CancellationToken ct) =>
                s.ApproveAsync(importId, asOwner: false, r, ct))
            .WithSummary("The accountant approves an imported month.");
        archive.MapPost("/imports/{importId:guid}/approve-owner", (Guid importId, ArchiveApprovalRequest r, ArchiveServerService s, CancellationToken ct) =>
                s.ApproveAsync(importId, asOwner: true, r, ct))
            .WithSummary("The owner approves an imported month, after the accountant (a different person).");
        return routes;
    }
}
