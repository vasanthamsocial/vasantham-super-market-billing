using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Dispatch;

/// <summary>How the goods on a bill reach the customer (spec section 19).</summary>
public static class FulfilmentModes
{
    public const string Pickup = "PICKUP";
    public const string OwnVehicle = "OWN_VEHICLE";
    public const string Lorry = "LORRY";
    public const string LocalDelivery = "LOCAL_DELIVERY";

    public static readonly IReadOnlyList<string> All = [Pickup, OwnVehicle, Lorry, LocalDelivery];

    /// <summary>Goods that leave the store with the business (not carried away by the customer) are dispatched.</summary>
    public static bool IsDispatched(string mode) => mode != Pickup;

    public static string Require(string? mode) =>
        All.Contains(mode ?? string.Empty) ? mode! : throw new DomainException("fulfilment.mode_invalid", "Choose pickup, own vehicle, lorry service or local delivery.");
}

/// <summary>Who pays the lorry: the business at booking (paid) or the customer at the destination (to pay).</summary>
public static class FreightTerms
{
    public const string Paid = "PAID";
    public const string ToPay = "TO_PAY";

    public static readonly IReadOnlyList<string> All = [Paid, ToPay];
}

internal static partial class DispatchText
{
    public static string? Optional(string? value, int max, string code, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var v && v.Length <= max ? v : throw new DomainException(code, $"{what} is at most {max} characters.");

    public static string Required(string? value, int max, string code, string what) =>
        Optional(value, max, code, what) ?? throw new DomainException(code, $"Give the {what.ToLowerInvariant()} (max {max} characters).");

    public static string Code(string? value, string code, string what) =>
        (value ?? string.Empty).Trim().ToUpperInvariant() is var c && CodePattern().IsMatch(c)
            ? c
            : throw new DomainException(code, $"A {what} code is 1-20 letters, digits or hyphens.");

    public static string? Phone(string? value, string code)
    {
        var phone = Optional(value, 20, code, "A phone number");
        return phone is null || PhonePattern().IsMatch(phone) ? phone : throw new DomainException(code, "A phone number has only digits, spaces, +, - and brackets.");
    }

    [GeneratedRegex("^[A-Z0-9-]{1,20}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[0-9 +\-()]{6,20}$")]
    private static partial Regex PhonePattern();
}

/// <summary>A lorry service (transporter) the business books goods with: the master of spec section 19.</summary>
public sealed class Transporter : ITenantOwned
{
    private Transporter()
    {
        Code = Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    /// <summary>Registered transporters have a GSTIN (needed on e-way bills); many small ones do not.</summary>
    public string? Gstin { get; private set; }

    public string? Phone { get; private set; }

    public string? Address { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static Transporter Create(Guid businessId, string code, string name, string? gstin, string? phone, string? address, DateTimeOffset now)
    {
        var transporter = new Transporter
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Code = DispatchText.Code(code, "transporter.code_invalid", "transporter"),
            CreatedAtUtc = now,
        };
        transporter.Update(name, gstin, phone, address, true);
        return transporter;
    }

    public void Update(string name, string? gstin, string? phone, string? address, bool isActive)
    {
        Name = DispatchText.Required(name, 100, "transporter.name_required", "Name");
        var normalized = string.IsNullOrWhiteSpace(gstin) ? null : Tax.Gstin.Normalize(gstin);
        Gstin = normalized is null || Tax.Gstin.IsValid(normalized)
            ? normalized
            : throw new DomainException("gstin.invalid", $"'{normalized}' is not a valid GSTIN (format or check digit is wrong).");
        Phone = DispatchText.Phone(phone, "transporter.phone_invalid");
        Address = DispatchText.Optional(address, 300, "transporter.address_invalid", "The address");
        IsActive = isActive;
    }
}

/// <summary>
/// An office of a transporter: where goods are booked (near the store) and/or a destination branch where the customer
/// collects them.
/// </summary>
public sealed class TransporterBranch : ITenantOwned
{
    private TransporterBranch()
    {
        Name = City = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid TransporterId { get; private set; }

    public string Name { get; private set; }

    public string City { get; private set; }

    public string? Address { get; private set; }

    public string? Phone { get; private set; }

    public bool IsBookingOffice { get; private set; }

    public bool IsDestination { get; private set; }

    public bool IsActive { get; private set; }

    public uint RowVersion { get; private set; }

    public static TransporterBranch Create(Guid businessId, Guid transporterId, string name, string city, string? address, string? phone, bool booking, bool destination,
        DateTimeOffset now)
    {
        var branch = new TransporterBranch { Id = Guid.CreateVersion7(now), BusinessId = businessId, TransporterId = transporterId };
        branch.Update(name, city, address, phone, booking, destination, true);
        return branch;
    }

    public void Update(string name, string city, string? address, string? phone, bool booking, bool destination, bool isActive)
    {
        Name = DispatchText.Required(name, 100, "branch.name_required", "Branch name");
        City = DispatchText.Required(city, 60, "branch.city_required", "City");
        Address = DispatchText.Optional(address, 300, "branch.address_invalid", "The address");
        Phone = DispatchText.Phone(phone, "branch.phone_invalid");
        if (!booking && !destination)
        {
            throw new DomainException("branch.role_required", "A branch is a booking office, a destination, or both.");
        }

        (IsBookingOffice, IsDestination, IsActive) = (booking, destination, isActive);
    }
}

/// <summary>A route a transporter runs: from a booking office to a destination branch, with its usual transit time.</summary>
public sealed class TransporterRoute : ITenantOwned
{
    private TransporterRoute()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid TransporterId { get; private set; }

    public Guid FromBranchId { get; private set; }

    public Guid ToBranchId { get; private set; }

    public int TransitDays { get; private set; }

    public bool IsActive { get; private set; }

    public uint RowVersion { get; private set; }

    public static TransporterRoute Create(Guid businessId, TransporterBranch from, TransporterBranch to, int transitDays, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.TransporterId != to.TransporterId)
        {
            throw new DomainException("route.transporter_mismatch", "Both branches of a route belong to the same transporter.");
        }

        if (from.Id == to.Id || !from.IsBookingOffice || !to.IsDestination)
        {
            throw new DomainException("route.branches_invalid", "A route runs from a booking office to a different, destination branch.");
        }

        var route = new TransporterRoute { Id = Guid.CreateVersion7(now), BusinessId = businessId, TransporterId = from.TransporterId, FromBranchId = from.Id, ToBranchId = to.Id };
        route.Update(transitDays, true);
        return route;
    }

    public void Update(int transitDays, bool isActive)
    {
        TransitDays = transitDays is >= 0 and <= 60 ? transitDays : throw new DomainException("route.transit_invalid", "Transit time is 0 to 60 days.");
        IsActive = isActive;
    }
}

/// <summary>
/// How a bill's goods reach the customer, chosen at the counter (or later by dispatch staff) and kept apart from the
/// invoice, which never changes. Pickup is the default and has no row. It can be changed until the goods are dispatched.
/// </summary>
public sealed class InvoiceFulfilment : ITenantOwned
{
    private InvoiceFulfilment()
    {
        Mode = string.Empty;
    }

    public Guid InvoiceId { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public string Mode { get; private set; }

    public string? DeliveryAddress { get; private set; }

    public string? ContactPhone { get; private set; }

    public Guid? TransporterId { get; private set; }

    public Guid? DestinationBranchId { get; private set; }

    public string? Note { get; private set; }

    public Guid ChosenByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static InvoiceFulfilment Choose(Guid businessId, Guid storeId, Guid invoiceId, Choice choice, Guid userId, DateTimeOffset now)
    {
        var fulfilment = new InvoiceFulfilment { InvoiceId = invoiceId, BusinessId = businessId, StoreId = storeId, CreatedAtUtc = now };
        fulfilment.Change(choice, userId, now);
        return fulfilment;
    }

    public void Change(Choice choice, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Mode = FulfilmentModes.Require(choice.Mode);
        DeliveryAddress = DispatchText.Optional(choice.DeliveryAddress, 500, "fulfilment.address_invalid", "The delivery address");
        if (FulfilmentModes.IsDispatched(Mode) && DeliveryAddress is null)
        {
            throw new DomainException("fulfilment.address_required", "Give the delivery address.");
        }

        ContactPhone = DispatchText.Phone(choice.ContactPhone, "fulfilment.phone_invalid");
        if (Mode == FulfilmentModes.Lorry && choice.TransporterId is null)
        {
            throw new DomainException("fulfilment.transporter_required", "Choose the lorry service.");
        }

        (TransporterId, DestinationBranchId) = Mode == FulfilmentModes.Lorry ? (choice.TransporterId, choice.DestinationBranchId) : (null, null);
        Note = DispatchText.Optional(choice.Note, 300, "fulfilment.note_invalid", "The note");
        (ChosenByUserId, UpdatedAtUtc) = (userId, now);
    }

    public sealed record Choice(string Mode, string? DeliveryAddress, string? ContactPhone, Guid? TransporterId, Guid? DestinationBranchId, string? Note);
}

public static class ConsignmentStatus
{
    /// <summary>The goods left the store.</summary>
    public const string Dispatched = "DISPATCHED";

    /// <summary>Recorded in error (wrong LR, booking cancelled before the goods left): kept, with the reason.</summary>
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [Dispatched, Cancelled];
}

/// <summary>
/// One dispatch of goods (one LR/GR with a lorry service, or one trip by own vehicle or local delivery), for one or more
/// bills of the same customer. Never edited: a mistake is cancelled with a reason and recorded again.
/// </summary>
public sealed partial class Consignment : ITenantOwned
{
    /// <summary>Goods worth this much or more need an e-way bill when moved by road (most states; some set other limits).</summary>
    public const decimal EwayBillThreshold = 50_000m;

    private readonly List<Guid> _invoiceIds = [];
    private readonly List<ConsignmentLine> _lines = [];

    private Consignment()
    {
        Number = Mode = Status = PartyName = DeliveryAddress = IdempotencyKey = RequestHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public string Number { get; private set; }

    public string Mode { get; private set; }

    public string PartyName { get; private set; }

    public string DeliveryAddress { get; private set; }

    public Guid? TransporterId { get; private set; }

    public string? TransporterName { get; private set; }

    public string? TransporterGstin { get; private set; }

    public Guid? BookingBranchId { get; private set; }

    public string? BookingOffice { get; private set; }

    public Guid? DestinationBranchId { get; private set; }

    public string? DestinationBranch { get; private set; }

    public string? VehicleNumber { get; private set; }

    public string? DriverName { get; private set; }

    public string? DriverPhone { get; private set; }

    public string? LrNumber { get; private set; }

    public DateOnly? LrDate { get; private set; }

    public int PackageCount { get; private set; }

    public decimal? WeightKg { get; private set; }

    public string? FreightTerms { get; private set; }

    public decimal FreightAmount { get; private set; }

    public DateOnly DispatchDate { get; private set; }

    public DateOnly? ExpectedDeliveryDate { get; private set; }

    public string? EwayBillNumber { get; private set; }

    public decimal GoodsValue { get; private set; }

    public string Status { get; private set; }

    public string? CancelReason { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    /// <summary>Delivered, partly delivered or failed, once reported.</summary>
    public string? DeliveryOutcome { get; private set; }

    public DateOnly? DeliveredOn { get; private set; }

    /// <summary>Who received the goods (or why they were not delivered).</summary>
    public string? DeliveryNote { get; private set; }

    public Guid? DeliveryReportedByUserId { get; private set; }

    public DateTimeOffset? DeliveryReportedAtUtc { get; private set; }

    public Guid? ReturnRecordedByUserId { get; private set; }

    public DateTimeOffset? ReturnRecordedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public IReadOnlyList<Guid> InvoiceIds => _invoiceIds;

    /// <summary>The quantities this dispatch carries (from the bills' challans).</summary>
    public IReadOnlyList<ConsignmentLine> Lines => _lines;

    /// <summary>Whether the goods are worth an e-way bill and none was given (a warning: the rules differ by state).</summary>
    public bool EwayBillMissing => EwayBillNumber is null && GoodsValue >= EwayBillThreshold;

    public static Consignment Record(Guid businessId, Guid storeId, string number, Details details, Party party, Lorry? lorry, decimal goodsValue,
        IReadOnlyCollection<Guid> invoiceIds, (string Key, string Hash) request, Guid userId, DateTimeOffset now,
        IReadOnlyDictionary<Guid, decimal>? lines = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(invoiceIds);
        var mode = FulfilmentModes.Require(details.Mode);
        if (!FulfilmentModes.IsDispatched(mode))
        {
            throw new DomainException("consignment.pickup", "Goods picked up by the customer are not dispatched.");
        }

        if (invoiceIds.Count == 0)
        {
            throw new DomainException("consignment.no_invoices", "Choose the bills sent in this dispatch.");
        }

        var consignment = new Consignment
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            StoreId = storeId,
            Number = number,
            Mode = mode,
            PartyName = DispatchText.Required(party.Name, 200, "consignment.party_required", "Party name"),
            DeliveryAddress = DispatchText.Required(party.DeliveryAddress, 500, "fulfilment.address_required", "Delivery address"),
            VehicleNumber = Vehicle(details.VehicleNumber),
            DriverName = DispatchText.Optional(details.DriverName, 100, "consignment.driver_invalid", "The driver's name"),
            DriverPhone = DispatchText.Phone(details.DriverPhone, "consignment.driver_phone_invalid"),
            PackageCount = details.PackageCount is >= 1 and <= 9999 ? details.PackageCount : throw new DomainException("consignment.packages_invalid", "Packages are 1 to 9999."),
            WeightKg = details.WeightKg is null or (> 0 and <= 100_000) && (details.WeightKg is null || decimal.Round(details.WeightKg.Value, 3) == details.WeightKg)
                ? details.WeightKg
                : throw new DomainException("consignment.weight_invalid", "Weight is more than 0 and at most 100,000 kg (3 decimals)."),
            DispatchDate = details.DispatchDate,
            ExpectedDeliveryDate = details.ExpectedDeliveryDate is null || details.ExpectedDeliveryDate >= details.DispatchDate
                ? details.ExpectedDeliveryDate
                : throw new DomainException("consignment.expected_invalid", "Expected delivery cannot be before the dispatch date."),
            EwayBillNumber = string.IsNullOrWhiteSpace(details.EwayBillNumber) ? null
                : EwayBill().IsMatch(details.EwayBillNumber.Trim()) ? details.EwayBillNumber.Trim()
                : throw new DomainException("consignment.eway_invalid", "An e-way bill number is 12 digits."),
            GoodsValue = goodsValue,
            Status = ConsignmentStatus.Dispatched,
            CreatedByUserId = userId,
            CreatedAtUtc = now,
            IdempotencyKey = request.Key is { Length: >= 8 and <= 100 } key ? key : throw new DomainException("idempotency.key_invalid", "A request key is 8-100 characters."),
            RequestHash = request.Hash,
        };
        consignment._invoiceIds.AddRange(invoiceIds.Distinct());
        consignment._lines.AddRange((lines ?? new Dictionary<Guid, decimal>()).Where(l => l.Value != 0)
            .Select(l => ConsignmentLine.Carry(businessId, consignment.Id, l.Key, l.Value)));
        if (lines is not null && consignment._lines.Count == 0)
        {
            throw new DomainException("consignment.nothing_packed", "Nothing packed is waiting to go: pack the goods first.");
        }
        switch (mode)
        {
            case FulfilmentModes.Lorry:
                consignment.BookWith(lorry ?? throw new DomainException("consignment.transporter_required", "Choose the lorry service, booking office and destination."),
                    details);
                break;
            case FulfilmentModes.OwnVehicle:
                if (consignment.VehicleNumber is null)
                {
                    throw new DomainException("consignment.vehicle_required", "Give the vehicle number.");
                }

                break;
            default:
                if (consignment.DriverName is null)
                {
                    throw new DomainException("consignment.driver_required", "Give the name of the person delivering.");
                }

                break;
        }

        return consignment;
    }

    private void BookWith(Lorry lorry, Details details)
    {
        (TransporterId, TransporterName, TransporterGstin) = (lorry.Transporter.Id, lorry.Transporter.Name, lorry.Transporter.Gstin);
        if (lorry.Booking.TransporterId != lorry.Transporter.Id || lorry.Destination.TransporterId != lorry.Transporter.Id ||
            !lorry.Booking.IsBookingOffice || !lorry.Destination.IsDestination)
        {
            throw new DomainException("consignment.branches_invalid", "Choose a booking office and a destination branch of this lorry service.");
        }

        (BookingBranchId, BookingOffice) = (lorry.Booking.Id, $"{lorry.Booking.Name}, {lorry.Booking.City}");
        (DestinationBranchId, DestinationBranch) = (lorry.Destination.Id, $"{lorry.Destination.Name}, {lorry.Destination.City}");
        LrNumber = (details.LrNumber ?? string.Empty).Trim().ToUpperInvariant() is var lr && LrPattern().IsMatch(lr)
            ? lr
            : throw new DomainException("consignment.lr_invalid", "Give the LR/GR number (1-30 letters, digits, / or -).");
        LrDate = details.LrDate is { } date && date <= DispatchDate
            ? date
            : throw new DomainException("consignment.lr_date_invalid", "Give the LR/GR date (on or before the dispatch date).");
        FreightTerms = Dispatch.FreightTerms.All.Contains(details.FreightTerms ?? string.Empty)
            ? details.FreightTerms
            : throw new DomainException("consignment.freight_terms_invalid", "Choose paid or to-pay freight.");
        FreightAmount = details.FreightAmount is >= 0 and <= 10_000_000 && decimal.Round(details.FreightAmount, 2) == details.FreightAmount
            ? details.FreightAmount
            : throw new DomainException("consignment.freight_invalid", "Freight is 0 or more, in rupees and paise.");
    }

    public void Cancel(string reason, Guid userId, DateTimeOffset now)
    {
        if (Status != ConsignmentStatus.Dispatched)
        {
            throw new DomainException("consignment.not_active", "This dispatch is already cancelled.");
        }

        if (DeliveryOutcome is not null)
        {
            throw new DomainException("consignment.reported", "Its delivery has been reported; a delivered dispatch is not cancelled.");
        }

        CancelReason = DispatchText.Required(reason, 300, "consignment.reason_required", "Reason");
        (Status, CancelledByUserId, CancelledAtUtc) = (ConsignmentStatus.Cancelled, userId, now);
    }

    /// <summary>
    /// What reached the customer, once: delivered quantities per line. Anything not delivered needs the reason; none
    /// delivered is a failed delivery. The bill is not changed.
    /// </summary>
    public void ReportDelivery(IReadOnlyDictionary<Guid, decimal> delivered, DateOnly deliveredOn, string? note, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(delivered);
        RequireStanding();
        if (DeliveryOutcome is not null)
        {
            throw new DomainException("delivery.already_reported", "This dispatch's delivery has been reported.");
        }

        if (deliveredOn < DispatchDate)
        {
            throw new DomainException("delivery.date_invalid", "Delivery cannot be before the dispatch date.");
        }

        if (!delivered.Keys.ToHashSet().SetEquals(_lines.Select(l => l.ChallanLineId)))
        {
            throw new DomainException("delivery.lines_incomplete", "Give the delivered quantity of every item sent.");
        }

        // Everything is checked before anything changes, so a refused report leaves the dispatch as it was.
        foreach (var line in _lines)
        {
            ConsignmentLine.RequireDelivered(line.Quantity, delivered[line.ChallanLineId]);
        }

        var all = _lines.All(l => delivered[l.ChallanLineId] == l.Quantity);
        var none = _lines.All(l => delivered[l.ChallanLineId] == 0);
        var deliveryNote = all
            ? DispatchText.Optional(note, 300, "delivery.note_invalid", "The note")
            : DispatchText.Required(note, 300, "delivery.reason_required", "Reason not everything was delivered");
        foreach (var line in _lines)
        {
            line.Deliver(delivered[line.ChallanLineId]);
        }

        DeliveryOutcome = all ? DeliveryOutcomes.Delivered : none ? DeliveryOutcomes.Failed : DeliveryOutcomes.PartlyDelivered;
        (DeliveryNote, DeliveredOn, DeliveryReportedByUserId, DeliveryReportedAtUtc) = (deliveryNote, deliveredOn, userId, now);
    }

    /// <summary>Goods that were not delivered and came back to the store, once; they can be sent again.</summary>
    public void RecordReturn(IReadOnlyDictionary<Guid, decimal> returned, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(returned);
        RequireStanding();
        if (DeliveryOutcome is null or DeliveryOutcomes.Delivered)
        {
            throw new DomainException("return.not_undelivered", "Only goods reported as not delivered come back.");
        }

        if (ReturnRecordedAtUtc is not null)
        {
            throw new DomainException("return.already_recorded", "The goods back from this dispatch have been recorded.");
        }

        if (returned.Keys.Any(k => _lines.All(l => l.ChallanLineId != k)) || returned.Values.All(q => q == 0))
        {
            throw new DomainException("return.nothing", "Enter what came back.");
        }

        foreach (var line in _lines)
        {
            ConsignmentLine.RequireReturned(line, returned.GetValueOrDefault(line.ChallanLineId));
        }

        foreach (var line in _lines)
        {
            line.Return(returned.GetValueOrDefault(line.ChallanLineId));
        }

        (ReturnRecordedByUserId, ReturnRecordedAtUtc) = (userId, now);
    }

    private void RequireStanding()
    {
        if (Status != ConsignmentStatus.Dispatched)
        {
            throw new DomainException("consignment.not_active", "This dispatch is cancelled.");
        }
    }

    private static string? Vehicle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = new string(value.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return VehiclePattern().IsMatch(normalized) ? normalized : throw new DomainException("consignment.vehicle_invalid", "A vehicle number is 4-12 letters and digits, for example TN38AB1234.");
    }

    [GeneratedRegex("^[0-9]{12}$")]
    private static partial Regex EwayBill();

    [GeneratedRegex("^[A-Z0-9/-]{1,30}$")]
    private static partial Regex LrPattern();

    [GeneratedRegex("^[A-Z0-9]{4,12}$")]
    private static partial Regex VehiclePattern();

    public sealed record Details(
        string Mode, string? VehicleNumber, string? DriverName, string? DriverPhone, string? LrNumber, DateOnly? LrDate, int PackageCount, decimal? WeightKg,
        string? FreightTerms, decimal FreightAmount, DateOnly DispatchDate, DateOnly? ExpectedDeliveryDate, string? EwayBillNumber);

    public sealed record Party(string Name, string DeliveryAddress);

    public sealed record Lorry(Transporter Transporter, TransporterBranch Booking, TransporterBranch Destination);
}

/// <summary>Which bills a dispatch carries.</summary>
public sealed class ConsignmentInvoice : ITenantOwned
{
    public Guid ConsignmentId { get; set; }

    public Guid InvoiceId { get; set; }

    public Guid BusinessId { get; set; }
}

/// <summary>How a debtor usually gets their goods; filled in on their bills at the counter.</summary>
public sealed class DeliveryPreference : ITenantOwned
{
    private DeliveryPreference()
    {
        Mode = string.Empty;
    }

    public Guid DebtorId { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Mode { get; private set; }

    public Guid? TransporterId { get; private set; }

    public Guid? DestinationBranchId { get; private set; }

    public string? DeliveryAddress { get; private set; }

    public uint RowVersion { get; private set; }

    public static DeliveryPreference For(Guid businessId, Guid debtorId) => new() { BusinessId = businessId, DebtorId = debtorId, Mode = FulfilmentModes.Pickup };

    public void Change(string mode, Guid? transporterId, Guid? destinationBranchId, string? deliveryAddress)
    {
        Mode = FulfilmentModes.Require(mode);
        if (Mode == FulfilmentModes.Lorry && transporterId is null)
        {
            throw new DomainException("fulfilment.transporter_required", "Choose the lorry service.");
        }

        (TransporterId, DestinationBranchId) = Mode == FulfilmentModes.Lorry ? (transporterId, destinationBranchId) : (null, null);
        DeliveryAddress = DispatchText.Optional(deliveryAddress, 500, "fulfilment.address_invalid", "The delivery address");
    }
}
