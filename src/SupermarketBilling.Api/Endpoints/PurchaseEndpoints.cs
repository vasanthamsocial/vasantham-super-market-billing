using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Purchases;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Suppliers, purchase settings and goods receipts.</summary>
internal static class PurchaseEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseEndpoints(this IEndpointRouteBuilder routes)
    {
        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}").WithTags("Purchases");

        business.MapGet("/suppliers", (Guid businessId, string? search, SupplierService s, CancellationToken ct) => s.ListAsync(businessId, search, ct));
        business.MapPost("/suppliers", async (Guid businessId, CreateSupplierRequest r, SupplierService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateAsync(businessId, r, ct).ConfigureAwait(false)));
        business.MapPut("/suppliers/{supplierId:guid}", (Guid businessId, Guid supplierId, UpdateSupplierRequest r, SupplierService s, CancellationToken ct) =>
            s.UpdateAsync(businessId, supplierId, r, ct));

        business.MapGet("/purchase-settings", (Guid businessId, SupplierService s, CancellationToken ct) => s.SettingsAsync(businessId, ct));
        business.MapPut("/purchase-settings", (Guid businessId, PurchaseSettingsDto r, SupplierService s, CancellationToken ct) => s.UpdateSettingsAsync(businessId, r, ct))
            .WithSummary("Cost-change thresholds (reason, approval) and whether loss-leader prices are allowed.");

        business.MapGet("/grns", (Guid businessId, Guid storeId, string? status, GrnService s, CancellationToken ct) => s.ListAsync(businessId, storeId, status, ct));
        business.MapPost("/grns/preview", (Guid businessId, GrnRequest r, GrnService s, CancellationToken ct) => s.PreviewAsync(businessId, r, ct))
            .WithSummary("Computes a goods receipt (taxes, expense allocation, landed costs) and lists what stops it from being saved.");
        business.MapPost("/grns", async (Guid businessId, GrnRequest r, GrnService s, CancellationToken ct) =>
            {
                var grn = await s.CreateAsync(businessId, r, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/businesses/{businessId}/grns/{grn.Id}", grn);
            })
            .WithSummary("Saves a goods receipt: stock goes in now, or after a manager approves a large cost change or a loss-leader price.");
        business.MapGet("/grns/{grnId:guid}", (Guid businessId, Guid grnId, GrnService s, CancellationToken ct) => s.GetAsync(businessId, grnId, ct));
        return routes;
    }
}
