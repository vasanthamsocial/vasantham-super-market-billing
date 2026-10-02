namespace SupermarketBilling.Application.Contracts;

/// <param name="Name">The legal name.</param>
/// <param name="Balance">What is owed to the supplier now (negative: an advance paid), from the ledger.</param>
/// <param name="Overdue">The part of the balance past its due date.</param>
public sealed record SupplierDto(
    Guid Id, string Code, string Name, string? Gstin, string StateCode, string? Address, string? Phone, bool IsActive, uint RowVersion,
    string? TradeName = null, string? ContactPerson = null, string? Email = null, string? WhatsAppNumber = null, string? SmsNumber = null,
    bool WhatsAppConsent = false, bool SmsConsent = false, int CreditPeriodDays = 0, decimal Balance = 0, decimal Overdue = 0);

/// <param name="OpeningBalance">Optional: the balance brought forward (positive: owed to the supplier; negative: an advance paid).</param>
public sealed record CreateSupplierRequest(
    string Code, string Name, string? Gstin, string StateCode, string? Address, string? Phone,
    string? TradeName = null, string? ContactPerson = null, string? Email = null, string? WhatsAppNumber = null, string? SmsNumber = null,
    bool WhatsAppConsent = false, bool SmsConsent = false, int CreditPeriodDays = 0, decimal? OpeningBalance = null, DateOnly? OpeningBalanceDate = null);

public sealed record UpdateSupplierRequest(
    string Name, string? Gstin, string StateCode, string? Address, string? Phone, bool IsActive, uint RowVersion,
    string? TradeName = null, string? ContactPerson = null, string? Email = null, string? WhatsAppNumber = null, string? SmsNumber = null,
    bool WhatsAppConsent = false, bool SmsConsent = false, int CreditPeriodDays = 0);

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

/// <param name="Received">Everything received on the line (free goods included), in its pack.</param>
/// <param name="UnitValue">What the supplier credits per pack returned (value and taxes over everything received).</param>
public sealed record ReturnableGrnLineDto(
    Guid GrnLineId, int LineNumber, string Description, string UnitCode, decimal Received, decimal Returned, decimal Returnable, string? BatchNumber,
    DateOnly? ExpiresOn, decimal UnitValue);

public sealed record ReturnableGrnDto(
    Guid GrnId, string Number, string Status, Guid StoreId, Guid SupplierId, string SupplierName, string SupplierInvoiceNumber, DateOnly SupplierInvoiceDate,
    IReadOnlyList<ReturnableGrnLineDto> Lines, IReadOnlyList<string> ReturnNumbers);

/// <param name="Quantity">In the receipt line's pack.</param>
public sealed record PurchaseReturnLineRequest(Guid GrnLineId, decimal Quantity);

public sealed record PurchaseReturnRequest(Guid GrnId, string Reason, IReadOnlyList<PurchaseReturnLineRequest> Lines, string? IdempotencyKey = null);

public sealed record PurchaseReturnLineDto(
    int LineNumber, Guid GrnLineId, string Description, string UnitCode, decimal Quantity, decimal BaseQuantity, decimal Taxable, decimal Cgst, decimal Sgst,
    decimal Igst, decimal Cess, decimal Total, decimal StockValue);

/// <param name="Total">What the supplier owes back; deducted from what is owed to them (first from this receipt).</param>
/// <param name="StockValue">The cost of the stock that went out (estimated in a preview).</param>
public sealed record PurchaseReturnDto(
    Guid Id, string Number, Guid StoreId, Guid SupplierId, string SupplierName, string? SupplierGstin, string SupplierStateCode, Guid GrnId, string GrnNumber,
    string SupplierInvoiceNumber, DateOnly SupplierInvoiceDate, DateOnly BusinessDate, string Reason, bool IsInterState, bool TaxRecoverable,
    IReadOnlyList<PurchaseReturnLineDto> Lines, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Cess, decimal RoundOff, decimal Total,
    decimal StockValue, string? CreatedBy, DateTimeOffset? CreatedAtUtc);

public sealed record PurchaseReturnSummaryDto(Guid Id, string Number, DateOnly BusinessDate, string SupplierName, string GrnNumber, string Reason, decimal Total);
