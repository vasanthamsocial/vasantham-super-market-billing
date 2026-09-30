using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.UnitTests.Catalog;

public sealed class PricingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 4, 30, 0, TimeSpan.Zero);
    private static readonly Guid Business = Guid.NewGuid();
    private static readonly Guid Variant = Guid.NewGuid();
    private static readonly Guid Pack = Guid.NewGuid();
    private static readonly Guid StoreA = Guid.NewGuid();
    private static readonly Guid Hotels = Guid.NewGuid();

    private static PriceRule Rule(
        string type, decimal price, string channel = SalesChannels.Retail, bool inclusive = true, decimal? mrp = null,
        Guid? store = null, Guid? group = null, bool members = false, decimal minQty = 0, decimal? maxQty = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int? priority = null, bool pending = false) =>
        PriceRule.Create(Business, Variant, Pack, type, channel, price, inclusive, mrp, store, group, members, minQty, maxQty,
            from ?? Now.AddDays(-30), to, priority, null, pending, Guid.NewGuid(), Now.AddDays(-30));

    private static PriceQuery Query(
        decimal qty = 1, string channel = SalesChannels.Retail, Guid? store = null, Guid? group = null, bool member = false,
        decimal? mrp = null, decimal tax = 5, DateTimeOffset? at = null) =>
        new(Pack, qty, channel, store, group, member, mrp, tax, at ?? Now);

    [Fact]
    public void Standard_price_applies_when_nothing_more_specific_does()
    {
        var standard = Rule(RateTypes.Standard, 52m);

        var quote = PriceResolver.Resolve([standard, Rule(RateTypes.Member, 49m, members: true)], Query());

        Assert.Same(standard, quote.Rule);
        Assert.Equal(52m, quote.UnitPrice);
    }

    [Fact]
    public void Member_group_slab_and_promotion_win_in_priority_order()
    {
        var rules = new[]
        {
            Rule(RateTypes.Standard, 52m),
            Rule(RateTypes.QuantitySlab, 50m, minQty: 10),
            Rule(RateTypes.Member, 49m, members: true),
            Rule(RateTypes.CustomerGroup, 47m, group: Hotels),
            Rule(RateTypes.Promotional, 45m, to: Now.AddDays(3)),
        };

        Assert.Equal(45m, PriceResolver.Resolve(rules, Query(qty: 12, member: true, group: Hotels)).UnitPrice);
        var withoutPromotion = rules[..4];
        Assert.Equal(47m, PriceResolver.Resolve(withoutPromotion, Query(qty: 12, member: true, group: Hotels)).UnitPrice);
        Assert.Equal(49m, PriceResolver.Resolve(withoutPromotion, Query(qty: 12, member: true)).UnitPrice);
        Assert.Equal(50m, PriceResolver.Resolve(withoutPromotion, Query(qty: 12)).UnitPrice);
        Assert.Equal(52m, PriceResolver.Resolve(withoutPromotion, Query(qty: 9)).UnitPrice);
    }

    [Fact]
    public void Wholesale_and_retail_prices_do_not_mix()
    {
        var rules = new[] { Rule(RateTypes.Standard, 52m), Rule(RateTypes.Standard, 48m, channel: SalesChannels.Wholesale) };

        Assert.Equal(52m, PriceResolver.Resolve(rules, Query(channel: SalesChannels.Retail)).UnitPrice);
        Assert.Equal(48m, PriceResolver.Resolve(rules, Query(channel: SalesChannels.Wholesale)).UnitPrice);
    }

    [Fact]
    public void Store_price_applies_only_in_its_store()
    {
        var rules = new[] { Rule(RateTypes.Standard, 52m), Rule(RateTypes.StoreSpecific, 51m, store: StoreA) };

        Assert.Equal(51m, PriceResolver.Resolve(rules, Query(store: StoreA)).UnitPrice);
        Assert.Equal(52m, PriceResolver.Resolve(rules, Query(store: Guid.NewGuid())).UnitPrice);
    }

    [Fact]
    public void Time_limited_prices_apply_only_within_their_window()
    {
        var promotion = Rule(RateTypes.Promotional, 45m, from: Now.AddDays(1), to: Now.AddDays(3));
        var rules = new[] { Rule(RateTypes.Standard, 52m), promotion };

        Assert.Equal(52m, PriceResolver.Resolve(rules, Query(at: Now)).UnitPrice);
        Assert.Equal(45m, PriceResolver.Resolve(rules, Query(at: Now.AddDays(2))).UnitPrice);
        Assert.Equal(52m, PriceResolver.Resolve(rules, Query(at: Now.AddDays(3))).UnitPrice); // end is exclusive
    }

    [Fact]
    public void Price_for_a_specific_mrp_applies_only_to_stock_with_that_mrp()
    {
        var rules = new[] { Rule(RateTypes.Standard, 52m), Rule(RateTypes.Standard, 54m, mrp: 55m) };

        Assert.Equal(54m, PriceResolver.Resolve(rules, Query(mrp: 55m)).UnitPrice);
        Assert.Equal(52m, PriceResolver.Resolve(rules, Query(mrp: 53m)).UnitPrice);
    }

    [Fact]
    public void Retired_pending_and_rejected_prices_are_never_used()
    {
        var retired = Rule(RateTypes.Promotional, 40m, to: Now.AddDays(3));
        retired.Retire(Guid.NewGuid(), Now);
        var pending = Rule(RateTypes.Promotional, 41m, to: Now.AddDays(3), pending: true);
        var rejected = Rule(RateTypes.Promotional, 42m, to: Now.AddDays(3), pending: true);
        rejected.Reject(null);

        var quote = PriceResolver.Resolve([Rule(RateTypes.Standard, 52m), retired, pending, rejected], Query());

        Assert.Equal(52m, quote.UnitPrice);
    }

    [Fact]
    public void Quote_flags_prices_below_the_minimum_selling_price()
    {
        var rules = new[] { Rule(RateTypes.Promotional, 44m, to: Now.AddDays(3)), Rule(RateTypes.MinimumSellingPrice, 46m) };

        var quote = PriceResolver.Resolve(rules, Query());

        Assert.Equal(44m, quote.UnitPrice);
        Assert.Equal(46m, quote.MinimumPriceInclusive);
        Assert.True(quote.BelowMinimum);
    }

    [Fact]
    public void Minimum_price_is_never_itself_a_selling_price()
    {
        var quote = PriceResolver.Resolve([Rule(RateTypes.MinimumSellingPrice, 46m)], Query());

        Assert.Null(quote.Rule);
        Assert.Null(quote.UnitPrice);
    }

    [Fact]
    public void Tax_exclusive_prices_are_compared_with_mrp_after_adding_tax()
    {
        var exclusive = Rule(RateTypes.Standard, 50m, inclusive: false);

        // 50 + 5% = 52.50, above an MRP of 52.00.
        var quote = PriceResolver.Resolve([exclusive], Query(mrp: 52m, tax: 5));

        Assert.Equal(52.50m, quote.UnitPriceInclusive);
        Assert.True(quote.AboveMrp);
        Assert.Throws<DomainException>(() => exclusive.EnsureNotAboveMrp(52m, 5));
        exclusive.EnsureNotAboveMrp(52.50m, 5);
    }

    [Theory]
    [InlineData(RateTypes.StoreSpecific, "price.store_required")]
    [InlineData(RateTypes.CustomerGroup, "price.group_required")]
    [InlineData(RateTypes.Member, "price.members_only")]
    [InlineData(RateTypes.QuantitySlab, "price.slab_required")]
    [InlineData(RateTypes.Promotional, "price.promotion_end_required")]
    public void Each_rate_type_requires_its_condition(string rateType, string expectedCode)
    {
        var error = Assert.Throws<DomainException>(() => Rule(rateType, 50m));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void Retiring_is_final_and_prices_cannot_be_reactivated()
    {
        var rule = Rule(RateTypes.Standard, 52m);
        rule.Retire(Guid.NewGuid(), Now);

        Assert.Throws<DomainException>(() => rule.Retire(Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => rule.Activate(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.23456)]
    public void Invalid_prices_are_rejected(decimal price)
    {
        Assert.Throws<DomainException>(() => Rule(RateTypes.Standard, price));
    }

    [Fact]
    public void Price_math_rounds_to_paise_half_away_from_zero()
    {
        Assert.Equal(10.50m, PriceMath.InclusiveOf(10m, false, 5m));
        Assert.Equal(1.06m, PriceMath.InclusiveOf(1.005m, false, 5m)); // 1.05525 -> 1.06
        Assert.Equal(118.00m, PriceMath.InclusiveOf(100m, false, 18m));
    }
}
