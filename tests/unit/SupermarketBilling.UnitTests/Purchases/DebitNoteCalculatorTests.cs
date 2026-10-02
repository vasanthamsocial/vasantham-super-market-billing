using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Purchases;

namespace SupermarketBilling.UnitTests.Purchases;

public sealed class DebitNoteCalculatorTests
{
    private static readonly LineAmounts Received = new(1000m, 90m, 90m, 0m, 0m);

    [Fact]
    public void A_part_return_takes_its_share_of_value_and_each_tax()
    {
        var part = DebitNoteCalculator.Line(Received, 10, LineAmounts.Zero, 0, 3);
        Assert.Equal(new LineAmounts(300m, 27m, 27m, 0m, 0m), part);
        Assert.Equal(354m, part.Total);
    }

    [Fact]
    public void Free_goods_share_the_value_and_the_last_return_takes_exactly_what_is_left()
    {
        // 10 paid + 2 free for Rs. 100: each piece credits 8.33.
        var received = new LineAmounts(100m, 0, 0, 0, 0);
        var first = DebitNoteCalculator.Line(received, 12, LineAmounts.Zero, 0, 1);
        var second = DebitNoteCalculator.Line(received, 12, first, 1, 1);
        var rest = DebitNoteCalculator.Line(received, 12, first.Plus(second), 2, 10);
        Assert.Equal((8.33m, 8.33m, 83.34m), (first.Taxable, second.Taxable, rest.Taxable));
        Assert.Equal(received, first.Plus(second).Plus(rest));
    }

    [Fact]
    public void Returns_never_exceed_what_was_received()
    {
        var random = new Random(8);
        for (var run = 0; run < 500; run++)
        {
            var received = new LineAmounts(random.Next(1, 100000) / 100m, random.Next(0, 9000) / 100m, 0, random.Next(0, 9000) / 100m, random.Next(0, 500) / 100m);
            var quantity = random.Next(1, 50);
            var returned = LineAmounts.Zero;
            var done = 0;
            while (done < quantity)
            {
                var take = random.Next(1, quantity - done + 1);
                returned = returned.Plus(DebitNoteCalculator.Line(received, quantity, returned, done, take));
                done += take;
                Assert.True(returned.Taxable <= received.Taxable && returned.Cgst <= received.Cgst && returned.Igst <= received.Igst && returned.Cess <= received.Cess);
            }

            Assert.Equal(received, returned);
        }
    }

    [Fact]
    public void More_than_is_left_is_refused()
    {
        Assert.Equal("purchase_return.quantity_too_large", Assert.Throws<DomainException>(() => DebitNoteCalculator.Line(Received, 10, Received, 10, 1)).Code);
        Assert.Equal("purchase_return.quantity_invalid", Assert.Throws<DomainException>(() => DebitNoteCalculator.Line(Received, 10, LineAmounts.Zero, 0, 0)).Code);
        Assert.Equal("purchase_return.quantity_invalid", Assert.Throws<DomainException>(() => DebitNoteCalculator.Line(Received, 10, LineAmounts.Zero, 0, 0.0001m)).Code);
    }
}
