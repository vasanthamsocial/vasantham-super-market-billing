using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Infrastructure.Catalog;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Catalogue, prices and tax registration of a business.</summary>
internal static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}");

        var tax = business.MapGroup("/tax-registrations").WithTags("Tax registration");
        tax.MapGet("/", (Guid businessId, TaxRegistrationService s, CancellationToken ct) => s.HistoryAsync(businessId, ct))
            .WithSummary("The business's tax-registration history, newest first, with the entry in force today marked.");
        tax.MapPost("/change-requests", async (Guid businessId, TaxRegistrationChangeRequest r, TaxRegistrationService s, CancellationToken ct) =>
                Results.Accepted(value: await s.RequestChangeAsync(businessId, r, ct).ConfigureAwait(false)))
            .WithSummary("Accountant's request to change the registration; applies only after independent approval.");

        var catalog = business.MapGroup("/catalog").WithTags("Catalogue");
        catalog.MapGet("/units", (Guid businessId, CatalogService s, CancellationToken ct) => s.ListUnitsAsync(businessId, ct));
        catalog.MapPost("/units", async (Guid businessId, CreateUnitRequest r, CatalogService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateUnitAsync(businessId, r, ct).ConfigureAwait(false)));
        catalog.MapGet("/categories", (Guid businessId, CatalogService s, CancellationToken ct) => s.ListCategoriesAsync(businessId, ct));
        catalog.MapPost("/categories", async (Guid businessId, CreateCategoryRequest r, CatalogService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateCategoryAsync(businessId, r, ct).ConfigureAwait(false)));
        catalog.MapGet("/brands", (Guid businessId, CatalogService s, CancellationToken ct) => s.ListBrandsAsync(businessId, ct));
        catalog.MapPost("/brands", async (Guid businessId, CreateBrandRequest r, CatalogService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateBrandAsync(businessId, r, ct).ConfigureAwait(false)));
        catalog.MapGet("/customer-groups", (Guid businessId, CatalogService s, CancellationToken ct) => s.ListCustomerGroupsAsync(businessId, ct));
        catalog.MapPost("/customer-groups", async (Guid businessId, CreateCustomerGroupRequest r, CatalogService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateCustomerGroupAsync(businessId, r, ct).ConfigureAwait(false)));

        catalog.MapGet("/products", (Guid businessId, string? search, bool? includeInactive, int? skip, int? take, CatalogService s, CancellationToken ct) =>
                s.SearchProductsAsync(businessId, search, includeInactive ?? false, skip ?? 0, take ?? 50, ct))
            .WithSummary("Search products by name, code or barcode (paged, at most 200).");
        catalog.MapPost("/products", async (Guid businessId, CreateProductRequest r, CatalogService s, CancellationToken ct) =>
        {
            var product = await s.CreateProductAsync(businessId, r, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/businesses/{businessId}/catalog/products/{product.Id}", product);
        });
        catalog.MapGet("/products/{productId:guid}", (Guid businessId, Guid productId, CatalogService s, CancellationToken ct) =>
            s.GetProductAsync(businessId, productId, ct));
        catalog.MapPut("/products/{productId:guid}", (Guid businessId, Guid productId, UpdateProductRequest r, CatalogService s, CancellationToken ct) =>
            s.UpdateProductAsync(businessId, productId, r, ct));
        catalog.MapPost("/products/{productId:guid}/variants", (Guid businessId, Guid productId, CreateVariantRequest r, CatalogService s, CancellationToken ct) =>
            s.AddVariantAsync(businessId, productId, r, ct));
        catalog.MapPost("/variants/{variantId:guid}/units", (Guid businessId, Guid variantId, AddVariantUnitRequest r, CatalogService s, CancellationToken ct) =>
            s.AddVariantUnitAsync(businessId, variantId, r, ct));
        catalog.MapPost("/variants/{variantId:guid}/barcodes", (Guid businessId, Guid variantId, AddBarcodeRequest r, CatalogService s, CancellationToken ct) =>
            s.AddBarcodeAsync(businessId, variantId, r, ct));
        catalog.MapPost("/barcodes/{barcodeId:guid}/deactivate", async (Guid businessId, Guid barcodeId, CatalogService s, CancellationToken ct) =>
        {
            await s.DeactivateBarcodeAsync(businessId, barcodeId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        catalog.MapPost("/variants/{variantId:guid}/mrps", (Guid businessId, Guid variantId, AddMrpRequest r, CatalogService s, CancellationToken ct) =>
            s.AddMrpAsync(businessId, variantId, r, ct));
        catalog.MapPost("/mrps/{mrpId:guid}/deactivate", async (Guid businessId, Guid mrpId, CatalogService s, CancellationToken ct) =>
        {
            await s.DeactivateMrpAsync(businessId, mrpId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        catalog.MapGet("/lookup", (Guid businessId, string barcode, CatalogService s, CancellationToken ct) => s.LookupBarcodeAsync(businessId, barcode, ct))
            .WithSummary("What a scanned barcode identifies: product, variant, pack and MRPs.");

        var prices = business.MapGroup("/prices").WithTags("Prices");
        prices.MapGet("/variants/{variantId:guid}", (Guid businessId, Guid variantId, bool? includeClosed, PricingService s, CancellationToken ct) =>
            s.ListAsync(businessId, variantId, includeClosed ?? false, ct));
        prices.MapPost("/variants/{variantId:guid}", async (Guid businessId, Guid variantId, CreatePriceRuleRequest r, PricingService s, CancellationToken ct) =>
            {
                var created = await s.CreateAsync(businessId, variantId, r, ct).ConfigureAwait(false);
                return created.ApprovalRequestId is null ? Results.Created(string.Empty, created) : Results.Accepted(value: created);
            })
            .WithSummary("Adds a price. Returns 202 when it must wait for approval.");
        prices.MapPost("/{ruleId:guid}/retire", async (Guid businessId, Guid ruleId, PricingService s, CancellationToken ct) =>
        {
            await s.RetireAsync(businessId, ruleId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        prices.MapGet("/quote", (Guid businessId, Guid variantUnitId, decimal? quantity, string? channel, Guid? storeId, Guid? customerGroupId,
                    bool? member, decimal? mrp, DateTimeOffset? at, PricingService s, CancellationToken ct) =>
                s.QuoteAsync(businessId, variantUnitId, quantity ?? 1m, channel ?? SalesChannels.Retail, storeId, customerGroupId, member ?? false, mrp, at, ct))
            .WithSummary("The price that applies to a sale line, with minimum-price and MRP checks.");

        return routes;
    }
}
