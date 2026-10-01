using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Inventory;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Stock documents, stock reports and inventory settings of a business.</summary>
internal static class StockEndpoints
{
    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder routes)
    {
        var stock = routes.MapGroup("/api/v1/businesses/{businessId:guid}/stock").WithTags("Stock");

        stock.MapPost("/documents", async (Guid businessId, PostStockDocumentRequest r, StockPostingService s, CancellationToken ct) =>
            {
                var document = await s.PostAsync(businessId, r, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/businesses/{businessId}/stock/documents/{document.Id}", document);
            })
            .WithSummary("Post opening stock, an adjustment, damage, wastage, a transfer or a count. Retrying with the same idempotency key returns the same document.");
        stock.MapGet("/documents", (Guid businessId, Guid storeId, string? type, InventoryService s, CancellationToken ct) =>
            s.DocumentsAsync(businessId, storeId, type, ct));
        stock.MapGet("/documents/{documentId:guid}", (Guid businessId, Guid documentId, InventoryService s, CancellationToken ct) =>
            s.DocumentAsync(businessId, documentId, ct));

        stock.MapGet("/on-hand", (Guid businessId, Guid storeId, string? search, bool? lowOnly, InventoryService s, CancellationToken ct) =>
                s.OnHandAsync(businessId, storeId, search, lowOnly ?? false, ct))
            .WithSummary("Stock on hand in a store, with value and reorder status.");
        stock.MapGet("/ledger", (Guid businessId, Guid storeId, Guid variantId, InventoryService s, CancellationToken ct) =>
                s.LedgerAsync(businessId, storeId, variantId, ct))
            .WithSummary("Every movement of one item in one store, newest first.");
        stock.MapGet("/batches", (Guid businessId, Guid storeId, int? expiringWithinDays, InventoryService s, CancellationToken ct) =>
            s.BatchesAsync(businessId, storeId, expiringWithinDays, ct));
        stock.MapGet("/ageing", (Guid businessId, Guid storeId, InventoryService s, CancellationToken ct) => s.AgeingAsync(businessId, storeId, ct));
        stock.MapGet("/valuation", (Guid businessId, InventoryService s, CancellationToken ct) => s.ValuationAsync(businessId, ct));

        stock.MapGet("/settings", (Guid businessId, InventoryService s, CancellationToken ct) => s.SettingsAsync(businessId, ct));
        stock.MapPut("/settings", (Guid businessId, UpdateInventorySettingsRequest r, InventoryService s, CancellationToken ct) =>
                s.UpdateSettingsAsync(businessId, r, ct))
            .WithSummary("Change the valuation method; allowed only before any stock has moved.");
        stock.MapGet("/negative-rules", (Guid businessId, InventoryService s, CancellationToken ct) => s.NegativeRulesAsync(businessId, ct));
        stock.MapPost("/negative-rules", async (Guid businessId, SetNegativeStockRuleRequest r, InventoryService s, CancellationToken ct) =>
            {
                var result = await s.SetNegativeRuleAsync(businessId, r, ct).ConfigureAwait(false);
                return result.ApprovalRequestId is null ? Results.Ok(result) : Results.Accepted(value: result);
            })
            .WithSummary("Set the negative-stock rule for the business, a store, a product or a product in a store. Loosening needs approval.");
        stock.MapPut("/reorder-levels", async (Guid businessId, SetReorderLevelRequest r, InventoryService s, CancellationToken ct) =>
        {
            await s.SetReorderLevelAsync(businessId, r, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        return routes;
    }
}
