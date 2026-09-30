using SupermarketBilling.Domain.Auditing;

namespace SupermarketBilling.UnitTests.Auditing;

public sealed class AuditEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_populates_fields_and_defaults_payload_to_empty_object()
    {
        var businessId = Guid.NewGuid();

        var auditEvent = AuditEvent.Create(Now, "store.created", "store", "S001", businessId: businessId);

        Assert.Equal("store.created", auditEvent.EventType);
        Assert.Equal("store", auditEvent.EntityType);
        Assert.Equal("S001", auditEvent.EntityId);
        Assert.Equal(businessId, auditEvent.BusinessId);
        Assert.Equal(Now, auditEvent.OccurredAtUtc);
        Assert.Equal("{}", auditEvent.PayloadJson);
    }

    [Fact]
    public void Create_uses_time_ordered_version_7_identifiers()
    {
        var first = AuditEvent.Create(Now, "a");
        var second = AuditEvent.Create(Now.AddSeconds(1), "b");

        Assert.Equal(7, first.Id.Version);
        Assert.True(first.Id.CompareTo(second.Id) < 0, "Later events must sort after earlier ones.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_event_type(string eventType)
    {
        Assert.Throws<ArgumentException>(() => AuditEvent.Create(Now, eventType));
    }

    [Fact]
    public void Create_rejects_overlong_event_type()
    {
        var eventType = new string('x', AuditEvent.MaxEventTypeLength + 1);

        Assert.Throws<ArgumentException>(() => AuditEvent.Create(Now, eventType));
    }

    [Fact]
    public void Create_rejects_non_utc_timestamps()
    {
        var indiaTime = new DateTimeOffset(2026, 9, 29, 16, 0, 0, TimeSpan.FromHours(5.5));

        Assert.Throws<ArgumentException>(() => AuditEvent.Create(indiaTime, "shift.opened"));
    }
}
