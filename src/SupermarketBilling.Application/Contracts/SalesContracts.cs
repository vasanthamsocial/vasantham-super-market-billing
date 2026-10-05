namespace SupermarketBilling.Application.Contracts;

public sealed record CounterDto(Guid Id, Guid StoreId, string Code, string Name, bool IsActive, int ActiveDevices, string NextInvoiceNumber, uint RowVersion);

public sealed record CreateCounterRequest(Guid StoreId, string Code, string Name);

public sealed record UpdateCounterRequest(string Name, bool IsActive, uint RowVersion);

/// <param name="OfflineMaxBills">With the amount and hours: this device may bill without the server within these limits (D-039); null: it may not.</param>
public sealed record CounterDeviceDto(
    Guid Id, string Name, string EnrolledBy, DateTimeOffset EnrolledAtUtc, DateTimeOffset? LastSeenAtUtc, DateTimeOffset? RevokedAtUtc, bool IsThisDevice,
    int? OfflineMaxBills = null, decimal? OfflineMaxAmount = null, int? OfflineMaxHours = null);

public sealed record EnrolDeviceRequest(string Name);

/// <summary>The device token goes into an HttpOnly cookie; it is never returned in a body.</summary>
public sealed record EnrolDeviceResult(CounterDeviceDto Device, string DeviceToken);

/// <summary>What the POS needs to know about where it is billing.</summary>
public sealed record PosContextDto(
    Guid BusinessId, string BusinessName, Guid StoreId, string StoreName, string StoreStateCode, Guid CounterId, string CounterCode, string CounterName,
    Guid DeviceId, string DeviceName, string TaxMode, string NextInvoiceNumber, bool CanOverridePrices, bool CanDiscount, bool CanOverrideNegativeStock,
    bool CanOverrideCreditLimit = false, string? OfflineSeries = null);

/// <summary>A supervisor approves at the counter by entering their own credentials. They are checked like a sign-in.</summary>
public sealed record SupervisorApprovalRequest(
    string Username, string Password, string? MfaCode, string Kind, Guid? VariantUnitId, decimal? Price, decimal? MaxAmount, string Reason);

public sealed record SupervisorApprovalResponse(Guid ApprovalId, string Token, string ApprovedBy, DateTimeOffset ExpiresAtUtc);

/// <param name="Quantity">In the pack given by <paramref name="VariantUnitId"/> (kilograms for loose goods sold by KG).</param>
/// <param name="Mrp">Which MRP is being sold, when the pack has more than one.</param>
/// <param name="OverridePrice">A price other than the rule's, per pack; needs <paramref name="OverrideApprovalToken"/> unless the cashier may override.</param>
/// <param name="BatchId">Sell from this batch (otherwise stock is taken in valuation order).</param>
public sealed record CartLineRequest(
    Guid VariantUnitId,
    decimal Quantity,
    decimal? Mrp = null,
    decimal? OverridePrice = null,
    string? OverrideApprovalToken = null,
    decimal? DiscountAmount = null,
    decimal? DiscountPercent = null,
    Guid? BatchId = null);

/// <param name="StateCode">Place of supply for a buyer without a GSTIN (taken from the GSTIN otherwise).</param>
public sealed record BuyerRequest(string? Name, string? Gstin, string? Phone, string? Address, string? StateCode);

/// <param name="DebtorId">The customer's account: their customer-group prices apply, their details go on the invoice (unless given), and the bill may go on account.</param>
public sealed record CartRequest(
    string Channel, IReadOnlyList<CartLineRequest> Lines, decimal? BillDiscountAmount = null, decimal? BillDiscountPercent = null, BuyerRequest? Buyer = null,
    Guid? DebtorId = null);

public sealed record PaymentRequest(string Method, decimal Amount, string? Reference);

/// <param name="ExpectedGrandTotal">The total the cashier showed and collected; the bill is refused if the server's total differs.</param>
/// <param name="CreditApprovalToken">A supervisor's approval to go beyond the debtor's credit limit (kind CREDIT_LIMIT, up to the amount over).</param>
public sealed record IssueInvoiceRequest(
    string IdempotencyKey, CartRequest Cart, IReadOnlyList<PaymentRequest> Payments, decimal ExpectedGrandTotal, string? DiscountApprovalToken = null,
    bool NegativeStockOverride = false, string? CreditApprovalToken = null, FulfilmentRequest? Fulfilment = null);

public sealed record CartLineDto(
    int LineNumber, Guid VariantId, Guid VariantUnitId, string Description, string UnitCode, string HsnSac, decimal Quantity, decimal? Mrp,
    decimal UnitPrice, bool TaxInclusive, string RateType, Guid? PriceRuleId, bool BelowMinimum, bool NeedsPriceApproval, string SupplyType,
    decimal GstRatePercent, decimal CessRatePercent, decimal Gross, decimal ItemDiscount, decimal BillDiscount, decimal Taxable, decimal Cgst,
    decimal Sgst, decimal Igst, decimal Cess, decimal Total);

public sealed record CartDto(
    string Kind, string TaxMode, bool IsInterState, string PlaceOfSupplyStateCode, IReadOnlyList<CartLineDto> Lines, decimal GrossTotal,
    decimal DiscountTotal, decimal TaxableTotal, decimal CgstTotal, decimal SgstTotal, decimal IgstTotal, decimal CessTotal, decimal RoundOff,
    decimal GrandTotal, bool NeedsDiscountApproval, CounterDebtorDto? Debtor = null);

/// <param name="Available">Credit left: limit less what is owed (negative when already over the limit).</param>
public sealed record CounterDebtorDto(
    Guid Id, string Code, string Name, string? Phone, string? Gstin, string Status, decimal CreditLimit, int CreditPeriodDays, decimal Balance, decimal Overdue,
    decimal Available);

public sealed record InvoicePaymentDto(string Method, decimal Amount, string? Reference);

public sealed record InvoiceDto(
    Guid Id, string Number, string Kind, string TaxMode, string Channel, DateOnly BusinessDate, DateTimeOffset IssuedAtUtc, Guid StoreId, Guid CounterId,
    string CounterCode, string Cashier, string SellerName, string? SellerGstin, string SellerAddress, string SellerStateCode, string? BuyerName,
    string? BuyerGstin, string? BuyerPhone, string? BuyerAddress, string PlaceOfSupplyStateCode, bool IsInterState, IReadOnlyList<CartLineDto> Lines,
    decimal GrossTotal, decimal DiscountTotal, decimal TaxableTotal, decimal CgstTotal, decimal SgstTotal, decimal IgstTotal, decimal CessTotal,
    decimal RoundOff, decimal GrandTotal, decimal PaidTotal, decimal ChangeDue, IReadOnlyList<InvoicePaymentDto> Payments, string? Declaration,
    Guid? DebtorId = null, string? DebtorCode = null, DateOnly? DueDate = null, decimal OnAccount = 0, FulfilmentDto? Fulfilment = null);

public sealed record InvoiceSummaryDto(
    Guid Id, string Number, string Kind, DateOnly BusinessDate, DateTimeOffset IssuedAtUtc, string CounterCode, string Cashier, string? BuyerName,
    decimal GrandTotal);

public sealed record ParkBillRequest(string? Label, CartRequest Cart);

public sealed record ParkedBillDto(Guid Id, string? Label, int Items, string ParkedBy, DateTimeOffset ParkedAtUtc);

/// <param name="Restock">False when the goods are damaged and must not go back on the shelf.</param>
public sealed record ReturnLineRequest(Guid OriginalLineId, decimal Quantity, bool Restock = true);

public sealed record ReturnPreviewRequest(Guid OriginalInvoiceId, IReadOnlyList<ReturnLineRequest> Lines);

/// <param name="ExpectedGrandTotal">The refund the cashier showed; the return is refused if the server's total differs.</param>
public sealed record IssueReturnRequest(
    string IdempotencyKey, Guid OriginalInvoiceId, string Reason, IReadOnlyList<ReturnLineRequest> Lines, IReadOnlyList<PaymentRequest> Refunds,
    decimal ExpectedGrandTotal, string? ApprovalToken = null);

/// <summary>An invoice found for a return, with how much of each line can still be returned.</summary>
public sealed record ReturnableInvoiceDto(InvoiceDto Invoice, IReadOnlyList<ReturnableLineDto> Lines);

public sealed record ReturnableLineDto(Guid OriginalLineId, int LineNumber, string Description, string UnitCode, decimal Sold, decimal Returnable, decimal UnitTotal);

public sealed record CreditNoteLineDto(
    int LineNumber, Guid OriginalLineId, string Description, string HsnSac, string UnitCode, decimal Quantity, bool Restocked, decimal GstRatePercent,
    decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Cess, decimal Total);

public sealed record ReturnPreviewDto(
    IReadOnlyList<CreditNoteLineDto> Lines, decimal TaxableTotal, decimal CgstTotal, decimal SgstTotal, decimal IgstTotal, decimal CessTotal,
    decimal RoundOff, decimal GrandTotal, bool NeedsApproval);

public sealed record CreditNoteDto(
    Guid Id, string Number, Guid OriginalInvoiceId, string OriginalInvoiceNumber, DateOnly OriginalInvoiceDate, string TaxMode, DateOnly BusinessDate,
    DateTimeOffset IssuedAtUtc, Guid StoreId, string CounterCode, string Cashier, string Reason, string SellerName, string? SellerGstin, string SellerAddress,
    string SellerStateCode, string? BuyerName, string? BuyerGstin, string PlaceOfSupplyStateCode, bool IsInterState, IReadOnlyList<CreditNoteLineDto> Lines,
    decimal TaxableTotal, decimal CgstTotal, decimal SgstTotal, decimal IgstTotal, decimal CessTotal, decimal RoundOff, decimal GrandTotal,
    IReadOnlyList<InvoicePaymentDto> Refunds, decimal StoreCredit, decimal StoreCreditLeft);

public sealed record CreditNoteSummaryDto(
    Guid Id, string Number, string OriginalInvoiceNumber, DateOnly BusinessDate, DateTimeOffset IssuedAtUtc, string CounterCode, string Cashier,
    decimal GrandTotal, decimal StoreCreditLeft);

public sealed record DenominationCount(decimal Denomination, int Count);

public sealed record OpenShiftRequest(IReadOnlyList<DenominationCount> Counts);

/// <param name="Note">Required when the count differs from what was expected (the cashier does not see that beforehand).</param>
public sealed record CloseShiftRequest(IReadOnlyList<DenominationCount> Counts, string? Note);

public sealed record CashMovementRequest(string Kind, decimal Amount, string Reason, string? ApprovalToken = null);

public sealed record ReviewShiftRequest(string Note);

public sealed record MethodTotalDto(string Method, decimal Amount);

public sealed record CashMovementDto(string Kind, decimal Amount, string Reason, string RecordedBy, DateTimeOffset RecordedAtUtc);

/// <summary>
/// A shift's report. While the shift is open the cashier's view leaves out the expected cash (the close is blind);
/// managers and closed shifts show everything.
/// </summary>
public sealed record ShiftSummaryDto(
    Guid Id, Guid StoreId, Guid CounterId, string CounterCode, Guid CashierUserId, string Cashier, string Status, DateOnly BusinessDate, DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc, decimal OpeningFloat, int Invoices, decimal SalesTotal, int Returns, decimal ReturnsTotal,
    IReadOnlyList<MethodTotalDto> Payments, IReadOnlyList<MethodTotalDto> Refunds, IReadOnlyList<CashMovementDto> Movements, decimal? ExpectedCash,
    decimal? CountedCash, decimal? Difference, string? CloseNote, bool NeedsReview, string? ReviewedBy, string? ReviewNote, int ParkedBillsCleared,
    uint RowVersion, IReadOnlyList<MethodTotalDto>? Receipts = null);
