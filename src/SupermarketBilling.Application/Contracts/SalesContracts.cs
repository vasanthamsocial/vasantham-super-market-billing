namespace SupermarketBilling.Application.Contracts;

public sealed record CounterDto(Guid Id, Guid StoreId, string Code, string Name, bool IsActive, int ActiveDevices, string NextInvoiceNumber, uint RowVersion);

public sealed record CreateCounterRequest(Guid StoreId, string Code, string Name);

public sealed record UpdateCounterRequest(string Name, bool IsActive, uint RowVersion);

public sealed record CounterDeviceDto(
    Guid Id, string Name, string EnrolledBy, DateTimeOffset EnrolledAtUtc, DateTimeOffset? LastSeenAtUtc, DateTimeOffset? RevokedAtUtc, bool IsThisDevice);

public sealed record EnrolDeviceRequest(string Name);

/// <summary>The device token goes into an HttpOnly cookie; it is never returned in a body.</summary>
public sealed record EnrolDeviceResult(CounterDeviceDto Device, string DeviceToken);

/// <summary>What the POS needs to know about where it is billing.</summary>
public sealed record PosContextDto(
    Guid BusinessId, string BusinessName, Guid StoreId, string StoreName, string StoreStateCode, Guid CounterId, string CounterCode, string CounterName,
    Guid DeviceId, string DeviceName, string TaxMode, string NextInvoiceNumber, bool CanOverridePrices, bool CanDiscount, bool CanOverrideNegativeStock);

/// <summary>A supervisor approves at the counter by entering their own credentials. They are checked like a sign-in.</summary>
public sealed record SupervisorApprovalRequest(
    string Username, string Password, string? MfaCode, string Kind, Guid? VariantUnitId, decimal? Price, decimal? MaxDiscount, string Reason);

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

public sealed record CartRequest(
    string Channel, IReadOnlyList<CartLineRequest> Lines, decimal? BillDiscountAmount = null, decimal? BillDiscountPercent = null, BuyerRequest? Buyer = null);

public sealed record PaymentRequest(string Method, decimal Amount, string? Reference);

/// <param name="ExpectedGrandTotal">The total the cashier showed and collected; the bill is refused if the server's total differs.</param>
public sealed record IssueInvoiceRequest(
    string IdempotencyKey, CartRequest Cart, IReadOnlyList<PaymentRequest> Payments, decimal ExpectedGrandTotal, string? DiscountApprovalToken = null,
    bool NegativeStockOverride = false);

public sealed record CartLineDto(
    int LineNumber, Guid VariantId, Guid VariantUnitId, string Description, string UnitCode, string HsnSac, decimal Quantity, decimal? Mrp,
    decimal UnitPrice, bool TaxInclusive, string RateType, Guid? PriceRuleId, bool BelowMinimum, bool NeedsPriceApproval, string SupplyType,
    decimal GstRatePercent, decimal CessRatePercent, decimal Gross, decimal ItemDiscount, decimal BillDiscount, decimal Taxable, decimal Cgst,
    decimal Sgst, decimal Igst, decimal Cess, decimal Total);

public sealed record CartDto(
    string Kind, string TaxMode, bool IsInterState, string PlaceOfSupplyStateCode, IReadOnlyList<CartLineDto> Lines, decimal GrossTotal,
    decimal DiscountTotal, decimal TaxableTotal, decimal CgstTotal, decimal SgstTotal, decimal IgstTotal, decimal CessTotal, decimal RoundOff,
    decimal GrandTotal, bool NeedsDiscountApproval);

public sealed record InvoicePaymentDto(string Method, decimal Amount, string? Reference);

public sealed record InvoiceDto(
    Guid Id, string Number, string Kind, string TaxMode, string Channel, DateOnly BusinessDate, DateTimeOffset IssuedAtUtc, Guid StoreId, Guid CounterId,
    string CounterCode, string Cashier, string SellerName, string? SellerGstin, string SellerAddress, string SellerStateCode, string? BuyerName,
    string? BuyerGstin, string? BuyerPhone, string? BuyerAddress, string PlaceOfSupplyStateCode, bool IsInterState, IReadOnlyList<CartLineDto> Lines,
    decimal GrossTotal, decimal DiscountTotal, decimal TaxableTotal, decimal CgstTotal, decimal SgstTotal, decimal IgstTotal, decimal CessTotal,
    decimal RoundOff, decimal GrandTotal, decimal PaidTotal, decimal ChangeDue, IReadOnlyList<InvoicePaymentDto> Payments, string? Declaration);

public sealed record InvoiceSummaryDto(
    Guid Id, string Number, string Kind, DateOnly BusinessDate, DateTimeOffset IssuedAtUtc, string CounterCode, string Cashier, string? BuyerName,
    decimal GrandTotal);
