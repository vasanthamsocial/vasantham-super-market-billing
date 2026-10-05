namespace SupermarketBilling.Application.Contracts;

public sealed record CollectionDeviceDto(
    Guid Id, Guid CollectorUserId, string Collector, string Name, decimal OfflineLimit, int MaxOfflineHours, long LastSequence, string EnrolledBy,
    DateTimeOffset EnrolledAtUtc, DateTimeOffset? LastSyncedAtUtc, bool IsActive, uint RowVersion);

/// <param name="OfflineLimit">Most the phone may hold in collections not yet synchronised (0: no offline collection).</param>
/// <param name="MaxOfflineHours">Longest a collection may stay on the phone before it must be synchronised.</param>
public sealed record EnrolCollectionDeviceRequest(Guid CollectorUserId, string Name, decimal OfflineLimit, int MaxOfflineHours);

public sealed record CollectionDeviceLimitsRequest(decimal OfflineLimit, int MaxOfflineHours, uint RowVersion);

/// <summary>What the Collection App keeps to work offline: whose phone it is and its limits.</summary>
public sealed record MyCollectionDeviceDto(Guid DeviceId, Guid BusinessId, Guid CollectorUserId, string Name, decimal OfflineLimit, int MaxOfflineHours, long LastSequence);

/// <param name="Id">Made on the phone (a UUID); also the receipt's idempotency key.</param>
/// <param name="Sequence">1, 2, 3... per phone, in the order the collections were recorded.</param>
/// <param name="RecordedAtUtc">When the collector recorded it, by the phone's clock.</param>
public sealed record OfflineItemRequest(
    Guid Id, long Sequence, Guid DebtorId, string Method, decimal Amount, DateTimeOffset RecordedAtUtc, string? Reference = null, string? Note = null,
    string? BankName = null, DateOnly? ChequeDate = null);

public sealed record OfflineSyncRequest(IReadOnlyList<OfflineItemRequest> Items);

/// <param name="Status">ACCEPTED (posted), QUARANTINED (waits for a manager), DUPLICATE (already received: its first result), REJECTED (not kept) or NOT_PROCESSED (after a gap; send again).</param>
public sealed record OfflineItemResult(Guid Id, long Sequence, string Status, string? Reason, string? ReceiptNumber, decimal? BalanceAfter);

public sealed record OfflineSyncResponse(IReadOnlyList<OfflineItemResult> Results, long LastSequence);

public sealed record OfflineSubmissionDto(
    Guid Id, Guid DeviceId, string Device, long Sequence, Guid CollectorUserId, string Collector, Guid DebtorId, string Debtor, string Method, decimal Amount,
    string? Reference, string? Note, DateTimeOffset RecordedAtUtc, DateTimeOffset ReceivedAtUtc, string Status, string? Reason, string? ReceiptNumber,
    string? ResolvedBy, DateTimeOffset? ResolvedAtUtc, string? ResolutionNote, uint RowVersion);

/// <param name="Accept">True: post it as a receipt at <paramref name="StoreId"/>; false: not posted.</param>
public sealed record ResolveOfflineRequest(bool Accept, string Note, Guid? StoreId = null, uint RowVersion = 0);
