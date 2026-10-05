using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Dispatch;

namespace SupermarketBilling.UnitTests.Dispatch;

public sealed class PackingDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly Guid Picker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    private static PackingChallan Challan() =>
        PackingChallan.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "MAIN/PCH/000001", "Lakshmi Stores",
            [new PackingChallanLine.Item(Guid.NewGuid(), 1, "Ponni Rice", "Ponni Rice 25 kg", "BAG", 10m), new PackingChallanLine.Item(Guid.NewGuid(), 2, "Oil", "Oil", "TIN", 4m)], Now);

    private static Dictionary<Guid, (decimal, string?)> All(PackingChallan c, decimal? first = null, string? reason = null) =>
        c.Lines.ToDictionary(l => l.Id, l => (l.LineNumber == 1 && first is { } q ? q : l.PickedQuantity ?? l.Quantity, l.LineNumber == 1 ? reason : null));

    [Fact]
    public void A_challan_copies_the_bill_and_a_variant_named_like_its_item_is_not_repeated()
    {
        var challan = Challan();
        Assert.Equal(("Ponni Rice 25 kg", (string?)null), (challan.Lines[0].VariantName, challan.Lines[1].VariantName));
        Assert.Equal("challan.no_lines", Assert.Throws<DomainException>(() => PackingChallan.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "X", "P", [], Now)).Code);
    }

    [Fact]
    public void Picking_short_needs_a_reason_checking_needs_a_second_person_and_packing_follows_checking()
    {
        var challan = Challan();
        Assert.Equal("challan.not_picked", Assert.Throws<DomainException>(() => challan.Check(All(challan), Checker, Now)).Code);
        Assert.Equal("challan.lines_incomplete", Assert.Throws<DomainException>(() =>
            challan.Pick(new Dictionary<Guid, (decimal, string?)> { [challan.Lines[0].Id] = (10m, null) }, Picker, Now)).Code);
        Assert.Equal("challan.short_reason_required", Assert.Throws<DomainException>(() => challan.Pick(All(challan, 9m), Picker, Now)).Code);
        Assert.Equal("challan.quantity_invalid", Assert.Throws<DomainException>(() => challan.Pick(All(challan, 11m), Picker, Now)).Code);
        challan.Pick(All(challan, 9m, "Torn bag"), Picker, Now);
        Assert.Equal("challan.already_picked", Assert.Throws<DomainException>(() => challan.Pick(All(challan), Picker, Now)).Code);

        Assert.Equal("challan.not_checked", Assert.Throws<DomainException>(() => challan.Pack(new Dictionary<Guid, decimal> { [challan.Lines[0].Id] = 1m }, 1, Picker)).Code);
        Assert.Equal("challan.self_check", Assert.Throws<DomainException>(() => challan.Check(All(challan), Picker, Now)).Code);
        Assert.Equal("challan.short_reason_required", Assert.Throws<DomainException>(() => challan.Check(All(challan, 8m), Checker, Now)).Code);
        challan.Check(All(challan, 8m, "One more torn"), Checker, Now);
        Assert.Equal("Torn bag; One more torn", challan.Lines[0].ShortReason);

        var rice = challan.Lines[0].Id;
        Assert.Equal("challan.pack_too_much", Assert.Throws<DomainException>(() => challan.Pack(new Dictionary<Guid, decimal> { [rice] = 9m }, 1, Picker)).Code);
        Assert.Equal("challan.packages_invalid", Assert.Throws<DomainException>(() => challan.Pack(new Dictionary<Guid, decimal> { [rice] = 1m }, 0, Picker)).Code);
        Assert.Equal("challan.nothing_packed", Assert.Throws<DomainException>(() => challan.Pack(new Dictionary<Guid, decimal> { [rice] = 0m }, 1, Picker)).Code);
        challan.Pack(new Dictionary<Guid, decimal> { [rice] = 5m }, 2, Picker);
        challan.Pack(new Dictionary<Guid, decimal> { [rice] = 3m }, 1, Checker);
        Assert.Equal((8m, 3, Picker), (challan.Lines[0].PackedQuantity, challan.PackageCount, challan.PackedByUserId));
        Assert.Equal("challan.pack_too_much", Assert.Throws<DomainException>(() => challan.Pack(new Dictionary<Guid, decimal> { [rice] = 1m }, 1, Picker)).Code);

        challan.Cancel("Changed to pickup");
        Assert.Equal("challan.cancelled", Assert.Throws<DomainException>(() => challan.Pack(new Dictionary<Guid, decimal> { [challan.Lines[1].Id] = 1m }, 1, Picker)).Code);
    }

    [Fact]
    public void A_refused_count_changes_nothing()
    {
        var challan = Challan();
        var counts = challan.Lines.ToDictionary(l => l.Id, l => (l.LineNumber == 2 ? 5m : l.Quantity, (string?)null)); // line 2: more than billed
        Assert.Throws<DomainException>(() => challan.Pick(counts, Picker, Now));
        Assert.All(challan.Lines, l => Assert.Null(l.PickedQuantity));
        Assert.Null(challan.PickedByUserId);

        var trip = Trip(Guid.NewGuid(), 4m);
        Assert.Throws<DomainException>(() => trip.ReportDelivery(new Dictionary<Guid, decimal> { [trip.Lines[0].ChallanLineId] = 3m }, Today, null, Guid.NewGuid(), Now));
        Assert.Equal(((string?)null, (decimal?)null), (trip.DeliveryOutcome, trip.Lines[0].DeliveredQuantity));
    }

    [Theory]
    [InlineData("OPEN", null, null, 0, 0, 0, "TO_PICK")]
    [InlineData("OPEN", 10, null, 0, 0, 0, "TO_CHECK")]
    [InlineData("OPEN", 10, 10, 0, 0, 0, "TO_PACK")]
    [InlineData("OPEN", 10, 10, 4, 0, 0, "PARTLY_PACKED")]
    [InlineData("OPEN", 10, 10, 10, 0, 0, "PACKED")]
    [InlineData("OPEN", 10, 10, 10, 4, 0, "PARTLY_DISPATCHED")]
    [InlineData("OPEN", 10, 10, 10, 6, 4, "DISPATCHED")]
    [InlineData("OPEN", 10, 9, 9, 0, 9, "DELIVERED")]
    [InlineData("OPEN", 10, 0, 0, 0, 0, "NOTHING_TO_SEND")]
    [InlineData("CANCELLED", 10, 10, 10, 0, 0, "CANCELLED")]
    public void Where_the_goods_are_is_worked_out_from_the_quantities(string status, int? picked, int? checkedQuantity, int packed, int outstanding, int delivered,
        string expected) =>
        Assert.Equal(expected, ChallanProgress.Of(status, [new ChallanProgress.LineState(10m, picked, checkedQuantity, packed, outstanding, delivered)]));

    private static Consignment Trip(Guid line, decimal quantity) =>
        Consignment.Record(Guid.NewGuid(), Guid.NewGuid(), "MAIN/DSP/000001",
            new Consignment.Details(FulfilmentModes.LocalDelivery, null, "Ravi", null, null, null, 1, null, null, 0m, Today, null, null),
            new Consignment.Party("Lakshmi Stores", "4 Big Bazaar Street"), null, 600m, [Guid.NewGuid()], ("key-12345678", "hash"), Guid.NewGuid(), Now,
            new Dictionary<Guid, decimal> { [line] = quantity });

    [Fact]
    public void A_delivery_is_reported_once_with_a_reason_for_anything_short_and_only_undelivered_goods_come_back()
    {
        var line = Guid.NewGuid();
        Assert.Equal("consignment.nothing_packed", Assert.Throws<DomainException>(() => Consignment.Record(Guid.NewGuid(), Guid.NewGuid(), "X",
            new Consignment.Details(FulfilmentModes.LocalDelivery, null, "Ravi", null, null, null, 1, null, null, 0m, Today, null, null),
            new Consignment.Party("P", "A"), null, 0m, [Guid.NewGuid()], ("key-12345678", "h"), Guid.NewGuid(), Now, new Dictionary<Guid, decimal>())).Code);

        var trip = Trip(line, 10m);
        Assert.Equal("return.not_undelivered", Assert.Throws<DomainException>(() => trip.RecordReturn(new Dictionary<Guid, decimal> { [line] = 1m }, Guid.NewGuid(), Now)).Code);
        Assert.Equal("delivery.date_invalid", Assert.Throws<DomainException>(() =>
            trip.ReportDelivery(new Dictionary<Guid, decimal> { [line] = 10m }, Today.AddDays(-1), null, Guid.NewGuid(), Now)).Code);
        Assert.Equal("delivery.quantity_invalid", Assert.Throws<DomainException>(() =>
            trip.ReportDelivery(new Dictionary<Guid, decimal> { [line] = 11m }, Today, null, Guid.NewGuid(), Now)).Code);
        Assert.Equal("delivery.reason_required", Assert.Throws<DomainException>(() =>
            trip.ReportDelivery(new Dictionary<Guid, decimal> { [line] = 7m }, Today, null, Guid.NewGuid(), Now)).Code);
        trip.ReportDelivery(new Dictionary<Guid, decimal> { [line] = 7m }, Today, "3 bags wet, refused", Guid.NewGuid(), Now);
        Assert.Equal(DeliveryOutcomes.PartlyDelivered, trip.DeliveryOutcome);
        Assert.Equal("delivery.already_reported", Assert.Throws<DomainException>(() =>
            trip.ReportDelivery(new Dictionary<Guid, decimal> { [line] = 10m }, Today, null, Guid.NewGuid(), Now)).Code);
        Assert.Equal("consignment.reported", Assert.Throws<DomainException>(() => trip.Cancel("x", Guid.NewGuid(), Now)).Code);

        Assert.Equal("return.quantity_invalid", Assert.Throws<DomainException>(() => trip.RecordReturn(new Dictionary<Guid, decimal> { [line] = 4m }, Guid.NewGuid(), Now)).Code);
        trip.RecordReturn(new Dictionary<Guid, decimal> { [line] = 2m }, Guid.NewGuid(), Now);
        Assert.Equal((7m, 2m, 1m), (trip.Lines[0].DeliveredQuantity, trip.Lines[0].ReturnedQuantity, trip.Lines[0].Outstanding)); // one bag lost
        Assert.Equal("return.already_recorded", Assert.Throws<DomainException>(() => trip.RecordReturn(new Dictionary<Guid, decimal> { [line] = 1m }, Guid.NewGuid(), Now)).Code);

        var failed = Trip(line, 4m);
        failed.ReportDelivery(new Dictionary<Guid, decimal> { [line] = 0m }, Today, "Shop closed", Guid.NewGuid(), Now);
        Assert.Equal(DeliveryOutcomes.Failed, failed.DeliveryOutcome);
        var delivered = Trip(line, 4m);
        delivered.ReportDelivery(new Dictionary<Guid, decimal> { [line] = 4m }, Today, null, Guid.NewGuid(), Now);
        Assert.Equal(DeliveryOutcomes.Delivered, delivered.DeliveryOutcome);
    }
}
