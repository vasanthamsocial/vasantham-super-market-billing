namespace SupermarketBilling.Application.Contracts;

public sealed record SupplierDto(Guid Id, string Code, string Name, string? Gstin, string StateCode, string? Address, string? Phone, bool IsActive, uint RowVersion);

public sealed record CreateSupplierRequest(string Code, string Name, string? Gstin, string StateCode, string? Address, string? Phone);

public sealed record UpdateSupplierRequest(string Name, string? Gstin, string StateCode, string? Address, string? Phone, bool IsActive, uint RowVersion);

public sealed record PurchaseSettingsDto(decimal CostReasonThresholdPercent, decimal CostApprovalThresholdPercent, bool AllowLossLeader, uint RowVersion);

/// <param name="Rate">Basic cost per pack, before discount and tax.</param>
/// <param name="SellingPrice">The selling price per pack (tax-inclusive) to check against the landed cost; defaults to the current retail price.</param>
/// <param name="CostChangeReason">Needed when the cost moved more than the business's reason threshold since the last receipt.</param>
/// <param name="LossLeaderReason">Needed (where allowed) to sell below the landed cost; then a manager must approve.</param>
public sealed record GrnLineRequest(
    Guid VariantUnitId,
    decimal Quantity,
    decimal Rate,
    decimal FreeQuantity = 0,
    decimal? Mrp = null,
    decimal? DiscountAmount = null,
    decimal? DiscountPercent = null,
    string? BatchNumber = null,
    DateOnly? ManufacturedOn = null,
    DateOnly? ExpiresOn = null,
    decimal? SellingPrice = null,
    string? CostChangeReason = null,
    string? LossLeaderReason = null,
    decimal? Weight = null,
    decimal? Volume = null,
    decimal? GstRatePercent = null,
    decimal? CessRatePercent = null,
    bool UpdateSellingPrice = false);

/// <param name="ManualAmounts">For MANUAL allocation: one amount per line, in line order.</param>
public sealed record GrnExpenseRequest(string Kind, decimal Amount, string Method, IReadOnlyList<decimal>? ManualAmounts = null, string? Note = null);

/// <param name="SupplierInvoiceTotal">The total printed on the supplier's invoice; may differ from the computed total by round-off (up to Rs. 1).</param>
/// <param name="IdempotencyKey">Required to save; ignored by the preview.</param>
public sealed record GrnRequest(
    Guid StoreId,
    Guid SupplierId,
    string SupplierInvoiceNumber,
    DateOnly SupplierInvoiceDate,
    string Classification,
    IReadOnlyList<GrnLineRequest> Lines,
    IReadOnlyList<GrnExpenseRequest>? Expenses = null,
    decimal? SupplierInvoiceTotal = null,
    string? PurchaseOrderReference = null,
    string? Notes = null,
    string? IdempotencyKey = null,
    Guid? PurchaseOrderId = null);

public sealed record CostChangeDto(
    decimal PreviousUnitCost, decimal NewUnitCost, decimal Difference, decimal PercentChange, string? PreviousSupplier, string? PreviousGrnNumber,
    DateOnly? PreviousDate, bool NeedsReason, bool NeedsApproval);

public sealed record BelowCostDto(decimal SellingPrice, decimal CostPerPack, decimal SellingNetPerPack, decimal LossPerPack, decimal MarginPercent);

public sealed record GrnLineDto(
    int LineNumber, Guid VariantId, Guid VariantUnitId, string Description, string UnitCode, decimal FactorToBase, decimal Quantity, decimal FreeQuantity,
    decimal BaseQuantity, decimal? Mrp, decimal Rate, decimal Discount, decimal GstRatePercent, decimal CessRatePercent, decimal Taxable, decimal Cgst,
    decimal Sgst, decimal Igst, decimal Cess, decimal Total, decimal ExpenseShare, decimal NonRecoverableTax, decimal LandedTotal, decimal LandedUnitCost,
    string? BatchNumber, DateOnly? ExpiresOn, decimal? SellingPrice, CostChangeDto? CostChange, BelowCostDto? BelowCost, string? CostChangeReason,
    string? LossLeaderReason, bool UpdateSellingPrice = false);

public sealed record GrnExpenseDto(string Kind, decimal Amount, string Method, string? Note, IReadOnlyList<decimal> Allocations);

/// <summary>A problem that stops the receipt from being saved as it is (shown by the preview, refused by the save).</summary>
public sealed record GrnIssueDto(string Code, string Message, int? LineNumber);

public sealed record GrnDto(
    Guid Id, string Number, string Status, Guid StoreId, Guid SupplierId, string SupplierName, string? SupplierGstin, string SupplierInvoiceNumber,
    DateOnly SupplierInvoiceDate, string Classification, string? PurchaseOrderReference, bool IsInterState, bool TaxRecoverable, DateOnly BusinessDate,
    string? Notes, IReadOnlyList<GrnLineDto> Lines, IReadOnlyList<GrnExpenseDto> Expenses, decimal GrossTotal, decimal DiscountTotal, decimal TaxableTotal,
    decimal CgstTotal, decimal SgstTotal, decimal IgstTotal, decimal CessTotal, decimal RoundOff, decimal InvoiceTotal, decimal ExpensesTotal,
    decimal LandedTotal, bool NeedsApproval, Guid? ApprovalRequestId, string? ReceivedBy, DateTimeOffset? ReceivedAtUtc, DateTimeOffset? PostedAtUtc,
    IReadOnlyList<GrnIssueDto> Issues, Guid? PurchaseOrderId = null, string? PurchaseOrderNumber = null);

public sealed record GrnSummaryDto(
    Guid Id, string Number, string Status, string SupplierName, string SupplierInvoiceNumber, DateOnly SupplierInvoiceDate, string Classification,
    DateOnly BusinessDate, decimal InvoiceTotal, decimal LandedTotal);

public sealed record PurchaseOrderLineRequest(Guid VariantUnitId, decimal Quantity, decimal? Rate = null);

public sealed record CreatePurchaseOrderRequest(Guid StoreId, Guid SupplierId, DateOnly? ExpectedDate, IReadOnlyList<PurchaseOrderLineRequest> Lines, string? Notes = null);

public sealed record PurchaseOrderLineDto(
    int LineNumber, Guid VariantId, Guid VariantUnitId, string Description, string UnitCode, decimal Ordered, decimal Received, decimal Outstanding, decimal? Rate);

/// <param name="Progress">NOT_RECEIVED, PARTLY_RECEIVED or RECEIVED (from the receipts against it).</param>
public sealed record PurchaseOrderDto(
    Guid Id, string Number, string Status, string Progress, Guid StoreId, Guid SupplierId, string SupplierName, DateOnly OrderDate, DateOnly? ExpectedDate,
    string? Notes, IReadOnlyList<PurchaseOrderLineDto> Lines, IReadOnlyList<string> ReceiptNumbers, uint RowVersion);

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long Size, string Sha256, string UploadedBy, DateTimeOffset UploadedAtUtc);
