namespace SupermarketBilling.Application.Contracts;

/// <param name="Month">YYYY-MM.</param>
/// <param name="State">IN_PROGRESS (not over yet), OPEN (over, not locked), LOCKED.</param>
public sealed record MonthStatusDto(
    string Month, string State, DateTimeOffset? LockedAtUtc, string? LockedBy, int Packages, DateTimeOffset? LastPackageAtUtc, string? LastPackageSha256);

/// <param name="Problems">How many records stop the month from being locked (0 when it passes).</param>
public sealed record MonthCheckDto(string Key, string Title, bool Passed, long Problems, string Detail);

public sealed record MonthChecksDto(string Month, bool Ready, IReadOnlyList<MonthCheckDto> Checks);

public sealed record LockMonthRequest(string? Note);

/// <summary>This installation's signing key (to register on the archive server) and the archive packages are encrypted for.</summary>
public sealed record ArchiveKeysDto(string SignerKeyId, string SignerPublicKeyPem, string? RecipientKeyId, DateTimeOffset? RecipientSetAtUtc, uint? RecipientRowVersion);

public sealed record SetArchiveRecipientRequest(string PublicKeyPem, uint? RowVersion);

public sealed record MonthPackageDto(Guid Id, string Month, string FileSha256, long FileSize, string RecipientKeyId, string CreatedBy, DateTimeOffset CreatedAtUtc);
