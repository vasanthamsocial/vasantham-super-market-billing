using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Catalog;

[Collection(ApiTestGroup.Name)]
public sealed class PricingTests(ApiFactory factory)
{
    private string Prices => $"/api/v1/businesses/{factory.BusinessId}/prices";

    internal static CreatePriceRuleRequest Price(
        Guid pack, decimal price, string type = "STANDARD", bool members = false, decimal? mrp = null, DateTimeOffset? to = null, decimal minQty = 0) =>
        new(pack, type, "RETAIL", price, true, mrp, null, null, members, minQty, null, null, to, null, null);

    [Fact]
    public async Task Quote_follows_the_rules_and_history_is_kept_when_prices_change()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId, mrp: 55m);
        var variant = product.Variants.Single();
        var pack = variant.Units.Single().Id;
        var url = $"{Prices}/variants/{variant.Id}";

        var standard = await (await owner.PostJsonAsync(url, Price(pack, 52m))).Content.ReadFromJsonAsync<CreatePriceRuleResponse>(TestClient.Json);
        Assert.Equal("ACTIVE", standard!.Rule.Status);
        (await owner.PostJsonAsync(url, Price(pack, 49m, "MEMBER", members: true))).EnsureSuccessStatusCode();

        Assert.Equal(52m, (await QuoteAsync(owner, pack, member: false)).UnitPrice);
        var member = await QuoteAsync(owner, pack, member: true);
        Assert.Equal(49m, member.UnitPrice);
        Assert.Equal("MEMBER", member.RateType);

        // Changing the standard price: retire the old rule and add a new one. The old rule remains, retired.
        (await owner.PostJsonAsync($"{Prices}/{standard.Rule.Id}/retire", new { })).EnsureSuccessStatusCode();
        (await owner.PostJsonAsync(url, Price(pack, 53m))).EnsureSuccessStatusCode();
        Assert.Equal(53m, (await QuoteAsync(owner, pack, member: false)).UnitPrice);
        var all = await owner.GetJsonAsync<List<PriceRuleDto>>($"{url}?includeClosed=true");
        Assert.Contains(all, r => r.Id == standard.Rule.Id && r.Status == "RETIRED" && r.Price == 52m);
    }

    [Fact]
    public async Task Prices_above_mrp_are_refused()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId, mrp: 55m);
        var variant = product.Variants.Single();
        var pack = variant.Units.Single().Id;

        var above = await owner.PostJsonAsync($"{Prices}/variants/{variant.Id}", Price(pack, 56m));
        Assert.Equal(HttpStatusCode.BadRequest, above.StatusCode);
        Assert.Equal("price.above_mrp", await above.ProblemCodeAsync());

        // Tax-exclusive 53 + 5% = 55.65, also above the MRP.
        var exclusive = await owner.PostJsonAsync($"{Prices}/variants/{variant.Id}", Price(pack, 53m) with { TaxInclusive = false });
        Assert.Equal("price.above_mrp", await exclusive.ProblemCodeAsync());
    }

    [Fact]
    public async Task Selling_below_the_minimum_price_needs_a_second_persons_approval()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId, mrp: 55m);
        var variant = product.Variants.Single();
        var pack = variant.Units.Single().Id;
        var url = $"{Prices}/variants/{variant.Id}";
        (await owner.PostJsonAsync(url, Price(pack, 52m))).EnsureSuccessStatusCode();
        (await owner.PostJsonAsync(url, Price(pack, 46m, "MINIMUM"))).EnsureSuccessStatusCode();

        var promotion = await owner.PostJsonAsync(url, Price(pack, 44m, "PROMOTIONAL", to: factory.Clock.GetUtcNow().AddDays(3)));
        Assert.Equal(HttpStatusCode.Accepted, promotion.StatusCode);
        var pending = (await promotion.Content.ReadFromJsonAsync<CreatePriceRuleResponse>(TestClient.Json))!;
        Assert.Equal("PENDING_APPROVAL", pending.Rule.Status);
        Assert.Equal(52m, (await QuoteAsync(owner, pack)).UnitPrice); // not used until approved

        Assert.Equal(HttpStatusCode.Forbidden,
            (await owner.PostJsonAsync($"/api/v1/approvals/{pending.ApprovalRequestId}/approve", new ApprovalDecisionRequest(null))).StatusCode);
        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        (await approver.PostJsonAsync($"/api/v1/approvals/{pending.ApprovalRequestId}/approve", new ApprovalDecisionRequest("Clearance"))).EnsureSuccessStatusCode();

        var quote = await QuoteAsync(owner, pack);
        Assert.Equal(44m, quote.UnitPrice);
        Assert.True(quote.BelowMinimum);
        Assert.Equal(46m, quote.MinimumPriceInclusive);
    }

    [Fact]
    public async Task Database_forbids_changing_a_price_rule_in_place()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        var variant = product.Variants.Single();
        var created = await (await owner.PostJsonAsync($"{Prices}/variants/{variant.Id}", Price(variant.Units.Single().Id, 40m)))
            .Content.ReadFromJsonAsync<CreatePriceRuleResponse>(TestClient.Json);

        await using var db = await factory.OpenAppConnectionAsync();
        foreach (var sql in new[]
        {
            "UPDATE price_rules SET price = 1 WHERE id = @id",
            "DELETE FROM price_rules WHERE id = @id",
        })
        {
            await using var command = new NpgsqlCommand(sql, db);
            command.Parameters.AddWithValue("id", created!.Rule.Id);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        }
    }

    private async Task<PriceQuoteDto> QuoteAsync(TestClient client, Guid pack, bool member = false) =>
        await client.GetJsonAsync<PriceQuoteDto>($"{Prices}/quote?variantUnitId={pack}&quantity=1&member={member.ToString().ToLowerInvariant()}");
}

/// <summary>A business that requires approval for every price, on its own installation.</summary>
public sealed class PriceApprovalPolicyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task With_the_policy_on_every_price_waits_and_rejected_prices_are_never_used()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var business = await owner.GetJsonAsync<BusinessDto>($"/api/v1/businesses/{factory.BusinessId}");
        (await owner.PutJsonAsync($"/api/v1/businesses/{factory.BusinessId}", new UpdateBusinessRequest(
            business.LegalName, business.TradeName, business.StateCode, business.Gstin, business.Address, false, business.RowVersion, RequirePriceApproval: true)))
            .EnsureSuccessStatusCode();

        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        var variant = product.Variants.Single();
        var pack = variant.Units.Single().Id;
        var url = $"/api/v1/businesses/{factory.BusinessId}/prices/variants/{variant.Id}";

        var first = (await (await owner.PostJsonAsync(url, PricingTests.Price(pack, 30m))).Content.ReadFromJsonAsync<CreatePriceRuleResponse>(TestClient.Json))!;
        var second = (await (await owner.PostJsonAsync(url, PricingTests.Price(pack, 31m, "MEMBER", members: true))).Content.ReadFromJsonAsync<CreatePriceRuleResponse>(TestClient.Json))!;
        Assert.Equal("PENDING_APPROVAL", first.Rule.Status);
        Assert.Null((await owner.GetJsonAsync<PriceQuoteDto>($"/api/v1/businesses/{factory.BusinessId}/prices/quote?variantUnitId={pack}")).UnitPrice);

        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        (await approver.PostJsonAsync($"/api/v1/approvals/{first.ApprovalRequestId}/approve", new ApprovalDecisionRequest(null))).EnsureSuccessStatusCode();
        (await approver.PostJsonAsync($"/api/v1/approvals/{second.ApprovalRequestId}/reject", new ApprovalDecisionRequest("Too low"))).EnsureSuccessStatusCode();

        var rules = await owner.GetJsonAsync<List<PriceRuleDto>>($"{url}?includeClosed=true");
        Assert.Equal("ACTIVE", rules.Single(r => r.Id == first.Rule.Id).Status);
        Assert.Equal("REJECTED", rules.Single(r => r.Id == second.Rule.Id).Status);
        var memberQuote = await owner.GetJsonAsync<PriceQuoteDto>($"/api/v1/businesses/{factory.BusinessId}/prices/quote?variantUnitId={pack}&member=true");
        Assert.Equal(30m, memberQuote.UnitPrice);
    }
}
