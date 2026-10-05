using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Dispatch;

namespace SupermarketBilling.UnitTests.Dispatch;

public sealed class DispatchDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 4);
    private static readonly Guid Business = Guid.NewGuid();

    private static (Transporter Transporter, TransporterBranch Booking, TransporterBranch Destination) Lorry()
    {
        var transporter = Transporter.Create(Business, " kpn-1 ", "KPN Parcel Service", null, "0422 2345678", null, Now);
        return (transporter, TransporterBranch.Create(Business, transporter.Id, "Gandhipuram", "Coimbatore", null, null, true, false, Now),
            TransporterBranch.Create(Business, transporter.Id, "Madurai branch", "Madurai", null, null, false, true, Now));
    }

    private static Consignment.Details Details(string mode = FulfilmentModes.Lorry, string? lr = "lr-1001", string? vehicle = null, string? driver = null) =>
        new(mode, vehicle, driver, null, lr, Today, 2, 12.5m, FreightTerms.ToPay, 250m, Today, Today.AddDays(2), null);

    private static Consignment Record(Consignment.Details details, Consignment.Lorry? lorry, decimal value = 1000m) =>
        Consignment.Record(Business, Guid.NewGuid(), "MAIN/DSP/000001", details, new Consignment.Party("Meena Stores", "12 East Masi Street"), lorry, value, [Guid.NewGuid()],
            ("key-12345678", "hash"), Guid.NewGuid(), Now);

    [Fact]
    public void A_lorry_service_has_a_code_a_valid_gstin_if_any_and_offices_that_book_or_receive()
    {
        var (transporter, booking, destination) = Lorry();
        Assert.Equal("KPN-1", transporter.Code);
        Assert.Equal("transporter.code_invalid", Assert.Throws<DomainException>(() => Transporter.Create(Business, "KPN 1", "x", null, null, null, Now)).Code);
        Assert.Equal("gstin.invalid", Assert.Throws<DomainException>(() => transporter.Update("KPN", "33AAACK1234C1Z9", null, null, true)).Code);
        transporter.Update("KPN", " " + Domain.Tax.Gstin.Complete("33AAACK1234C1Z").ToLowerInvariant(), null, null, true);
        Assert.Equal(Domain.Tax.Gstin.Complete("33AAACK1234C1Z"), transporter.Gstin);
        Assert.Equal("transporter.phone_invalid", Assert.Throws<DomainException>(() => transporter.Update("KPN", null, "call me", null, true)).Code);
        Assert.Equal("branch.role_required", Assert.Throws<DomainException>(() => booking.Update("x", "y", null, null, false, false, true)).Code);

        var route = TransporterRoute.Create(Business, booking, destination, 2, Now);
        Assert.Equal((booking.Id, destination.Id, 2), (route.FromBranchId, route.ToBranchId, route.TransitDays));
        Assert.Equal("route.branches_invalid", Assert.Throws<DomainException>(() => TransporterRoute.Create(Business, destination, booking, 2, Now)).Code);
        Assert.Equal("route.transit_invalid", Assert.Throws<DomainException>(() => route.Update(61, true)).Code);
        var stranger = TransporterBranch.Create(Business, Guid.NewGuid(), "Other", "Salem", null, null, false, true, Now);
        Assert.Equal("route.transporter_mismatch", Assert.Throws<DomainException>(() => TransporterRoute.Create(Business, booking, stranger, 1, Now)).Code);
    }

    [Fact]
    public void A_bill_delivered_by_anyone_but_the_customer_needs_an_address_and_by_lorry_a_lorry_service()
    {
        var choice = new InvoiceFulfilment.Choice(FulfilmentModes.Lorry, "12 East Masi Street", null, Guid.NewGuid(), Guid.NewGuid(), null);
        var fulfilment = InvoiceFulfilment.Choose(Business, Guid.NewGuid(), Guid.NewGuid(), choice, Guid.NewGuid(), Now);
        Assert.Equal(FulfilmentModes.Lorry, fulfilment.Mode);
        Assert.Equal("fulfilment.address_required", Assert.Throws<DomainException>(() => fulfilment.Change(choice with { DeliveryAddress = " " }, Guid.NewGuid(), Now)).Code);
        Assert.Equal("fulfilment.transporter_required", Assert.Throws<DomainException>(() => fulfilment.Change(choice with { TransporterId = null }, Guid.NewGuid(), Now)).Code);
        Assert.Equal("fulfilment.mode_invalid", Assert.Throws<DomainException>(() => fulfilment.Change(choice with { Mode = "COURIER" }, Guid.NewGuid(), Now)).Code);

        // Other ways drop the lorry details; pickup needs nothing.
        fulfilment.Change(choice with { Mode = FulfilmentModes.LocalDelivery }, Guid.NewGuid(), Now);
        Assert.Equal((FulfilmentModes.LocalDelivery, (Guid?)null, (Guid?)null), (fulfilment.Mode, fulfilment.TransporterId, fulfilment.DestinationBranchId));
        fulfilment.Change(new InvoiceFulfilment.Choice(FulfilmentModes.Pickup, null, null, null, null, null), Guid.NewGuid(), Now);
        Assert.False(FulfilmentModes.IsDispatched(fulfilment.Mode));
    }

    [Fact]
    public void A_lorry_dispatch_records_the_booking_and_snapshots_the_names()
    {
        var (transporter, booking, destination) = Lorry();
        var consignment = Record(Details(), new Consignment.Lorry(transporter, booking, destination));
        Assert.Equal(("LR-1001", "KPN Parcel Service", "Gandhipuram, Coimbatore", "Madurai branch, Madurai", ConsignmentStatus.Dispatched),
            (consignment.LrNumber, consignment.TransporterName, consignment.BookingOffice, consignment.DestinationBranch, consignment.Status));

        Assert.Equal("consignment.lr_invalid", Assert.Throws<DomainException>(() => Record(Details(lr: "LR 1001"), new(transporter, booking, destination))).Code);
        Assert.Equal("consignment.lr_date_invalid", Assert.Throws<DomainException>(() =>
            Record(Details() with { LrDate = Today.AddDays(1) }, new(transporter, booking, destination))).Code);
        Assert.Equal("consignment.freight_terms_invalid", Assert.Throws<DomainException>(() =>
            Record(Details() with { FreightTerms = "COD" }, new(transporter, booking, destination))).Code);
        Assert.Equal("consignment.freight_invalid", Assert.Throws<DomainException>(() =>
            Record(Details() with { FreightAmount = 10.005m }, new(transporter, booking, destination))).Code);
        Assert.Equal("consignment.branches_invalid", Assert.Throws<DomainException>(() => Record(Details(), new(transporter, destination, booking))).Code);
        Assert.Equal("consignment.transporter_required", Assert.Throws<DomainException>(() => Record(Details(), null)).Code);
        Assert.Equal("consignment.expected_invalid", Assert.Throws<DomainException>(() =>
            Record(Details() with { ExpectedDeliveryDate = Today.AddDays(-1) }, new(transporter, booking, destination))).Code);
        Assert.Equal("consignment.packages_invalid", Assert.Throws<DomainException>(() =>
            Record(Details() with { PackageCount = 0 }, new(transporter, booking, destination))).Code);
        Assert.Equal("consignment.pickup", Assert.Throws<DomainException>(() => Record(Details(FulfilmentModes.Pickup), null)).Code);
    }

    [Fact]
    public void Own_vehicle_needs_a_vehicle_local_delivery_a_person_and_neither_carries_freight()
    {
        var trip = Record(Details(FulfilmentModes.OwnVehicle, lr: null, vehicle: "tn-38 ab 1234"), null);
        Assert.Equal(("TN38AB1234", (string?)null, (string?)null, 0m), (trip.VehicleNumber, trip.LrNumber, trip.FreightTerms, trip.FreightAmount));
        Assert.Equal("consignment.vehicle_required", Assert.Throws<DomainException>(() => Record(Details(FulfilmentModes.OwnVehicle, lr: null), null)).Code);
        Assert.Equal("consignment.vehicle_invalid", Assert.Throws<DomainException>(() => Record(Details(FulfilmentModes.OwnVehicle, lr: null, vehicle: "T1"), null)).Code);
        Assert.Equal("consignment.driver_required", Assert.Throws<DomainException>(() => Record(Details(FulfilmentModes.LocalDelivery, lr: null), null)).Code);
        Assert.Equal("Ravi", Record(Details(FulfilmentModes.LocalDelivery, lr: null, driver: " Ravi "), null).DriverName);
    }

    [Theory]
    [InlineData(49_999.99, null, false)]
    [InlineData(50_000, null, true)]
    [InlineData(80_000, "123456789012", false)]
    public void An_eway_bill_is_flagged_when_goods_worth_fifty_thousand_go_without_one(decimal value, string? eway, bool missing)
    {
        var trip = Record(Details(FulfilmentModes.OwnVehicle, lr: null, vehicle: "TN38AB1234") with { EwayBillNumber = eway }, null, value);
        Assert.Equal(missing, trip.EwayBillMissing);
        Assert.Equal("consignment.eway_invalid", Assert.Throws<DomainException>(() =>
            Record(Details(FulfilmentModes.OwnVehicle, lr: null, vehicle: "TN38AB1234") with { EwayBillNumber = "12345" }, null)).Code);
    }

    [Fact]
    public void A_dispatch_is_cancelled_once_with_a_reason()
    {
        var trip = Record(Details(FulfilmentModes.OwnVehicle, lr: null, vehicle: "TN38AB1234"), null);
        Assert.Equal("consignment.reason_required", Assert.Throws<DomainException>(() => trip.Cancel(" ", Guid.NewGuid(), Now)).Code);
        trip.Cancel("Wrong vehicle", Guid.NewGuid(), Now);
        Assert.Equal((ConsignmentStatus.Cancelled, "Wrong vehicle"), (trip.Status, trip.CancelReason));
        Assert.Equal("consignment.not_active", Assert.Throws<DomainException>(() => trip.Cancel("Again", Guid.NewGuid(), Now)).Code);
    }
}
