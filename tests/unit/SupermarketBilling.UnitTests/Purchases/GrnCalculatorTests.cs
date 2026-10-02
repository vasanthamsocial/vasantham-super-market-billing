using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Purchases;

namespace SupermarketBilling.UnitTests.Purchases;

public sealed class GrnCalculatorTests
{
    private static GrnLineInput Line(decimal qty, decimal rate, decimal free = 0, decimal factor = 1, decimal discount = 0, decimal gst = 5, decimal? weight = null) =>
        new(qty, free, factor, rate, discount, gst, 0, weight);

    [Fact]
    public void Recoverable_gst_stays_out_of_the_cost_and_free_goods_share_it()
    {
        // 10 cases of 12 at Rs. 240 a case, 1 case free, 5% GST recoverable: 2400 over 132 pieces.
        var result = GrnCalculator.Calculate(new GrnInput([Line(10, 240, free: 1, factor: 12)], [], InterState: false, ChargesGst: true, TaxRecoverable: true));
        var line = result.Lines[0];
        Assert.Equal((2400m, 60m, 60m, 2520m, 132m, 2400m), (line.Taxable, line.Cgst, line.Sgst, line.Total, line.BaseQuantity, line.LandedTotal));
        Assert.Equal(18.1818m, line.LandedUnitCost);
    }

    [Fact]
    public void Tax_that_cannot_be_recovered_is_part_of_the_cost()
    {
        var line = GrnCalculator.Calculate(new GrnInput([Line(100, 10, gst: 18)], [], InterState: true, ChargesGst: true, TaxRecoverable: false)).Lines[0];
        Assert.Equal((180m, 180m, 1180m, 11.80m), (line.Igst, line.NonRecoverableTax, line.LandedTotal, line.LandedUnitCost));
    }

    [Fact]
    public void Freight_is_shared_by_value_and_loading_by_quantity_adding_up_exactly()
    {
        var result = GrnCalculator.Calculate(new GrnInput(
            [Line(10, 100), Line(20, 25), Line(5, 10)],
            [new GrnExpenseInput("FREIGHT", 100m, "VALUE"), new GrnExpenseInput("LOADING", 35m, "QUANTITY")],
            InterState: false, ChargesGst: true, TaxRecoverable: true));
        // Values 1000 : 500 : 50 -> 64.52, 32.26, 3.22; quantities 10 : 20 : 5 -> 10, 20, 5.
        Assert.Equal([64.52m, 32.26m, 3.22m], result.Allocations[0]);
        Assert.Equal([10m, 20m, 5m], result.Allocations[1]);
        Assert.Equal(100m, result.Allocations[0].Sum());
        Assert.Equal((135m, result.Taxable + 135m), (result.Expenses, result.LandedTotal));
        Assert.Equal(107.452m, result.Lines[0].LandedUnitCost);
    }

    [Fact]
    public void Manual_allocation_must_reconcile_with_the_expense()
    {
        GrnInput Input(params decimal[] manual) =>
            new([Line(1, 100), Line(1, 100)], [new GrnExpenseInput("TRANSPORT", 50m, "MANUAL", manual)], false, true, true);
        Assert.Equal([30m, 20m], GrnCalculator.Calculate(Input(30, 20)).Allocations[0]);
        Assert.Equal("grn.manual_allocation_mismatch", Assert.Throws<DomainException>(() => GrnCalculator.Calculate(Input(30, 19.99m))).Code);
    }

    [Fact]
    public void Weight_allocation_needs_every_weight() =>
        Assert.Equal("grn.weight_required", Assert.Throws<DomainException>(() => GrnCalculator.Calculate(new GrnInput(
            [Line(1, 100, weight: 5), Line(1, 100)], [new GrnExpenseInput("FREIGHT", 10m, "WEIGHT")], false, true, true))).Code);

    [Fact]
    public void A_bill_of_supply_carries_no_gst()
    {
        var line = GrnCalculator.Calculate(new GrnInput([Line(10, 50)], [], false, ChargesGst: false, TaxRecoverable: false)).Lines[0];
        Assert.Equal((0m, 500m, 50m), (line.Tax, line.Total, line.LandedUnitCost));
    }

    [Fact]
    public void Random_receipts_always_reconcile()
    {
        var random = new Random(7);
        string[] methods = ["QUANTITY", "VALUE", "EQUAL", "WEIGHT"];
        for (var run = 0; run < 500; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 8)).Select(_ => Line(random.Next(1, 50), random.Next(1, 50000) / 100m, free: random.Next(0, 3),
                factor: random.Next(1, 13), gst: new[] { 0m, 5m, 12m, 18m }[random.Next(4)], weight: random.Next(1, 100))).ToList();
            var expenses = Enumerable.Range(0, random.Next(0, 4)).Select(_ => new GrnExpenseInput("FREIGHT", random.Next(1, 100000) / 100m, methods[random.Next(methods.Length)])).ToList();
            var recoverable = random.Next(2) == 0;
            var result = GrnCalculator.Calculate(new GrnInput(lines, expenses, random.Next(2) == 0, true, recoverable));

            for (var e = 0; e < expenses.Count; e++)
            {
                Assert.Equal(expenses[e].Amount, result.Allocations[e].Sum());
            }

            Assert.Equal(result.Taxable + (recoverable ? 0 : result.Cgst + result.Sgst + result.Igst + result.Cess) + result.Expenses, result.LandedTotal);
            Assert.All(result.Lines, l => Assert.True(Math.Abs((l.LandedUnitCost * l.BaseQuantity) - l.LandedTotal) <= l.BaseQuantity * 0.00005m));
        }
    }

    [Theory]
    [InlineData(10, 10.4, 5, 15, 4.0, false, false)]
    [InlineData(10, 10.6, 5, 15, 6.0, true, false)]
    [InlineData(10, 8.4, 5, 15, -16.0, true, true)]
    public void Cost_changes_need_a_reason_or_an_approval_by_size(double previous, double next, double reason, double approval, double percent, bool needsReason, bool needsApproval)
    {
        var change = PurchaseRules.CompareCost((decimal)previous, (decimal)next, (decimal)reason, (decimal)approval);
        Assert.NotNull(change);
        Assert.Equal(((decimal)percent, needsReason, needsApproval), (change.PercentChange, change.NeedsReason, change.NeedsApproval));
    }

    [Fact]
    public void First_receipt_or_same_cost_is_no_change()
    {
        Assert.Null(PurchaseRules.CompareCost(null, 10m, 5, 15));
        Assert.Null(PurchaseRules.CompareCost(10m, 10m, 5, 15));
    }

    [Fact]
    public void Selling_below_landed_cost_is_measured_without_output_gst()
    {
        // Cost 50/piece; price 55 inclusive of 18% = 46.61 net: a loss of 3.39 a piece.
        var loss = PurchaseRules.CheckSellingPrice(50m, 1, 55m, 18, sellerCollectsTax: true);
        Assert.NotNull(loss);
        Assert.Equal((50m, 46.61m, 3.39m, -7.27m), (loss.CostPerPack, loss.SellingNetPerPack, loss.LossPerPack, loss.MarginPercent));
        Assert.Null(PurchaseRules.CheckSellingPrice(50m, 1, 59m, 18, sellerCollectsTax: true)); // 50.00 net: not below
        Assert.Null(PurchaseRules.CheckSellingPrice(50m, 1, 55m, 18, sellerCollectsTax: false)); // composition: price is the seller's
    }
}
