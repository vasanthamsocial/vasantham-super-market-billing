namespace SupermarketBilling.Application.Contracts;

public sealed record TransporterBranchDto(
    Guid Id, string Name, string City, string? Address, string? Phone, bool IsBookingOffice, bool IsDestination, bool IsActive, uint RowVersion);

public sealed record TransporterRouteDto(Guid Id, Guid FromBranchId, string FromBranch, Guid ToBranchId, string ToBranch, int TransitDays, bool IsActive, uint RowVersion);

public sealed record TransporterDto(
    Guid Id, string Code, string Name, string? Gstin, string? Phone, string? Address, bool IsActive, IReadOnlyList<TransporterBranchDto> Branches,
    IReadOnlyList<TransporterRouteDto> Routes, uint RowVersion);

public sealed record CreateTransporterRequest(string Code, string Name, string? Gstin = null, string? Phone = null, string? Address = null);

public sealed record UpdateTransporterRequest(string Name, string? Gstin, string? Phone, string? Address, bool IsActive, uint RowVersion);

public sealed record TransporterBranchRequest(
    string Name, string City, string? Address = null, string? Phone = null, bool IsBookingOffice = false, bool IsDestination = true, bool IsActive = true,
    uint RowVersion = 0);

public sealed record CreateTransporterRouteRequest(Guid FromBranchId, Guid ToBranchId, int TransitDays);

public sealed record UpdateTransporterRouteRequest(int TransitDays, bool IsActive, uint RowVersion);

/// <summary>How a bill's goods reach the customer. Pickup needs nothing else; the others need the delivery address; a lorry service needs the transporter.</summary>
public sealed record FulfilmentRequest(
    string Mode, string? DeliveryAddress = null, string? ContactPhone = null, Guid? TransporterId = null, Guid? DestinationBranchId = null, string? Note = null,
    uint RowVersion = 0);

/// <param name="Status">AWAITING_DISPATCH, DISPATCHED, or PICKUP (nothing to dispatch).</param>
public sealed record FulfilmentDto(
    string Mode, string? DeliveryAddress, string? ContactPhone, Guid? TransporterId, string? TransporterName, Guid? DestinationBranchId, string? DestinationBranch,
    string? Note, string Status, IReadOnlyList<string> Consignments, uint RowVersion, Guid? ChallanId = null, string? ChallanNumber = null);

public sealed record DeliveryPreferenceDto(string Mode, Guid? TransporterId, string? TransporterName, Guid? DestinationBranchId, string? DestinationBranch,
    string? DeliveryAddress, uint RowVersion);

public sealed record DeliveryPreferenceRequest(string Mode, Guid? TransporterId = null, Guid? DestinationBranchId = null, string? DeliveryAddress = null, uint RowVersion = 0);

/// <summary>A bill whose packed goods are ready to leave the store.</summary>
public sealed record DispatchQueueItemDto(
    Guid InvoiceId, string InvoiceNumber, DateTimeOffset IssuedAtUtc, Guid StoreId, Guid? DebtorId, string PartyName, decimal GrandTotal, string Mode,
    string? DeliveryAddress, Guid? TransporterId, string? TransporterName, Guid? DestinationBranchId, string? DestinationBranch, Guid? ChallanId = null,
    string? ChallanNumber = null, string? Progress = null);

/// <param name="InvoiceIds">The bills in this dispatch: one customer, one store, one way of delivery (and one lorry service).</param>
/// <param name="Lines">What to send from the bills' challans; when left out, everything packed and ready to send.</param>
/// <param name="BookingBranchId">Lorry service only: the office the goods were booked at.</param>
/// <param name="DestinationBranchId">Lorry service only: the branch the customer collects from (defaults to the one on the bills).</param>
public sealed record RecordConsignmentRequest(
    string IdempotencyKey, IReadOnlyList<Guid> InvoiceIds, int PackageCount, DateOnly DispatchDate, Guid? BookingBranchId = null, Guid? DestinationBranchId = null,
    string? VehicleNumber = null, string? DriverName = null, string? DriverPhone = null, string? LrNumber = null, DateOnly? LrDate = null, decimal? WeightKg = null,
    string? FreightTerms = null, decimal FreightAmount = 0, DateOnly? ExpectedDeliveryDate = null, string? EwayBillNumber = null,
    IReadOnlyList<ChallanQuantityRequest>? Lines = null);

public sealed record ConsignmentInvoiceDto(Guid InvoiceId, string Number, decimal GrandTotal);

/// <param name="EwayBillMissing">The goods are worth Rs. 50,000 or more and no e-way bill was given (check your state's rule).</param>
public sealed record ConsignmentDto(
    Guid Id, string Number, Guid StoreId, string Mode, string Status, string PartyName, string DeliveryAddress, IReadOnlyList<ConsignmentInvoiceDto> Invoices,
    Guid? TransporterId, string? TransporterName, string? TransporterGstin, string? BookingOffice, string? DestinationBranch, string? VehicleNumber,
    string? DriverName, string? DriverPhone, string? LrNumber, DateOnly? LrDate, int PackageCount, decimal? WeightKg, string? FreightTerms, decimal FreightAmount,
    DateOnly DispatchDate, DateOnly? ExpectedDeliveryDate, string? EwayBillNumber, decimal GoodsValue, bool EwayBillMissing, string? CancelReason,
    string CreatedBy, DateTimeOffset CreatedAtUtc, uint RowVersion, IReadOnlyList<ConsignmentLineDto>? Lines = null, string? DeliveryOutcome = null,
    DateOnly? DeliveredOn = null, string? DeliveryNote = null, bool ReturnRecorded = false);

public sealed record CancelConsignmentRequest(string Reason, uint RowVersion);

/// <summary>What the counter needs to choose a lorry service: active transporters and their destination branches.</summary>
public sealed record CounterTransporterDto(Guid Id, string Code, string Name, IReadOnlyList<CounterBranchDto> Destinations);

public sealed record CounterBranchDto(Guid Id, string Name, string City);

/// <param name="DeliveryAddress">The customer's usual delivery address (or their address), to fill in.</param>
public sealed record CounterDeliveryOptionsDto(IReadOnlyList<CounterTransporterDto> Transporters, DeliveryPreferenceDto? Preference, string? DeliveryAddress);

/// <param name="ReadyToSend">Packed, not yet sent, and not settled by a credit note: what the next dispatch can carry.</param>
/// <param name="Lost">Sent, reported not delivered, and not back in the store.</param>
/// <param name="Credited">Taken back by credit notes against the bill line.</param>
/// <param name="Difference">Billed but not sent (short) or lost, less what was credited: to settle with a credit note or by sending again.</param>
public sealed record ChallanLineDto(
    Guid Id, int LineNumber, string ItemName, string? VariantName, string UnitCode, decimal Quantity, decimal FreeQuantity, string? Batches, decimal? Picked,
    decimal? Checked, string? ShortReason, decimal Packed, decimal InTransit, decimal Delivered, decimal Returned, decimal Lost, decimal ReadyToSend,
    decimal Credited, decimal Difference);

public sealed record ChallanEventDto(string Kind, string? Detail, string By, DateTimeOffset AtUtc);

public sealed record ChallanDispatchDto(Guid Id, string Number, string Status, DateOnly DispatchDate, string? LrNumber, string? DeliveryOutcome);

/// <param name="Progress">TO_PICK, TO_CHECK, TO_PACK, PARTLY_PACKED, PACKED, PARTLY_DISPATCHED, DISPATCHED, DELIVERED, NOTHING_TO_SEND or CANCELLED.</param>
public sealed record ChallanDto(
    Guid Id, string Number, Guid StoreId, Guid InvoiceId, string InvoiceNumber, DateOnly InvoiceDate, Guid? DebtorId, string PartyName, string Mode,
    string? DeliveryAddress, string? ContactPhone, string? Route, string? TransporterName, string? DestinationBranch, string Status, string Progress, string? Picker,
    string? Checker, string? Packer, int PackageCount, string? CancelReason, IReadOnlyList<ChallanLineDto> Lines, IReadOnlyList<ChallanDispatchDto> Dispatches,
    IReadOnlyList<ChallanEventDto> Events, uint RowVersion);

public sealed record ChallanSummaryDto(
    Guid Id, string Number, Guid StoreId, Guid InvoiceId, string InvoiceNumber, string PartyName, string Mode, string? TransporterName, string? DestinationBranch,
    string Progress, bool ReadyToSend, bool HasDifference, DateTimeOffset CreatedAtUtc);

public sealed record CountedLineRequest(Guid LineId, decimal Quantity, string? Reason = null);

public sealed record CountChallanRequest(IReadOnlyList<CountedLineRequest> Lines, uint RowVersion);

public sealed record PackChallanRequest(IReadOnlyList<CountedLineRequest> Lines, int Packages, uint RowVersion);

/// <summary>A quantity of one challan item (dispatched, delivered or back in the store).</summary>
public sealed record ChallanQuantityRequest(Guid ChallanLineId, decimal Quantity);

public sealed record ReportDeliveryRequest(DateOnly DeliveredOn, IReadOnlyList<ChallanQuantityRequest> Lines, string? Note, uint RowVersion);

public sealed record RecordReturnRequest(IReadOnlyList<ChallanQuantityRequest> Lines, uint RowVersion);

public sealed record ConsignmentLineDto(Guid ChallanLineId, Guid InvoiceId, string ItemName, string UnitCode, decimal Quantity, decimal? Delivered, decimal? Returned);
