using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.UnitTests.Accounts;

public sealed class OfflineRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Collector = Guid.NewGuid();

    private static CollectionDevice Device(decimal limit = 1000m, int hours = 24) =>
        CollectionDevice.Enrol(Guid.NewGuid(), Collector, "Ravi's phone", new byte[32], limit, hours, Guid.NewGuid(), Now);

    private static OfflineSubmission.Item Item(decimal amount, TimeSpan ago, long sequence = 1) =>
        new(Guid.NewGuid(), sequence, Guid.NewGuid(), "CASH", amount, null, null, null, null, Now - ago);

    [Fact]
    public void A_phone_has_a_name_and_sensible_limits()
    {
        Assert.Equal("device.name_required", Assert.Throws<DomainException>(() => CollectionDevice.Enrol(Guid.NewGuid(), Collector, " ", [], 10m, 1, Guid.NewGuid(), Now)).Code);
        Assert.Equal("device.hours_invalid", Assert.Throws<DomainException>(() => Device(hours: 0)).Code);
        Assert.Equal("device.limit_invalid", Assert.Throws<DomainException>(() => Device(limit: -1m)).Code);
        Assert.Equal("device.limit_invalid", Assert.Throws<DomainException>(() => Device(limit: 10.005m)).Code);
    }

    [Theory]
    [InlineData(100, 1, 0, null)]
    [InlineData(100, 25, 0, "24 hours")]
    [InlineData(100, -1, 0, "clock")]
    [InlineData(100, 1, 950, "limit")]
    [InlineData(0, 1, 0, "positive")]
    [InlineData(10.001, 1, 0, "positive")]
    public void An_offline_collection_is_posted_only_within_the_phones_limits(decimal amount, int hoursAgo, decimal heldBefore, string? reason)
    {
        var why = OfflineRules.Check(Device(), Item(amount, TimeSpan.FromHours(hoursAgo)), heldBefore, Now);
        if (reason is null)
        {
            Assert.Null(why);
        }
        else
        {
            Assert.Contains(reason, why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_phones_collections_arrive_one_after_another()
    {
        var device = Device();
        device.Received(1, Now);
        device.Received(2, Now);
        Assert.Equal("offline.sequence_gap", Assert.Throws<DomainException>(() => device.Received(4, Now)).Code);
        Assert.Equal("offline.sequence_gap", Assert.Throws<DomainException>(() => device.Received(2, Now)).Code);
        Assert.Equal(2L, device.LastSequence);
    }

    [Fact]
    public void A_quarantined_collection_is_resolved_once_by_someone_other_than_the_collector()
    {
        var device = Device();
        var submission = OfflineSubmission.Receive(Guid.NewGuid(), device, Item(100m, TimeSpan.FromHours(30)), "hash", null, "Too old", Now);
        Assert.Equal(OfflineStatus.Quarantined, submission.Status);
        Assert.Equal("offline.self_resolve", Assert.Throws<DomainException>(() => submission.Resolve(null, "Mine", Collector, Now)).Code);
        Assert.Equal("offline.note_required", Assert.Throws<DomainException>(() => submission.Resolve(null, " ", Guid.NewGuid(), Now)).Code);
        submission.Resolve(Guid.NewGuid(), "Handed in at the office", Guid.NewGuid(), Now);
        Assert.Equal(OfflineStatus.ResolvedAccepted, submission.Status);
        Assert.Equal("offline.not_quarantined", Assert.Throws<DomainException>(() => submission.Resolve(null, "Again", Guid.NewGuid(), Now)).Code);

        var accepted = OfflineSubmission.Receive(Guid.NewGuid(), device, Item(100m, TimeSpan.FromHours(1), 2), "hash", Guid.NewGuid(), null, Now);
        Assert.Equal(OfflineStatus.Accepted, accepted.Status);
        Assert.Equal("offline.not_quarantined", Assert.Throws<DomainException>(() => accepted.Resolve(null, "No", Guid.NewGuid(), Now)).Code);
    }
}
