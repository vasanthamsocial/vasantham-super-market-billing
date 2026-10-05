using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.Application.Contracts;

/// <summary>Lets a counter device bill without the server within these limits; all empty stops it (D-039).</summary>
public sealed record CounterOfflineRequest(int? MaxBills, decimal? MaxAmount, int? MaxHours);

/// <summary>Bills a counter agent issued without the server, oldest first.</summary>
public sealed record OfflineBillsSyncRequest(IReadOnlyList<OfflineBill> Bills);

/// <param name="Status">POSTED (an invoice now), QUARANTINED (its number is used; a manager decides), DUPLICATE (received before: its first result), REJECTED (not from this counter's series or reusing a number) or NOT_PROCESSED (after a gap; send again).</param>
/// <param name="Review">On a posted bill: what a manager should check (for example a price no longer in the price list).</param>
public sealed record OfflineBillResult(Guid Id, string Number, string Status, string? Reason, string? Review, Guid? InvoiceId);

public sealed record OfflineBillsSyncResponse(IReadOnlyList<OfflineBillResult> Results);

public sealed record OfflineBillDto(
    Guid Id, string Number, Guid CounterId, string CounterCode, string Device, string Cashier, DateTimeOffset IssuedAtUtc, DateTimeOffset ReceivedAtUtc,
    decimal GrandTotal, string Status, string? Reason, string? Review, string? ReviewedBy, DateTimeOffset? ReviewedAtUtc, Guid? InvoiceId, string? ResolvedBy,
    DateTimeOffset? ResolvedAtUtc, string? ResolutionNote, uint RowVersion);

/// <param name="Post">True: post it now as an invoice (in its cashier's open shift on that counter); false: record it as not posted.</param>
public sealed record ResolveOfflineBillRequest(bool Post, string Note, uint RowVersion);

public sealed record ReviewOfflineBillRequest(string Note, uint RowVersion);
