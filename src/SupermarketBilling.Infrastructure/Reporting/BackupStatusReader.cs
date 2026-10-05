using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Contracts;

namespace SupermarketBilling.Infrastructure.Reporting;

public sealed class BackupOptions
{
    public const string SectionName = "Backups";

    /// <summary>The folder sb-backup writes to on this server; when empty, SB_BACKUP_DIR (the backup tool's own setting) is used.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>A backup older than this is a warning.</summary>
    public int MaxAgeHours { get; set; } = 26;
}

/// <summary>
/// Backup status for the dashboard, read from the backup folder: file names, sizes and times of the encrypted backups,
/// and the restore-test reports sb-backup writes next to them. Backups are never opened or decrypted here.
/// </summary>
public sealed class BackupStatusReader(IOptions<BackupOptions> options, TimeProvider clock)
{
    private const string BackupExtension = ".sbbak";
    private const string ReportExtension = ".restore-test.json";

    public BackupStatusDto Read()
    {
        var directory = string.IsNullOrWhiteSpace(options.Value.Directory) ? Environment.GetEnvironmentVariable("SB_BACKUP_DIR") : options.Value.Directory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new BackupStatusDto("NOT_CONFIGURED", null, null, null, null, null, 0, "The backup folder is not set for this server (SB_BACKUP_DIR).");
        }

        if (!System.IO.Directory.Exists(directory))
        {
            return new BackupStatusDto("NONE", null, null, null, null, null, 0, "The backup folder does not exist yet: no backup has been made.");
        }

        var backups = new DirectoryInfo(directory).GetFiles("*" + BackupExtension).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        if (backups.Count == 0)
        {
            return new BackupStatusDto("NONE", null, null, null, null, null, 0, "No backup has been made.");
        }

        var latest = backups[0];
        var made = new DateTimeOffset(latest.LastWriteTimeUtc, TimeSpan.Zero);
        var (passed, tested) = ReadReport(latest.FullName + ReportExtension);
        var age = clock.GetUtcNow() - made;
        var (status, message) = passed == false
            ? ("FAILED", "The restore test of the latest backup failed; see its report and make a new backup.")
            : age > TimeSpan.FromHours(options.Value.MaxAgeHours)
                ? ("WARNING", $"The latest backup is {Math.Floor(age.TotalHours).ToString(CultureInfo.InvariantCulture)} hours old.")
                : passed is null
                    ? ("WARNING", "The latest backup has not been restore-tested yet.")
                    : ("OK", "The latest backup is recent and its restore test passed.");
        return new BackupStatusDto(status, latest.Name, made, latest.Length, passed, tested, backups.Count, message);
    }

    private static (bool? Passed, DateTimeOffset? Tested) ReadReport(string path)
    {
        if (!File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            using var report = JsonDocument.Parse(File.ReadAllText(path));
            var root = report.RootElement;
            return (root.TryGetProperty("passed", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : false,
                root.TryGetProperty("testedUtc", out var t) && t.TryGetDateTimeOffset(out var at) ? at : null);
        }
        catch (JsonException)
        {
            // An unreadable report counts as a failed test: the backup has not been proven.
            return (false, null);
        }
    }
}
