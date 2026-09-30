using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Businesses, stores, users, roles, approvals and audit. All require a fully signed-in session.</summary>
internal static class AdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1");

        api.MapGet("/roles", () => UserAdminService.ListRoles()).WithTags("Users");

        var businesses = api.MapGroup("/businesses").WithTags("Businesses");
        businesses.MapGet("/", (OrganisationService s, CancellationToken ct) => s.ListBusinessesAsync(ct));
        businesses.MapPost("/", async (CreateBusinessRequest r, OrganisationService s, CancellationToken ct) =>
        {
            var business = await s.CreateBusinessAsync(r, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/businesses/{business.Id}", business);
        });
        businesses.MapGet("/{businessId:guid}", (Guid businessId, OrganisationService s, CancellationToken ct) => s.GetBusinessAsync(businessId, ct));
        businesses.MapPut("/{businessId:guid}", (Guid businessId, UpdateBusinessRequest r, OrganisationService s, CancellationToken ct) =>
            s.UpdateBusinessAsync(businessId, r, ct));

        var stores = businesses.MapGroup("/{businessId:guid}/stores").WithTags("Stores");
        stores.MapGet("/", (Guid businessId, OrganisationService s, CancellationToken ct) => s.ListStoresAsync(businessId, ct));
        stores.MapPost("/", async (Guid businessId, CreateStoreRequest r, OrganisationService s, CancellationToken ct) =>
        {
            var store = await s.CreateStoreAsync(businessId, r, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/businesses/{businessId}/stores/{store.Id}", store);
        });
        stores.MapPut("/{storeId:guid}", (Guid businessId, Guid storeId, UpdateStoreRequest r, OrganisationService s, CancellationToken ct) =>
            s.UpdateStoreAsync(businessId, storeId, r, ct));

        var users = businesses.MapGroup("/{businessId:guid}/users").WithTags("Users");
        users.MapGet("/", (Guid businessId, UserAdminService s, CancellationToken ct) => s.ListUsersAsync(businessId, ct));
        users.MapPost("/", async (Guid businessId, CreateUserRequest r, UserAdminService s, CancellationToken ct) =>
        {
            var created = await s.CreateUserAsync(businessId, r, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/businesses/{businessId}/users/{created.User.Id}", created);
        });
        users.MapPut("/{userId:guid}/active", (Guid businessId, Guid userId, SetUserActiveRequest r, UserAdminService s, CancellationToken ct) =>
            s.SetActiveAsync(businessId, userId, r.IsActive, ct));
        users.MapPost("/{userId:guid}/unlock", (Guid businessId, Guid userId, UserAdminService s, CancellationToken ct) =>
            s.UnlockAsync(businessId, userId, ct));
        users.MapPost("/{userId:guid}/password-reset", (Guid businessId, Guid userId, UserAdminService s, CancellationToken ct) =>
                s.IssuePasswordResetAsync(businessId, userId, ct))
            .WithSummary("Issues a one-time reset code. It is shown once; hand it to the user in person.");
        users.MapPost("/{userId:guid}/mfa-reset", (Guid businessId, Guid userId, UserAdminService s, CancellationToken ct) =>
                s.ResetMfaAsync(businessId, userId, ct))
            .WithSummary("Turns off a user's two-step verification (lost phone) and signs them out.");
        users.MapPost("/{userId:guid}/sessions/revoke", async (Guid businessId, Guid userId, UserAdminService s, CancellationToken ct) =>
            Results.Ok(new { revoked = await s.RevokeSessionsAsync(businessId, userId, ct).ConfigureAwait(false) }));
        users.MapPost("/{userId:guid}/roles", async (Guid businessId, Guid userId, GrantRoleRequest r, UserAdminService s, CancellationToken ct) =>
            {
                var outcome = await s.GrantRoleAsync(businessId, userId, r, ct).ConfigureAwait(false);
                return outcome.Outcome == "pending_approval" ? Results.Accepted(value: outcome) : Results.Ok(outcome);
            })
            .WithSummary("Grants a role. Privileged roles return 202 and wait for a second person's approval.");
        users.MapDelete("/{userId:guid}/roles/{assignmentId:guid}", async (Guid businessId, Guid userId, Guid assignmentId, UserAdminService s, CancellationToken ct) =>
        {
            await s.RevokeRoleAsync(businessId, userId, assignmentId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        businesses.MapGet("/{businessId:guid}/approvals", (Guid businessId, string? status, ApprovalService s, CancellationToken ct) =>
            s.ListAsync(businessId, status, ct)).WithTags("Approvals");
        var approvals = api.MapGroup("/approvals/{approvalId:guid}").WithTags("Approvals");
        approvals.MapPost("/approve", async (Guid approvalId, ApprovalDecisionRequest r, ApprovalService s, CancellationToken ct) =>
        {
            await s.ApproveAsync(approvalId, r.Note, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        approvals.MapPost("/reject", async (Guid approvalId, ApprovalDecisionRequest r, ApprovalService s, CancellationToken ct) =>
        {
            await s.RejectAsync(approvalId, r.Note, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        approvals.MapPost("/cancel", async (Guid approvalId, ApprovalService s, CancellationToken ct) =>
        {
            await s.CancelAsync(approvalId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        businesses.MapGet("/{businessId:guid}/audit", (Guid businessId, long? before, int? limit, AuditQueryService s, CancellationToken ct) =>
            s.ListAsync(businessId, before, limit ?? 100, ct)).WithTags("Audit");

        return routes;
    }
}
