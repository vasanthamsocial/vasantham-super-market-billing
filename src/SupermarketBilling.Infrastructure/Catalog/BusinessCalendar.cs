namespace SupermarketBilling.Infrastructure.Catalog;

/// <summary>Business dates (effective dates, invoice dates) are calendar days in the store's time zone, not UTC.</summary>
public static class BusinessCalendar
{
    public const string DefaultTimeZone = "Asia/Kolkata";

    public static DateOnly Today(TimeProvider clock, string timeZone = DefaultTimeZone)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }
}
