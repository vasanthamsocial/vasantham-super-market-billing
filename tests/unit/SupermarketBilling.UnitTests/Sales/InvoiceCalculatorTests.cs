using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.UnitTests.Sales;

public sealed class InvoiceCalculatorTests
{
    private const string Regular = "GST_REGULAR";

    private static BillLineInput Line(decimal qty, decimal price, bool inclusive = true, decimal gst = 5, decimal cess = 0, string supply = "TAXABLE", decimal discount = 0) =>
        new(qty, price, inclusive, supply, gst, cess, discount);

    private static BillResult Calc(string mode, bool interState, decimal billDiscount, params BillLineInput[] lines) =>
        InvoiceCalculator.Calculate(new BillInput(mode, interState, lines, billDiscount));

    [Fact]
    public void Tax_inclusive_price_carves_equal_cgst_and_sgst_out_of_the_amount_paid()
    {
        var bill = Calc(Regular, false, 0, Line(2, 52.50m));
        var line = Assert.Single(bill.Lines);
        Assert.Equal((105.00m, 100.00m, 2.50m, 2.50m, 0m, 105.00m), (line.Gross, line.Taxable, line.Cgst, line.Sgst, line.Igst, line.Total));
        Assert.Equal(("TAX_INVOICE", 105m, 0m), (bill.Kind, bill.GrandTotal, bill.RoundOff));
    }

    [Fact]
    public void Inclusive_rounding_puts_the_paise_into_the_taxable_value()
    {
        // 99 / 1.18 = 83.898..; CGST = SGST = 9% of 83.90 = 7.551 -> 7.55; taxable = 99 - 15.10.
        var line = Calc(Regular, false, 0, Line(1, 99m, gst: 18)).Lines[0];
        Assert.Equal((83.90m, 7.55m, 7.55m, 99m), (line.Taxable, line.Cgst, line.Sgst, line.Total));
    }

    [Fact]
    public void Tax_exclusive_inter_state_adds_igst_and_the_total_is_rounded_to_the_rupee()
    {
        var bill = Calc(Regular, true, 0, Line(3, 33.33m, inclusive: false, gst: 12));
        var line = bill.Lines[0];
        Assert.Equal((99.99m, 0m, 0m, 12.00m, 111.99m), (line.Taxable, line.Cgst, line.Sgst, line.Igst, line.Total));
        Assert.Equal((112m, 0.01m), (bill.GrandTotal, bill.RoundOff));
    }

    [Fact]
    public void Cess_is_charged_on_the_same_taxable_value()
    {
        var line = Calc(Regular, false, 0, Line(1, 140m, gst: 28, cess: 12)).Lines[0];
        Assert.Equal((100m, 14m, 14m, 12m, 140m), (line.Taxable, line.Cgst, line.Sgst, line.Cess, line.Total));
    }

    [Fact]
    public void Bill_discount_is_shared_in_proportion_and_adds_up_exactly()
    {
        var bill = Calc(Regular, false, 10m, Line(1, 100m, supply: "EXEMPT", gst: 0), Line(1, 50m, supply: "EXEMPT", gst: 0));
        Assert.Equal([6.67m, 3.33m], bill.Lines.Select(l => l.BillDiscount));
        Assert.Equal((150m, 10m, 140m), (bill.Gross, bill.Discount, bill.GrandTotal));
        Assert.Equal("BILL_OF_SUPPLY", bill.Kind); // a regular seller selling only exempt goods
    }

    [Theory]
    [InlineData(0.10, new[] { 1.0, 1.0, 1.0 }, new[] { 0.04, 0.03, 0.03 })]
    [InlineData(1.00, new[] { 1.0, 2.0 }, new[] { 0.33, 0.67 })]
    [InlineData(5.00, new[] { 0.0, 10.0 }, new[] { 0.0, 5.0 })]
    public void Apportion_uses_largest_remainders(double amount, double[] weights, double[] expected)
    {
        var shares = InvoiceCalculator.Apportion((decimal)amount, weights.Select(w => (decimal)w).ToList());
        Assert.Equal(expected.Select(e => (decimal)e), shares);
    }

    [Fact]
    public void Item_discount_on_an_inclusive_line_reduces_the_amount_paid_and_the_tax()
    {
        var line = Calc(Regular, false, 0, Line(1, 110m, gst: 10, discount: 11m)).Lines[0];
        Assert.Equal((99m, 90m, 4.50m, 4.50m), (line.Total, line.Taxable, line.Cgst, line.Sgst));
    }

    [Theory]
    [InlineData("GST_COMPOSITION", "BILL_OF_SUPPLY")]
    [InlineData("NOT_GST_REGISTERED", "INVOICE")]
    public void Only_a_regular_seller_collects_tax(string mode, string kind)
    {
        var bill = Calc(mode, false, 0, Line(1, 105m), Line(1, 100m, inclusive: false, gst: 18));
        Assert.Equal(kind, bill.Kind);
        Assert.All(bill.Lines, l => Assert.Equal(0m, l.Tax));
        Assert.Equal(205m, bill.GrandTotal);
    }

    [Fact]
    public void Weighed_quantities_are_priced_to_the_paisa()
    {
        var line = Calc(Regular, false, 0, Line(1.235m, 80m, supply: "EXEMPT", gst: 0)).Lines[0];
        Assert.Equal(98.80m, line.Total);
    }

    [Theory]
    [InlineData(0, 10, 0, 0, "invoice.quantity_invalid")]
    [InlineData(1, 10, 11, 0, "invoice.discount_too_large")]
    [InlineData(1, 10, 0, 11, "invoice.discount_too_large")]
    [InlineData(1, -1, 0, 0, "invoice.amount_invalid")]
    public void Impossible_bills_are_refused(double qty, double price, double itemDiscount, double billDiscount, string code)
    {
        var error = Assert.Throws<DomainException>(() =>
            Calc(Regular, false, (decimal)billDiscount, Line((decimal)qty, (decimal)price, discount: (decimal)itemDiscount)));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Random_bills_always_add_up()
    {
        var random = new Random(20261001);
        decimal[] rates = [0, 5, 12, 18, 28];
        for (var run = 0; run < 2000; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 8)).Select(_ =>
            {
                var qty = random.Next(1, 5000) / 1000m;
                var price = random.Next(1, 100000) / 100m;
                var gross = InvoiceCalculator.Money(qty * price);
                var discount = random.Next(4) == 0 ? InvoiceCalculator.Money(gross * random.Next(0, 30) / 100m) : 0m;
                return new BillLineInput(qty, price, random.Next(2) == 0, "TAXABLE", rates[random.Next(rates.Length)], random.Next(5) == 0 ? 12 : 0, discount);
            }).ToArray();
            var available = lines.Sum(l => InvoiceCalculator.Money(l.Quantity * l.UnitPrice) - l.ItemDiscount);
            var billDiscount = random.Next(3) == 0 ? InvoiceCalculator.Money(available * random.Next(0, 20) / 100m) : 0m;
            var interState = random.Next(4) == 0;

            var bill = InvoiceCalculator.Calculate(new BillInput(Regular, interState, lines, billDiscount));

            for (var i = 0; i < lines.Length; i++)
            {
                var (input, line) = (lines[i], bill.Lines[i]);
                Assert.Equal(line.Total, line.Taxable + line.Cgst + line.Sgst + line.Igst + line.Cess);
                Assert.Equal(line.Cgst, line.Sgst);
                Assert.True(interState ? line.Cgst == 0 : line.Igst == 0);
                Assert.Equal(line.Gross - line.ItemDiscount - line.BillDiscount, input.TaxInclusive ? line.Total : line.Taxable);
                Assert.True(line.Taxable >= 0 && line.Tax >= 0);
                Assert.Equal(line.Total, decimal.Round(line.Total, 2));
            }

            Assert.Equal(billDiscount, bill.Lines.Sum(l => l.BillDiscount));
            Assert.Equal(bill.GrandTotal, bill.Lines.Sum(l => l.Total) + bill.RoundOff);
            Assert.True(Math.Abs(bill.RoundOff) <= 0.50m);
            Assert.Equal(bill.GrandTotal, decimal.Round(bill.GrandTotal, 0));
        }
    }
}
