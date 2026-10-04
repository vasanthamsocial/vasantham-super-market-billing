namespace SupermarketBilling.Application.Contracts;

public sealed record RouteDto(Guid Id, string Code, string Name, string? Description, bool IsActive, int Parties, uint RowVersion);

public sealed record CreateRouteRequest(string Code, string Name, string? Description = null);

public sealed record UpdateRouteRequest(string Name, string? Description, bool IsActive, uint RowVersion);

/// <summary>Someone who can collect (holds <c>collections.collect</c> in the business).</summary>
public sealed record CollectorDto(Guid UserId, string DisplayName, string Username);

/// <param name="Weekdays">Day names, for example MONDAY (weekday and fortnightly schedules).</param>
/// <param name="AnchorDate">Fortnightly: a date in a visiting week. Specific date: the visit date.</param>
/// <param name="DueOffsetDays">Due-date schedule: days after the due date (negative: before).</param>
public sealed record SetCollectionPlanRequest(
    Guid? RouteId,
    int? VisitSequence,
    Guid? PrimaryCollectorUserId,
    Guid? BackupCollectorUserId,
    TimeOnly? PreferredFrom,
    TimeOnly? PreferredTo,
    string ScheduleType,
    IReadOnlyList<string>? Weekdays = null,
    DateOnly? AnchorDate = null,
    int? MonthDay = null,
    int? DueOffsetDays = null);

public sealed record CollectionPlanDto(
    Guid DebtorId, string DebtorCode, string DebtorName, Guid? RouteId, string? RouteCode, int? VisitSequence, Guid? PrimaryCollectorUserId, string? PrimaryCollector,
    Guid? BackupCollectorUserId, string? BackupCollector, TimeOnly? PreferredFrom, TimeOnly? PreferredTo, string ScheduleType, IReadOnlyList<string> Weekdays,
    DateOnly? AnchorDate, int? MonthDay, int? DueOffsetDays, string Description);

public sealed record AssignVisitRequest(Guid DebtorId, Guid CollectorUserId, DateOnly VisitDate, string? Note = null);

public sealed record VisitDto(
    Guid Id, Guid DebtorId, string DebtorName, Guid CollectorUserId, string Collector, DateOnly VisitDate, string Note, bool IsCancelled, string AssignedBy);

public sealed record RecordPromiseRequest(decimal Amount, DateOnly PromisedDate, string? Note = null);

/// <param name="Status">PENDING, KEPT, BROKEN or CANCELLED (kept: paid at least the amount from when it was made to the promised date).</param>
public sealed record PromiseDto(
    Guid Id, Guid DebtorId, decimal Amount, DateOnly PromisedDate, string Note, string Status, decimal PaidSince, string RecordedBy, DateTimeOffset RecordedAtUtc);

public sealed record RecordAbsenceRequest(Guid CollectorUserId, DateOnly AbsentOn, string? Reason = null);

public sealed record AbsenceDto(Guid Id, Guid CollectorUserId, string Collector, DateOnly AbsentOn, string Reason);

/// <param name="Reasons">Why the party is on the list: SCHEDULE, DUE_DATE, PROMISE, ASSIGNED, BACKUP, OVERDUE.</param>
/// <param name="DueBalance">Unpaid charges due on or before the day (overdue included).</param>
/// <param name="Status">COLLECTED when money was received from the party that day, VISITED when a visit brought none, otherwise PENDING.</param>
public sealed record DayPartyDto(
    Guid DebtorId, string Code, string Name, string? RouteCode, string? RouteName, int? VisitSequence, string? Address, string? Phone, string? WhatsAppNumber,
    TimeOnly? PreferredFrom, TimeOnly? PreferredTo, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes, decimal TotalBalance, decimal DueBalance,
    decimal OverdueBalance, decimal NotYetDueBalance, string? OldestUnpaidDocument, DateOnly? OldestUnpaidDueDate, int DaysOverdue, DateOnly? LastCollectionDate,
    decimal? LastCollectionAmount, decimal? PromisedAmount, DateOnly? PromisedDate, decimal CollectedToday, string Status, string? VisitOutcome = null);

public sealed record DayListDto(
    Guid CollectorUserId, string Collector, DateOnly Date, bool Absent, IReadOnlyList<DayPartyDto> Parties, decimal DueTotal, decimal OverdueTotal, decimal CollectedTotal);

public sealed record OpenCollectorSessionRequest(Guid StoreId);

public sealed record CashCount(decimal Denomination, int Count);

public sealed record HandOverRequest(IReadOnlyList<CashCount> Counts);

public sealed record ConfirmHandoverRequest(IReadOnlyList<CashCount> Counts, string? Note = null);

public sealed record SessionInstrumentDto(Guid ChequeId, string Kind, string Number, string? BankName, decimal Amount, string DebtorName, string Status);

/// <param name="ExpectedCash">The cash receipts of the round (shown to the receiver; to the collector only after confirmation).</param>
public sealed record CollectorSessionDto(
    Guid Id, Guid StoreId, Guid CollectorUserId, string Collector, DateOnly BusinessDate, string Status, DateTimeOffset OpenedAtUtc, int Receipts,
    IReadOnlyList<MethodTotalDto> Totals, IReadOnlyList<SessionInstrumentDto> Instruments, decimal? ExpectedCash, decimal? DeclaredCash, decimal? CountedCash,
    decimal? Variance, string? ReceivedBy, string? Note, DateTimeOffset? HandedOverAtUtc, DateTimeOffset? ConfirmedAtUtc, uint RowVersion);

/// <param name="Allocations">Which bills to pay (needs <c>collections.allocate</c>); otherwise the oldest due first.</param>
public sealed record FieldCollectionRequest(
    Guid DebtorId,
    string Method,
    decimal Amount,
    string? Reference = null,
    string? BankName = null,
    DateOnly? ChequeDate = null,
    string? Note = null,
    IReadOnlyList<SettlementAllocation>? Allocations = null,
    string? IdempotencyKey = null);

public sealed record VisitOutcomeRequest(Guid DebtorId, string Outcome, string? Note = null);

public sealed record ChequeEventDto(string Status, DateOnly EventDate, string? Note, string RecordedBy, DateTimeOffset RecordedAtUtc);

public sealed record ChequeDto(
    Guid Id, string Kind, string Number, string? BankName, DateOnly? ChequeDate, decimal Amount, string Status, Guid ReceiptId, string ReceiptNumber, Guid DebtorId,
    string DebtorName, string? ReplacedByReceiptNumber, IReadOnlyList<ChequeEventDto> History, uint RowVersion);

/// <param name="To">DEPOSITED, CLEARED, BOUNCED, CANCELLED or REPLACED (bounced and cancelled reverse the receipt).</param>
public sealed record MoveChequeRequest(string To, DateOnly? Date = null, string? Note = null, Guid? ReplacedByReceiptId = null);

public sealed record ReverseReceiptRequest(string Reason);

public sealed record ReverseReceiptResponse(Guid ApprovalRequestId, string Message);
