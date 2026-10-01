using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.UnitTests.Common;

public sealed class SequentialGuidTests
{
    [Fact]
    public void Ids_from_the_same_millisecond_sort_in_creation_order_and_are_version_7()
    {
        var now = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var ids = Enumerable.Range(0, 10_000).Select(_ => SequentialGuid.Next(now)).ToList();

        Assert.Equal(ids, ids.Order().ToList()); // Guid.CompareTo is what EF Core uses to order inserts
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(7, id.Version));
        Assert.All(ids, id => Assert.Equal(0x80, id.ToByteArray(bigEndian: true)[8] & 0xC0));
    }

    [Fact]
    public void A_clock_going_backwards_does_not_break_the_order()
    {
        var first = SequentialGuid.Next(new DateTimeOffset(2026, 10, 1, 10, 0, 1, TimeSpan.Zero));
        var second = SequentialGuid.Next(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
        Assert.True(second.CompareTo(first) > 0);
    }
}
