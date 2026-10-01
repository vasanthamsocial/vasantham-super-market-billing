using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.UnitTests.Sales;

public sealed class ReturnCalculatorTests
{
    private static readonly BillLineResult Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static BillLineResult Sum(BillLineResult a, BillLineResult b) =>
        new(a.Gross + b.Gross, a.ItemDiscount + b.ItemDiscount, a.BillDiscount + b.BillDiscount, a.Taxable + b.Taxable, a.Cgst + b.Cgst, a.Sgst + b.Sgst,
            a.Igst + b.Igst, a.Cess + b.Cess, a.Total + b.Total);

    [Fact]
    public void A_partial_return_refunds_its_share_with_taxes_in_proportion()
    {
        // 3 at 52.50 inclusive of 5%: 157.50 paid, taxable 150.00, CGST = SGST = 3.75.
        var original = new BillLineResult(157.50m, 0, 0, 150m, 3.75m, 3.75m, 0, 0, 157.50m);
        var one = ReturnCalculator.Line(new ReturnableLine(3, original, Zero, 0), 1);
        Assert.Equal((52.50m, 1.25m, 1.25m, 50m), (one.Total, one.Cgst, one.Sgst, one.Taxable));
    }

    [Fact]
    public void Returning_too_much_is_refused()
    {
        var original = new BillLineResult(100, 0, 0, 100, 0, 0, 0, 0, 100);
        var error = Assert.Throws<DomainException>(() => ReturnCalculator.Line(new ReturnableLine(2, original, Zero, 1.5m), 1));
        Assert.Equal("return.quantity_too_large", error.Code);
        Assert.Contains("0.5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_sequence_of_partial_returns_adds_up_to_the_original_line_exactly()
    {
        var random = new Random(20261001);
        for (var run = 0; run < 1000; run++)
        {
            var quantity = random.Next(1, 20);
            var bill = InvoiceCalculator.Calculate(new BillInput("GST_REGULAR", random.Next(2) == 0, [
                new BillLineInput(quantity, random.Next(100, 99999) / 100m, random.Next(2) == 0, "TAXABLE", new[] { 5m, 12m, 18m, 28m }[random.Next(4)],
                    random.Next(4) == 0 ? 12 : 0, 0),
            ], 0));
            var original = bill.Lines[0];

            var returned = Zero;
            var returnedQuantity = 0m;
            while (returnedQuantity < quantity)
            {
                var take = Math.Min(random.Next(1, 4), quantity - returnedQuantity);
                var part = ReturnCalculator.Line(new ReturnableLine(quantity, original, returned, returnedQuantity), take);
                Assert.Equal(part.Cgst, part.Sgst);
                Assert.Equal(part.Total, part.Taxable + part.Cgst + part.Sgst + part.Igst + part.Cess);
                Assert.True(part.Taxable >= 0 && part.Total >= 0);
                returned = Sum(returned, part);
                returnedQuantity += take;
            }

            Assert.Equal(original, returned);
        }
    }

    [Fact]
    public void Refunds_must_add_up_to_the_credit_note_and_store_credit_is_counted()
    {
        Assert.Equal(40m, ReturnCalculator.StoreCredit(100m, [new("CASH", 60m, null), new("STORE_CREDIT", 40m, null)]));
        Assert.Equal("refund.mismatch", Assert.Throws<DomainException>(() => ReturnCalculator.StoreCredit(100m, [new("CASH", 90m, null)])).Code);
        Assert.Equal("refund.method_invalid", Assert.Throws<DomainException>(() => ReturnCalculator.StoreCredit(100m, [new("CHEQUE", 100m, null)])).Code);
    }

    [Theory]
    [InlineData("C1", 1, "C1/CN000001")]
    [InlineData("BILL01B", 999_999, "BILL01B/CN999999")]
    public void Credit_note_numbers_fit_the_gst_limit(string prefix, long sequence, string expected)
    {
        var number = ReturnCalculator.CreditNoteNumber(prefix, sequence);
        Assert.Equal(expected, number);
        Assert.True(number.Length <= 16);
    }
}
