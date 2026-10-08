using System.Globalization;
using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Archiving;

/// <summary>What archive users may do (the archive server's own permissions, separate from the store server's).</summary>
public static class ArchivePermissions
{
    public const string UsersManage = "archive.users.manage";
    public const string UsersView = "archive.users.view";
    public const string UsersUnlock = "archive.users.unlock";
    public const string SourcesManage = "archive.sources.manage";
    public const string SourcesView = "archive.sources.view";
    public const string Import = "archive.import";

    /// <summary>The accountant's approval of an imported month (step 10 of the monthly archival).</summary>
    public const string ApproveAccounts = "archive.approve.accounts";

    /// <summary>The owner's approval, after the accountant's.</summary>
    public const string ApproveOwner = "archive.approve.owner";

    public const string Reports = "archive.reports";
    public const string ReportsProfit = "archive.reports.profit";
    public const string Audit = "archive.audit";
    public const string Diagnostics = "archive.diagnostics";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        UsersManage, UsersView, UsersUnlock, SourcesManage, SourcesView, Import, ApproveAccounts, ApproveOwner, Reports, ReportsProfit, Audit, Diagnostics,
    };
}

public sealed record ArchiveRoleDefinition(string Code, string Name, IReadOnlySet<string> Permissions);

/// <summary>The archive roles of the specification (section 22).</summary>
public static class ArchiveRoles
{
    public const string Owner = "archive_owner";
    public const string Manager = "archive_manager";
    public const string Accountant = "archive_accountant";
    public const string Auditor = "archive_auditor";
    public const string ReportUser = "archive_report_user";
    public const string Support = "archive_support";

    private static readonly Dictionary<string, ArchiveRoleDefinition> Definitions = new(StringComparer.Ordinal)
    {
        [Owner] = new(Owner, "Owner administrator", ArchivePermissions.All),
        [Manager] = new(Manager, "Archive manager", Set(
            ArchivePermissions.UsersView, ArchivePermissions.SourcesView, ArchivePermissions.Import, ArchivePermissions.Reports)),
        [Accountant] = new(Accountant, "Accountant", Set(
            ArchivePermissions.SourcesView, ArchivePermissions.ApproveAccounts, ArchivePermissions.Reports, ArchivePermissions.ReportsProfit)),
        [Auditor] = new(Auditor, "Auditor", Set(
            ArchivePermissions.SourcesView, ArchivePermissions.Reports, ArchivePermissions.ReportsProfit, ArchivePermissions.Audit)),
        [ReportUser] = new(ReportUser, "Report user", Set(ArchivePermissions.Reports)),
        [Support] = new(Support, "Restricted support administrator", Set(ArchivePermissions.UsersUnlock, ArchivePermissions.Diagnostics)),
    };

    public static IReadOnlyCollection<ArchiveRoleDefinition> All => Definitions.Values;

    public static ArchiveRoleDefinition Get(string code) =>
        Definitions.TryGetValue(code ?? string.Empty, out var role) ? role : throw new DomainException("archive_role.unknown", $"Unknown archive role '{code}'.");

    private static HashSet<string> Set(params string[] permissions) => new(permissions, StringComparer.Ordinal);
}

/// <summary>Financial years run April to March: 2026 is 2026-27 (1 April 2026 to 31 March 2027).</summary>
public static class FinancialYears
{
    public static int Of(DateOnly day) => day.Month >= 4 ? day.Year : day.Year - 1;

    public static string Label(int year) => string.Create(CultureInfo.InvariantCulture, $"{year}-{(year + 1) % 100:00}");
}

/// <summary>
/// A role on the archive server, optionally limited to one business, one store, one financial year and some reports
/// (spec: "enforce business, store, financial-year and report permissions"). Never deleted: revoking keeps the history.
/// </summary>
public sealed partial class ArchiveGrant : ITenantOwned
{
    private ArchiveGrant()
    {
        RoleCode = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string RoleCode { get; private set; }

    /// <summary>Null: every archived business.</summary>
    public Guid? BusinessId { get; private set; }

    /// <summary>Null: every store (needs a business).</summary>
    public Guid? StoreId { get; private set; }

    /// <summary>The financial year it covers (2026 = 2026-27); null: all years.</summary>
    public int? FinancialYear { get; private set; }

    /// <summary>Report keys it may run, comma-separated; null: all reports.</summary>
    public string? Reports { get; private set; }

    public Guid? GrantedByUserId { get; private set; }

    public DateTimeOffset GrantedAtUtc { get; private set; }

    public Guid? RevokedByUserId { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static ArchiveGrant Grant(
        Guid userId, string roleCode, Guid? businessId, Guid? storeId, int? financialYear, IReadOnlyCollection<string>? reports, Guid? grantedBy, DateTimeOffset now)
    {
        var role = ArchiveRoles.Get(roleCode);
        if (storeId is not null && businessId is null)
        {
            throw new DomainException("archive_grant.store_needs_business", "A grant limited to a store must name the store's business.");
        }

        if (financialYear is { } year && (year < 2000 || year > 2100))
        {
            throw new DomainException("archive_grant.year_invalid", "Give the financial year as its first year, for example 2026 for 2026-27.");
        }

        string? list = null;
        if (reports is { Count: > 0 })
        {
            var keys = reports.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (keys.Any(k => !ReportKeyPattern().IsMatch(k)))
            {
                throw new DomainException("archive_grant.reports_invalid", "Report keys are lower-case letters, digits and hyphens.");
            }

            list = keys.Count == 0 ? null : string.Join(',', keys);
        }

        return new ArchiveGrant
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            RoleCode = role.Code,
            BusinessId = businessId,
            StoreId = storeId,
            FinancialYear = financialYear,
            Reports = list,
            GrantedByUserId = grantedBy,
            GrantedAtUtc = now,
        };
    }

    public void Revoke(Guid by, DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new DomainException("archive_grant.revoked", "This role has already been revoked.");
        }

        RevokedByUserId = by;
        RevokedAtUtc = now;
    }

    /// <summary>Whether this grant gives the permission for a business (and, when given, a store, a date and a report).</summary>
    public bool Covers(string permission, Guid? businessId, Guid? storeId = null, DateOnly? day = null, string? report = null) =>
        IsActive
        && ArchiveRoles.Get(RoleCode).Permissions.Contains(permission)
        && (BusinessId is null || BusinessId == businessId)
        && (StoreId is null || StoreId == storeId)
        && (FinancialYear is null || day is null || FinancialYears.Of(day.Value) == FinancialYear)
        && (Reports is null || report is null || Reports.Split(',').Contains(report, StringComparer.Ordinal));

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$")]
    private static partial Regex ReportKeyPattern();
}

/// <summary>A store server whose packages this archive accepts: its signing public key, registered by the owner administrator.</summary>
public sealed class ArchiveSource : ITenantOwned
{
    private ArchiveSource()
    {
        Name = KeyId = PublicKeyPem = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string KeyId { get; private set; }

    public string PublicKeyPem { get; private set; }

    public Guid RegisteredByUserId { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? RevokedByUserId { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static ArchiveSource Register(string name, string keyId, string publicKeyPem, Guid by, DateTimeOffset now)
    {
        var trimmed = name?.Trim();
        return new ArchiveSource
        {
            Id = Guid.CreateVersion7(now),
            Name = string.IsNullOrEmpty(trimmed) || trimmed.Length > 100
                ? throw new DomainException("archive_source.name_required", "Name the store server (max 100 characters).")
                : trimmed,
            KeyId = keyId,
            PublicKeyPem = publicKeyPem,
            RegisteredByUserId = by,
            RegisteredAtUtc = now,
        };
    }

    /// <summary>Stops accepting new packages from it; what was imported stays.</summary>
    public void Revoke(Guid by, DateTimeOffset now)
    {
        RevokedAtUtc ??= now;
        RevokedByUserId ??= by;
    }
}

public static class ArchiveImportStatus
{
    /// <summary>Opened, every checksum, count and total verified, and stored; waiting for the accountant.</summary>
    public const string Verified = "VERIFIED";

    /// <summary>The accountant approved; waiting for the owner.</summary>
    public const string AccountantApproved = "ACCOUNTANT_APPROVED";

    /// <summary>Accountant and owner approved (spec step 10).</summary>
    public const string Approved = "APPROVED";

    public static readonly IReadOnlyList<string> All = [Verified, AccountantApproved, Approved];
}

/// <summary>
/// One month of one business, imported from a package: where it came from, what it held (its manifest), how it was
/// verified, and the accountant's and the owner's approvals (by two different people). Imported records never change.
/// </summary>
public sealed class ArchiveImport : ITenantOwned
{
    private ArchiveImport()
    {
        BusinessCode = BusinessName = FileSha256 = Manifest = Verification = Status = string.Empty;
    }

    /// <summary>The package id (the same import of the same package is recognised by it).</summary>
    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string BusinessCode { get; private set; }

    public string BusinessName { get; private set; }

    /// <summary>First day of the month.</summary>
    public DateOnly Month { get; private set; }

    public Guid SourceId { get; private set; }

    public string FileSha256 { get; private set; }

    public long FileSize { get; private set; }

    public string Manifest { get; private set; }

    /// <summary>The checks made after storing (JSON): counts and totals recomputed from the archive's own records.</summary>
    public string Verification { get; private set; }

    public Guid ImportedByUserId { get; private set; }

    public DateTimeOffset ImportedAtUtc { get; private set; }

    public string Status { get; private set; }

    public Guid? AccountantApprovedByUserId { get; private set; }

    public DateTimeOffset? AccountantApprovedAtUtc { get; private set; }

    public string? AccountantNote { get; private set; }

    public Guid? OwnerApprovedByUserId { get; private set; }

    public DateTimeOffset? OwnerApprovedAtUtc { get; private set; }

    public string? OwnerNote { get; private set; }

    public uint RowVersion { get; private set; }

    public static ArchiveImport Record(
        Guid packageId, Guid businessId, string businessCode, string businessName, DateOnly month, Guid sourceId, string fileSha256, long fileSize,
        string manifest, string verification, Guid importedBy, DateTimeOffset now) => new()
    {
        Id = packageId,
        BusinessId = businessId,
        BusinessCode = businessCode,
        BusinessName = businessName,
        Month = month,
        SourceId = sourceId,
        FileSha256 = fileSha256,
        FileSize = fileSize,
        Manifest = manifest,
        Verification = verification,
        ImportedByUserId = importedBy,
        ImportedAtUtc = now,
        Status = ArchiveImportStatus.Verified,
    };

    public void ApproveAsAccountant(Guid by, string? note, DateTimeOffset now)
    {
        if (Status != ArchiveImportStatus.Verified)
        {
            throw new DomainException("archive_import.not_waiting_for_accountant", "This month is not waiting for the accountant's approval.");
        }

        AccountantApprovedByUserId = by;
        AccountantApprovedAtUtc = now;
        AccountantNote = Note(note);
        Status = ArchiveImportStatus.AccountantApproved;
    }

    public void ApproveAsOwner(Guid by, string? note, DateTimeOffset now)
    {
        if (Status != ArchiveImportStatus.AccountantApproved)
        {
            throw new DomainException("archive_import.not_waiting_for_owner", "The accountant approves first; then the owner.");
        }

        if (by == AccountantApprovedByUserId)
        {
            throw new DomainException("archive_import.same_approver", "The owner's approval must come from a different person than the accountant's.");
        }

        OwnerApprovedByUserId = by;
        OwnerApprovedAtUtc = now;
        OwnerNote = Note(note);
        Status = ArchiveImportStatus.Approved;
    }

    private static string? Note(string? note)
    {
        var trimmed = note?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= 300 ? trimmed : throw new DomainException("archive_import.note_too_long", "A note is at most 300 characters.");
    }
}

/// <summary>A record of an archived month, exactly as the store server held it (JSON). Never changed or removed.</summary>
public sealed class ArchiveRecord : ITenantOwned
{
    private ArchiveRecord()
    {
        Dataset = RecordId = Data = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ImportId { get; private set; }

    public Guid BusinessId { get; private set; }

    public DateOnly Month { get; private set; }

    public string Dataset { get; private set; }

    /// <summary>The record's id on the store server.</summary>
    public string RecordId { get; private set; }

    public string Data { get; private set; }

    public static ArchiveRecord Create(Guid importId, Guid businessId, DateOnly month, string dataset, string recordId, string data, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ImportId = importId,
        BusinessId = businessId,
        Month = month,
        Dataset = dataset,
        RecordId = recordId,
        Data = data,
    };
}

/// <summary>Master data (stores, products, parties, users...) as the latest imported package carried it.</summary>
public sealed class ArchiveMaster : ITenantOwned
{
    private ArchiveMaster()
    {
        Dataset = RecordId = Data = string.Empty;
    }

    public Guid BusinessId { get; private set; }

    public string Dataset { get; private set; }

    public string RecordId { get; private set; }

    public string Data { get; private set; }

    public Guid ImportId { get; private set; }

    public DateOnly Month { get; private set; }

    public static ArchiveMaster Create(Guid businessId, string dataset, string recordId, string data, Guid importId, DateOnly month) => new()
    {
        BusinessId = businessId,
        Dataset = dataset,
        RecordId = recordId,
        Data = data,
        ImportId = importId,
        Month = month,
    };

    /// <summary>A newer month's package brings the master as it is now.</summary>
    public void Refresh(string data, Guid importId, DateOnly month)
    {
        if (month < Month)
        {
            return;
        }

        Data = data;
        ImportId = importId;
        Month = month;
    }
}
