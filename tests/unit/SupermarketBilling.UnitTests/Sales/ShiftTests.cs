using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.UnitTests.Sales;

public sealed class ShiftTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 3, 30, 0, TimeSpan.Zero);
    private static readonly Guid Cashier = Guid.NewGuid();

    private static Shift NewShift() => Shift.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Cashier, new DateOnly(2026, 10, 2), 2000m, Now);

    [Fact]
    public void Drawer_counts_add_up_by_denomination()
    {
        Assert.Equal(2_731m, Denominations.Total(new Dictionary<decimal, int> { [500] = 4, [200] = 3, [100] = 1, [20] = 1, [10] = 1, [1] = 1 }));
        Assert.Equal("count.denomination_invalid", Assert.Throws<DomainException>(() => Denominations.Total(new Dictionary<decimal, int> { [25] = 1 })).Code);
        Assert.Equal("count.number_invalid", Assert.Throws<DomainException>(() => Denominations.Total(new Dictionary<decimal, int> { [10] = -1 })).Code);
    }

    [Fact]
    public void Expected_cash_is_float_plus_net_cash_less_refunds_payouts_and_drops() =>
        // 2000 float; 5000 tendered with 300 change; 200 refunded; 500 in; 150 out; 3000 to the safe.
        Assert.Equal(3_850m, ShiftCash.Expected(2000m, 5000m, 300m, 200m, 500m, 150m, 3000m));

    [Fact]
    public void A_matching_count_closes_without_a_note_and_needs_no_review()
    {
        var shift = NewShift();
        shift.Close(3850m, 3850m, null, Cashier, Now.AddHours(8));
        Assert.Equal((ShiftStatus.Closed, 0m, false), (shift.Status, shift.Difference!.Value, shift.NeedsReview));
        Assert.Equal("shift.closed", Assert.Throws<DomainException>(() => shift.Close(1, 1, null, Cashier, Now)).Code);
    }

    [Fact]
    public void A_difference_needs_an_explanation_and_a_review_by_someone_else()
    {
        var shift = NewShift();
        Assert.Equal("shift.note_required", Assert.Throws<DomainException>(() => shift.Close(3850m, 3800m, null, Cashier, Now)).Code);
        shift.Close(3850m, 3800m, "Gave Rs. 50 extra change by mistake", Cashier, Now);
        Assert.Equal((-50m, true), (shift.Difference!.Value, shift.NeedsReview));

        Assert.Equal("shift.review_self", Assert.Throws<DomainException>(() => shift.Review(Cashier, "Accepted", Now)).Code);
        shift.Review(Guid.NewGuid(), "Checked CCTV, accepted", Now);
        Assert.False(shift.NeedsReview);
        Assert.Equal("shift.review_not_needed", Assert.Throws<DomainException>(() => shift.Review(Guid.NewGuid(), "Again", Now)).Code);
    }

    [Theory]
    [InlineData("PAY_OUT", 0, "cash_movement.amount_invalid")]
    [InlineData("PAY_OUT", 10.005, "cash_movement.amount_invalid")]
    [InlineData("STEAL", 10, "cash_movement.kind_invalid")]
    public void Bad_cash_movements_are_refused(string kind, double amount, string code) =>
        Assert.Equal(code, Assert.Throws<DomainException>(() =>
            CashMovement.Record(Guid.NewGuid(), Guid.NewGuid(), kind, (decimal)amount, "Tea for staff", Cashier, null, Now)).Code);
}
