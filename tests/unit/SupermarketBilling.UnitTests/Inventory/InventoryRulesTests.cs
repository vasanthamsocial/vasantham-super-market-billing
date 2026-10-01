using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Inventory;

namespace SupermarketBilling.UnitTests.Inventory;

public sealed class InventoryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 4, 30, 0, TimeSpan.Zero);
    private static readonly Guid BatchA = Guid.NewGuid();
    private static readonly Guid BatchB = Guid.NewGuid();

    // Received first, expires last.
    private static readonly LayerSnapshot Old = new(Guid.NewGuid(), BatchA, new DateOnly(2027, 6, 30), Now.AddDays(-20), 1, 10m, 20m);

    // Received later, expires first.
    private static readonly LayerSnapshot Newer = new(Guid.NewGuid(), BatchB, new DateOnly(2026, 12, 31), Now.AddDays(-5), 2, 10m, 22m);

    [Fact]
    public void Fifo_takes_the_oldest_receipt_first()
    {
        var plan = IssuePlanner.Plan([Newer, Old], 12m, ValuationMethods.Fifo, null);

        Assert.Equal([(Old.LayerId, 10m), (Newer.LayerId, 2m)], plan.Takes.Select(t => (t.LayerId, t.Quantity)));
        Assert.Equal(0m, plan.Shortfall);
    }

    [Fact]
    public void Fefo_takes_the_earliest_expiry_first()
    {
        var plan = IssuePlanner.Plan([Old, Newer], 12m, ValuationMethods.Fefo, null);

        Assert.Equal([(Newer.LayerId, 10m), (Old.LayerId, 2m)], plan.Takes.Select(t => (t.LayerId, t.Quantity)));
    }

    [Fact]
    public void Fefo_puts_undated_stock_last()
    {
        var undated = Old with { LayerId = Guid.NewGuid(), BatchId = null, ExpiryDate = null, ReceiptSequence = 0 };

        var plan = IssuePlanner.Plan([undated, Newer], 3m, ValuationMethods.Fefo, null);

        Assert.Equal(Newer.LayerId, Assert.Single(plan.Takes).LayerId);
    }

    [Fact]
    public void A_named_batch_is_taken_only_from_that_batch_and_reports_the_shortfall()
    {
        var plan = IssuePlanner.Plan([Old, Newer], 15m, ValuationMethods.Fifo, BatchB);

        Assert.Equal(Newer.LayerId, Assert.Single(plan.Takes).LayerId);
        Assert.Equal(10m, plan.Covered);
        Assert.Equal(5m, plan.Shortfall);
    }

    [Fact]
    public void Shortfall_is_reported_when_layers_run_out()
    {
        var plan = IssuePlanner.Plan([Old], 10.5m, ValuationMethods.Fifo, null);

        Assert.Equal(0.5m, plan.Shortfall);
    }

    [Fact]
    public void Issues_must_be_positive()
    {
        Assert.Throws<DomainException>(() => IssuePlanner.Plan([Old], 0m, ValuationMethods.Fifo, null));
    }

    [Theory]
    [InlineData(10, 20, 10, 22, 21)]
    [InlineData(0, 0, 5, 30, 30)]
    [InlineData(-3, 25, 5, 30, 30)] // recovering from negative stock restarts the average
    [InlineData(3, 10, 1, 11, 10.25)]
    [InlineData(3, 1, 3, 2, 1.5)]
    public void Average_cost_after_receipt(decimal onHand, decimal average, decimal received, decimal cost, decimal expected)
    {
        Assert.Equal(expected, StockMath.AverageAfterReceipt(onHand, average, received, cost));
    }

    [Fact]
    public void Average_cost_is_rounded_to_four_decimals()
    {
        // (1 x 10 + 2 x 11) / 3 = 10.66666...
        Assert.Equal(10.6667m, StockMath.AverageAfterReceipt(1, 10, 2, 11));
    }

    [Fact]
    public void Movement_value_is_exact_decimal_arithmetic()
    {
        Assert.Equal(-37.5m, StockMath.Value(-1.5m, 25m));
        Assert.Equal(0.3333m, StockMath.Value(0.333m, 1.001m)); // 0.333333 -> 0.3333
    }

    [Theory]
    [InlineData(NegativeStockModes.Disabled, null, -0.001, false, false)]
    [InlineData(NegativeStockModes.Disabled, null, 0, false, true)]
    [InlineData(NegativeStockModes.WarnWithOverride, null, -5, false, false)]
    [InlineData(NegativeStockModes.WarnWithOverride, null, -5, true, true)]
    [InlineData(NegativeStockModes.EnabledWithLimit, 10d, -10, false, true)]
    [InlineData(NegativeStockModes.EnabledWithLimit, 10d, -10.5, true, false)]
    public void Negative_stock_policy(string mode, double? limit, double balanceAfter, bool overrideApproved, bool allowed)
    {
        var policy = new NegativeStockPolicy(mode, limit is null ? null : (decimal)limit);

        var result = policy.Check((decimal)balanceAfter, overrideApproved);
        Assert.True(allowed == (result is null), $"policy={policy} balance={(decimal)balanceAfter} result={result}");
    }

    [Fact]
    public void Most_specific_negative_stock_rule_wins()
    {
        var business = Guid.NewGuid();
        var store = Guid.NewGuid();
        var product = Guid.NewGuid();
        var rules = new[]
        {
            NegativeStockRule.Create(business, null, null, NegativeStockModes.WarnWithOverride, null, "Business default", Guid.NewGuid(), null, true, Now),
            NegativeStockRule.Create(business, store, null, NegativeStockModes.EnabledWithLimit, 5, "Busy store", Guid.NewGuid(), null, true, Now),
            NegativeStockRule.Create(business, null, product, NegativeStockModes.Disabled, null, "Controlled item", Guid.NewGuid(), null, true, Now),
        };

        Assert.Equal(NegativeStockModes.Disabled, NegativeStockRule.Resolve(rules, store, product).Mode);
        Assert.Equal(NegativeStockModes.EnabledWithLimit, NegativeStockRule.Resolve(rules, store, Guid.NewGuid()).Mode);
        Assert.Equal(NegativeStockModes.WarnWithOverride, NegativeStockRule.Resolve(rules, Guid.NewGuid(), Guid.NewGuid()).Mode);
        Assert.Equal(NegativeStockModes.Disabled, NegativeStockRule.Resolve([], store, product).Mode);
    }

    [Fact]
    public void Valuation_method_is_locked_once_stock_has_moved()
    {
        var settings = InventorySettings.Create(Guid.NewGuid(), ValuationMethods.Fifo);

        settings.ChangeValuationMethod(ValuationMethods.Fefo, anyStockMovement: false);
        Assert.Equal(ValuationMethods.Fefo, settings.ValuationMethod);
        Assert.Equal("stock.valuation_locked",
            Assert.Throws<DomainException>(() => settings.ChangeValuationMethod(ValuationMethods.WeightedAverage, anyStockMovement: true)).Code);
    }

    [Fact]
    public void Layers_cannot_be_overdrawn()
    {
        var layer = CostLayer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, 5m, 10m, Now);

        layer.Consume(5m);
        Assert.Throws<DomainException>(() => layer.Consume(0.001m));
    }

    [Fact]
    public void Batches_validate_number_and_dates()
    {
        Assert.Throws<DomainException>(() => Batch.Create(Guid.NewGuid(), Guid.NewGuid(), "bad batch!", null, null, Now));
        Assert.Throws<DomainException>(() => Batch.Create(Guid.NewGuid(), Guid.NewGuid(), "B1", new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 1), Now));
        Assert.Equal("LOT-7", Batch.Create(Guid.NewGuid(), Guid.NewGuid(), " lot-7 ", null, new DateOnly(2027, 1, 1), Now).BatchNumber);
    }
}
