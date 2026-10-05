using SupermarketBilling.Infrastructure.Catalog;

namespace SupermarketBilling.UnitTests.Reporting;

public sealed class BusinessCalendarTests
{
    [Fact]
    public void A_business_day_in_india_starts_at_half_past_six_the_evening_before_in_utc()
    {
        var start = BusinessCalendar.StartOf(new DateOnly(2026, 10, 1));
        Assert.Equal((TimeSpan.Zero, new DateTime(2026, 9, 30, 18, 30, 0)), (start.Offset, start.DateTime));
    }
}
