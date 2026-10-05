using Microsoft.Extensions.Options;
using SupermarketBilling.Infrastructure.Reporting;

namespace SupermarketBilling.UnitTests.Reporting;

public sealed class BackupStatusReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sb-backup-status-" + Guid.NewGuid().ToString("N"));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public BackupStatusReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private BackupStatusReader Reader(string? folder = null) => new(Options.Create(new BackupOptions { Directory = folder ?? _folder }), new FixedClock(Now));

    private string Backup(string name, DateTimeOffset made, string? report = null)
    {
        var path = Path.Combine(_folder, name + ".sbbak");
        File.WriteAllBytes(path, new byte[1234]);
        File.SetLastWriteTimeUtc(path, made.UtcDateTime);
        if (report is not null)
        {
            File.WriteAllText(path + ".restore-test.json", report);
        }

        return path;
    }

    [Fact]
    public void No_folder_or_no_backup_is_reported_plainly()
    {
        Assert.Equal("NONE", Reader(Path.Combine(_folder, "missing")).Read().Status);
        Assert.Equal("NONE", Reader().Read().Status);
    }

    [Fact]
    public void A_recent_restore_tested_backup_is_ok_and_its_details_are_read_without_opening_it()
    {
        Backup("supermarketbilling_20261003T010000Z", Now.AddDays(-2), """{ "passed": true, "testedUtc": "2026-10-03T01:10:00Z" }""");
        Backup("supermarketbilling_20261005T010000Z", Now.AddHours(-5), """{ "passed": true, "testedUtc": "2026-10-05T01:10:00Z" }""");
        var status = Reader().Read();
        Assert.Equal(("OK", "supermarketbilling_20261005T010000Z.sbbak", 1234L, true, 2), (status.Status, status.LatestFile, status.LatestBytes, status.RestoreTestPassed, status.BackupCount));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 1, 10, 0, TimeSpan.Zero), status.RestoreTestedUtc);
    }

    [Theory]
    [InlineData(-30, """{ "passed": true }""", "WARNING")]
    [InlineData(-2, null, "WARNING")]
    [InlineData(-2, """{ "passed": false, "problems": ["rows differ"] }""", "FAILED")]
    [InlineData(-2, "not json", "FAILED")]
    public void Old_untested_or_failed_backups_need_attention(int hoursAgo, string? report, string expected)
    {
        Backup("supermarketbilling_x", Now.AddHours(hoursAgo), report);
        Assert.Equal(expected, Reader().Read().Status);
    }
}
