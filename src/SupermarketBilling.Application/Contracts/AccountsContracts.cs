namespace SupermarketBilling.Application.Contracts;

/// <param name="OpeningBalance">Optional: the balance brought forward (positive: owed by the debtor; negative: an advance).</param>
public sealed record CreateDebtorRequest(
    string Code,
    string LegalName,
    string? TradeName,
    string? Gstin,
    string StateCode,
    string? Address = null,
    string? ContactPerson = null,
    string? Phone = null,
    string? Email = null,
    string? WhatsAppNumber = null,
    string? SmsNumber = null,
    bool WhatsAppConsent = false,
    bool SmsConsent = false,
    int CreditPeriodDays = 0,
    decimal CreditLimit = 0,
    Guid? CustomerGroupId = null,
    decimal? OpeningBalance = null,
    DateOnly? OpeningBalanceDate = null);

public sealed record UpdateDebtorRequest(
    string LegalName,
    string? TradeName,
    string? Gstin,
    string StateCode,
    string? Address,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? WhatsAppNumber,
    string? SmsNumber,
    bool WhatsAppConsent,
    bool SmsConsent,
    int CreditPeriodDays,
    decimal CreditLimit,
    Guid? CustomerGroupId,
    string Status,
    uint RowVersion);

/// <param name="Balance">What the debtor owes now (negative: an advance), from the ledger.</param>
/// <param name="Overdue">The part of the balance past its due date.</param>
public sealed record DebtorDto(
    Guid Id, string Code, string LegalName, string? TradeName, string DisplayName, string? Gstin, string StateCode, string? Address, string? ContactPerson,
    string? Phone, string? Email, string? WhatsAppNumber, string? SmsNumber, bool WhatsAppConsent, bool SmsConsent, DateTimeOffset? ConsentChangedAtUtc,
    int CreditPeriodDays, decimal CreditLimit, Guid? CustomerGroupId, string Status, decimal Balance, decimal Overdue, uint RowVersion);

/// <param name="Outstanding">For a charge, what is still unpaid; for a payment, what is not yet applied to a charge.</param>
public sealed record AccountEntryDto(
    Guid Id, long Sequence, string EntryType, Guid? StoreId, Guid? DocumentId, string? DocumentNumber, DateOnly EntryDate, DateOnly? DueDate,
    decimal Amount, decimal BalanceAfter, string Narration, decimal Outstanding, string CreatedBy, DateTimeOffset CreatedAtUtc);

/// <param name="OpeningBalance">The balance before <paramref name="From"/>.</param>
public sealed record StatementDto(
    string PartyType, Guid PartyId, string PartyName, DateOnly? From, DateOnly? To, decimal OpeningBalance, IReadOnlyList<AccountEntryDto> Entries,
    decimal ClosingBalance);

public sealed record OpenItemDto(
    Guid EntryId, string EntryType, string? DocumentNumber, Guid? DocumentId, DateOnly EntryDate, DateOnly? DueDate, decimal Amount, decimal Remaining,
    int DaysOverdue);

/// <param name="Ageing">Unpaid charges by days past due: not due, 1-30, 31-60, 61-90, over 90.</param>
public sealed record OpenItemsDto(
    string PartyType, Guid PartyId, decimal Balance, decimal Overdue, IReadOnlyList<OpenItemDto> Charges, IReadOnlyList<OpenItemDto> UnappliedPayments,
    AgeingDto Ageing);

public sealed record AgeingDto(decimal NotDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90);

/// <param name="Amount">Positive: owed (to the supplier, or by the debtor); negative: an advance.</param>
public sealed record OpeningBalanceRequest(decimal Amount, DateOnly AsOf, DateOnly? DueDate = null, string? Note = null);

/// <param name="Amount">Positive adds to what is owed; negative reduces it.</param>
public sealed record LedgerAdjustmentRequest(decimal Amount, string Reason);

public sealed record LedgerAdjustmentResponse(Guid ApprovalRequestId, string Message);

public sealed record SettlementAllocation(Guid ChargeEntryId, decimal Amount);

/// <param name="Allocations">Which bills to pay and how much; when empty, the oldest due bills are paid first.</param>
public sealed record SupplierPaymentRequest(
    Guid StoreId,
    Guid SupplierId,
    string Method,
    decimal Amount,
    string? Reference = null,
    string? Note = null,
    IReadOnlyList<SettlementAllocation>? Allocations = null,
    string? IdempotencyKey = null);

public sealed record AppliedToDto(Guid ChargeEntryId, string EntryType, string? DocumentNumber, DateOnly EntryDate, decimal Amount);

public sealed record SupplierPaymentDto(
    Guid Id, string Number, Guid StoreId, Guid SupplierId, string SupplierName, DateOnly PaymentDate, string Method, string? Reference, decimal Amount,
    string Note, string PaidBy, DateTimeOffset CreatedAtUtc, IReadOnlyList<AppliedToDto> AppliedTo, decimal Unapplied);
