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

// Retention (spec section 22, D-044)

/// <param name="Enabled">Retention is switched on in this server's configuration (off by default).</param>
/// <param name="KeepFrom">The first month always kept (the previous financial year's April); only earlier months may be removed.</param>
/// <param name="Months">The months older than that, oldest first.</param>
public sealed record RetentionDto(
    bool Enabled, string KeepFrom, bool ArchiveRegistered, string? ReceiptKeyId, DateTimeOffset? ReceiptKeySetAtUtc, IReadOnlyList<RetentionMonthDto> Months);

public sealed record RetentionMonthDto(string Month, bool Locked, bool Packaged, DateTimeOffset? ReceiptRegisteredAtUtc, DateTimeOffset? ArchiveApprovedAtUtc);

public sealed record RetentionCheckDto(string Code, string Description, bool Passed, string? Detail);

public sealed record RetentionBlockerDto(string Code, string Description, long Count, IReadOnlyList<string> Examples);

public sealed record RetentionCountDto(string Dataset, long Records);

/// <summary>What removing a month would do (a dry run): its gates, what stops it, what would go and what would stay.</summary>
public sealed record RetentionPlanDto(
    string Month, bool Ready, IReadOnlyList<RetentionCheckDto> Checks, IReadOnlyList<RetentionBlockerDto> Blockers, IReadOnlyList<RetentionCountDto> Removed,
    IReadOnlyList<RetentionCountDto> Kept, string Note);

public sealed record SetReceiptKeyRequest(string PublicKeyPem);
