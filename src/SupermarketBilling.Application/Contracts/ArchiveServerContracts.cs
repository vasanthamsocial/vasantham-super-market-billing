namespace SupermarketBilling.Application.Contracts;

/// <summary>The archive server's first setup: its company and owner administrator, with the setup code from the server.</summary>
public sealed record ArchiveSetupRequest(string? SetupCode, string CompanyCode, string CompanyName, string OwnerUsername, string OwnerDisplayName, string OwnerPassword);

/// <param name="BusinessId">Null: every archived business.</param>
/// <param name="FinancialYear">First year of the financial year (2026 = 2026-27); null: all years.</param>
/// <param name="Reports">Report keys allowed; null or empty: all reports.</param>
public sealed record ArchiveGrantRequest(string RoleCode, Guid? BusinessId = null, Guid? StoreId = null, int? FinancialYear = null, IReadOnlyList<string>? Reports = null);

public sealed record ArchiveGrantDto(
    Guid Id, string RoleCode, string RoleName, Guid? BusinessId, string? BusinessName, Guid? StoreId, string? StoreName, int? FinancialYear, string? FinancialYearLabel,
    IReadOnlyList<string>? Reports);

public sealed record ArchiveMeDto(Guid UserId, string Username, string DisplayName, IReadOnlyList<ArchiveGrantDto> Grants, IReadOnlyList<string> Permissions);

public sealed record ArchiveRoleDto(string Code, string Name, IReadOnlyList<string> Permissions);

public sealed record ArchiveUserDto(
    Guid Id, string Username, string DisplayName, bool IsActive, bool IsLockedOut, bool MfaEnabled, bool MustChangePassword, DateTimeOffset? LastLoginAtUtc,
    IReadOnlyList<ArchiveGrantDto> Grants);

public sealed record CreateArchiveUserRequest(string Username, string DisplayName, string TemporaryPassword, ArchiveGrantRequest Grant);

public sealed record ArchiveSourceDto(Guid Id, string Name, string KeyId, string RegisteredBy, DateTimeOffset RegisteredAtUtc, bool IsActive, int Imports);

public sealed record RegisterArchiveSourceRequest(string Name, string PublicKeyPem);

/// <summary>This archive's public key: registered on each store server, which encrypts its packages for it.</summary>
public sealed record ArchiveServerKeysDto(string KeyId, string PublicKeyPem);

public sealed record ArchiveImportDatasetDto(string Name, long Records, IReadOnlyDictionary<string, decimal> Totals, bool Master);

/// <param name="AlreadyImported">True when the same month (identical records) was already archived: nothing was added.</param>
public sealed record ArchiveImportDto(
    Guid Id, Guid BusinessId, string BusinessCode, string BusinessName, string Month, string Source, string FileSha256, long FileSize, long Records,
    string ImportedBy, DateTimeOffset ImportedAtUtc, string Status, string? AccountantApprovedBy, DateTimeOffset? AccountantApprovedAtUtc, string? AccountantNote,
    string? OwnerApprovedBy, DateTimeOffset? OwnerApprovedAtUtc, string? OwnerNote, uint RowVersion, IReadOnlyList<ArchiveImportDatasetDto> Datasets,
    bool AlreadyImported = false);

public sealed record ArchiveApprovalRequest(string? Note, uint RowVersion);

public sealed record ArchiveStoreDto(Guid Id, string Code, string Name);

/// <summary>An archived business, with its stores and the months held.</summary>
public sealed record ArchiveBusinessDto(Guid Id, string Code, string Name, IReadOnlyList<ArchiveStoreDto> Stores, IReadOnlyList<string> Months);
