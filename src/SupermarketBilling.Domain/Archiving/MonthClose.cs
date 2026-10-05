using System.Globalization;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Archiving;

/// <summary>Calendar months as the business keeps them (<c>2026-09</c>).</summary>
public static class Months
{
    public static DateOnly Parse(string month)
    {
        if (string.IsNullOrWhiteSpace(month)
            || !DateOnly.TryParseExact(month.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        {
            throw new DomainException("month.invalid", "Give the month as YYYY-MM, for example 2026-09.");
        }

        return first;
    }

    public static string Format(DateOnly first) => first.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static DateOnly First(DateOnly day) => new(day.Year, day.Month, 1);
}

/// <summary>
/// A closed month (spec section 22): once every check passed, nothing dated in it can be posted, changed or removed
/// (database triggers enforce this). A lock is never undone.
/// </summary>
public sealed class MonthLock : ITenantOwned
{
    private MonthLock()
    {
        Checks = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    /// <summary>The first day of the month.</summary>
    public DateOnly Month { get; private set; }

    public Guid LockedByUserId { get; private set; }

    public DateTimeOffset LockedAtUtc { get; private set; }

    public string? Note { get; private set; }

    /// <summary>The checks as they passed (JSON), kept with the lock.</summary>
    public string Checks { get; private set; }

    public static MonthLock Lock(Guid businessId, DateOnly month, DateOnly today, string checks, string? note, Guid by, DateTimeOffset now)
    {
        if (month.Day != 1)
        {
            throw new DomainException("month.invalid", "A month lock starts on the first day of the month.");
        }

        if (month.AddMonths(1) > today)
        {
            throw new DomainException("month.not_over", $"{Months.Format(month)} is not over yet.");
        }

        var trimmed = note?.Trim();
        return new MonthLock
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Month = month,
            LockedByUserId = by,
            LockedAtUtc = now,
            Note = string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= 300 ? trimmed : throw new DomainException("month.note_too_long", "The note is at most 300 characters."),
            Checks = checks,
        };
    }
}

/// <summary>An archive package made for a locked month: what it holds (its manifest) and its checksum, for the archive to confirm.</summary>
public sealed class MonthPackage : ITenantOwned
{
    private MonthPackage()
    {
        FileSha256 = SignerKeyId = RecipientKeyId = Manifest = string.Empty;
    }

    /// <summary>Also the package id inside the file.</summary>
    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public DateOnly Month { get; private set; }

    public string FileSha256 { get; private set; }

    public long FileSize { get; private set; }

    public string SignerKeyId { get; private set; }

    public string RecipientKeyId { get; private set; }

    /// <summary>The package's manifest (JSON): datasets, record counts, totals and checksums.</summary>
    public string Manifest { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static MonthPackage Record(
        Guid id, Guid businessId, DateOnly month, string fileSha256, long fileSize, string signerKeyId, string recipientKeyId, string manifest, Guid by,
        DateTimeOffset now) => new()
    {
        Id = id,
        BusinessId = businessId,
        Month = month,
        FileSha256 = fileSha256,
        FileSize = fileSize,
        SignerKeyId = signerKeyId,
        RecipientKeyId = recipientKeyId,
        Manifest = manifest,
        CreatedByUserId = by,
        CreatedAtUtc = now,
    };
}

/// <summary>The archive server packages are encrypted for: its public key, registered by an owner from the archive's screen.</summary>
public sealed class ArchiveRecipient : ITenantOwned
{
    private ArchiveRecipient()
    {
        PublicKeyPem = KeyId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string PublicKeyPem { get; private set; }

    public string KeyId { get; private set; }

    public Guid SetByUserId { get; private set; }

    public DateTimeOffset SetAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static ArchiveRecipient Register(Guid businessId, string publicKeyPem, string keyId, Guid by, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        PublicKeyPem = publicKeyPem,
        KeyId = keyId,
        SetByUserId = by,
        SetAtUtc = now,
    };

    public void Replace(string publicKeyPem, string keyId, Guid by, DateTimeOffset now)
    {
        PublicKeyPem = publicKeyPem;
        KeyId = keyId;
        SetByUserId = by;
        SetAtUtc = now;
    }
}
