using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;
using static SupermarketBilling.IntegrationTests.Inventory.StockTests;

namespace SupermarketBilling.IntegrationTests.Inventory;

/// <summary>Own installation: the valuation method can only be chosen before any stock moves.</summary>
public sealed class FefoValuationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Fefo_issues_the_batch_that_expires_first_whatever_order_it_arrived_in()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        (await owner.PutJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stock/settings", new UpdateInventorySettingsRequest("FEFO")))
            .EnsureSuccessStatusCode();
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId, tracksBatches: true, tracksExpiry: true);

        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId,
            Line(product, 10, cost: 5m) with { BatchNumber = "LATE", ExpiresOn = new DateOnly(2027, 3, 31) }));
        await PostAsync(owner, factory.BusinessId, Doc("ADJUSTMENT", factory.MainStoreId,
            Line(product, 4, "IN", 6m) with { BatchNumber = "SOON", ExpiresOn = new DateOnly(2026, 12, 31) }));

        var damage = await PostAsync(owner, factory.BusinessId, Doc("DAMAGE", factory.MainStoreId, Line(product, 6)));
        Assert.Collection(damage.Movements,
            m => Assert.Equal(("SOON", -4m, 6m), (m.BatchNumber, m.Quantity, m.UnitCost)),
            m => Assert.Equal(("LATE", -2m, 5m), (m.BatchNumber, m.Quantity, m.UnitCost)));

        var onHand = await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product);
        Assert.Equal((8m, 40m), (onHand!.Quantity, onHand.Value));
        var valuation = Assert.Single(await owner.GetJsonAsync<List<StockValuationDto>>($"/api/v1/businesses/{factory.BusinessId}/stock/valuation"));
        Assert.Equal(("FEFO", 40m), (valuation.ValuationMethod, valuation.TotalValue));
    }
}

public sealed class WeightedAverageValuationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Weighted_average_issues_at_the_running_average_cost()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        (await owner.PutJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stock/settings", new UpdateInventorySettingsRequest("WEIGHTED_AVERAGE")))
            .EnsureSuccessStatusCode();
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);

        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 10, cost: 5m)));
        await PostAsync(owner, factory.BusinessId, Doc("ADJUSTMENT", factory.MainStoreId, Line(product, 10, "IN", 7m)));
        var damage = await PostAsync(owner, factory.BusinessId, Doc("DAMAGE", factory.MainStoreId, Line(product, 5)));
        Assert.Equal((-5m, 6m, -30m), (damage.Movements.Single().Quantity, damage.Movements.Single().UnitCost, damage.Movements.Single().Value));

        await PostAsync(owner, factory.BusinessId, Doc("ADJUSTMENT", factory.MainStoreId, Line(product, 5, "IN", 2.5m)));
        var onHand = await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product);
        Assert.Equal(20m, onHand!.Quantity);
        Assert.Equal(5.125m, onHand.AverageCost); // (15 x 6 + 5 x 2.5) / 20
        Assert.Equal(102.5m, onHand.Value);
    }
}
