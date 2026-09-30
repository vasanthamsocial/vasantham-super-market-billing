using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SupermarketBilling.Application.Auditing;
using SupermarketBilling.BackupTool;
using SupermarketBilling.BackupTool.Format;
using SupermarketBilling.BackupTool.Postgres;
using SupermarketBilling.Domain.Auditing;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Backup;

/// <summary>
/// Real pg_dump / pg_restore round trips against the test database, through the same code path the scripts use.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BackupRestoreTests(ApiFactory factory) : IDisposable
{
    private string TestDatabase => factory.DatabaseName;
    private readonly string _outputDirectory = Directory.CreateTempSubdirectory("sb-backup-test-").FullName;
    private readonly List<string> _databasesToDrop = [];

    [Fact]
    public async Task Backup_restores_with_identical_row_counts_and_passes_verification()
    {
        await AppendAuditEventAsync();
        var service = CreateService();

        var backup = await service.CreateBackupAsync(TestDatabase, _outputDirectory, CancellationToken.None);
        var report = await service.RestoreTestAsync(backup.FilePath, keepDatabase: false, CancellationToken.None);

        Assert.True(report.Passed, string.Join("; ", report.Problems));
        Assert.True(backup.Manifest.TableRowCounts["audit_events"] > 0);
        Assert.Contains(backup.Manifest.Migrations, m => m.EndsWith("_InitialFoundation", StringComparison.Ordinal));
        Assert.True(File.Exists(backup.FilePath + BackupService.ChecksumExtension));
        Assert.True(File.Exists(backup.FilePath + BackupService.RestoreReportExtension));
        Assert.DoesNotContain(await ListDatabasesAsync(), name => name.StartsWith("sb_restoretest_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_file_contains_no_readable_database_content()
    {
        await AppendAuditEventAsync("test.backup.confidential-marker");
        var backup = await CreateService().CreateBackupAsync(TestDatabase, _outputDirectory, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(backup.FilePath);

        Assert.Equal(-1, bytes.AsSpan().IndexOf("confidential-marker"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("audit_events"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("PGDMP"u8));
    }

    [Fact]
    public async Task Modified_backup_is_rejected_by_checksum_and_by_decryption()
    {
        var service = CreateService();
        var backup = await service.CreateBackupAsync(TestDatabase, _outputDirectory, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(backup.FilePath);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(backup.FilePath, bytes);

        var checksumError = await Assert.ThrowsAsync<BackupIntegrityException>(() => service.VerifyAsync(backup.FilePath, CancellationToken.None));
        Assert.Contains("SHA-256 mismatch", checksumError.Message, StringComparison.Ordinal);

        // Even if an attacker also rewrites the checksum file, authenticated encryption still rejects the change.
        var newHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        await File.WriteAllTextAsync(backup.FilePath + BackupService.ChecksumExtension, $"{newHash}  x\n");
        var cryptoError = await Assert.ThrowsAsync<BackupIntegrityException>(() => service.VerifyAsync(backup.FilePath, CancellationToken.None));
        Assert.Contains("failed authentication", cryptoError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_cannot_be_read_with_a_different_passphrase()
    {
        var backup = await CreateService().CreateBackupAsync(TestDatabase, _outputDirectory, CancellationToken.None);

        var other = CreateService("a completely different backup passphrase");

        await Assert.ThrowsAsync<BackupPassphraseException>(() => other.VerifyAsync(backup.FilePath, CancellationToken.None));
    }

    [Fact]
    public async Task Restore_into_existing_database_requires_replace_and_keeps_the_previous_copy()
    {
        var service = CreateService();
        var backup = await service.CreateBackupAsync(TestDatabase, _outputDirectory, CancellationToken.None);
        var target = $"sbit_restore_{Guid.NewGuid():N}"[..28];
        _databasesToDrop.Add(target);

        await service.RestoreAsync(backup.FilePath, target, replaceExisting: false, CancellationToken.None);
        var refused = await Assert.ThrowsAsync<BackupException>(() => service.RestoreAsync(backup.FilePath, target, replaceExisting: false, CancellationToken.None));
        Assert.Contains("already exists", refused.Message, StringComparison.Ordinal);

        await service.RestoreAsync(backup.FilePath, target, replaceExisting: true, CancellationToken.None);

        var databases = await ListDatabasesAsync();
        Assert.Contains(target, databases);
        var previous = Assert.Single(databases, name => name.StartsWith(target + "_pre_restore_", StringComparison.Ordinal));
        _databasesToDrop.Add(previous);
        Assert.DoesNotContain(databases, name => name.Contains("_restoring_", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(Settings().AdminConnectionString("postgres"));
        connection.Open();
        foreach (var database in _databasesToDrop)
        {
            using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
            command.ExecuteNonQuery();
        }

        Directory.Delete(_outputDirectory, recursive: true);
    }

    private static BackupSettings Settings(string passphrase = "integration test backup passphrase") => new(
        TestSettings.Get("SB_DB_HOST"),
        int.Parse(TestSettings.Get("SB_DB_PORT"), System.Globalization.CultureInfo.InvariantCulture),
        TestSettings.Get("SB_DB_SUPERUSER"),
        TestSettings.Get("SB_DB_SUPERUSER_PASSWORD"),
        TestSettings.Get("SB_DB_MIGRATOR_USER"),
        TestSettings.Get("SB_DB_APP_USER"),
        TestSettings.Get("SB_DB_CONTAINER"),
        passphrase,
        Path.Combine(TestSettings.RepoRoot, "database", "verification"));

    private static BackupService CreateService(string? passphrase = null)
    {
        var settings = passphrase is null ? Settings() : Settings(passphrase);
        return new BackupService(settings, new DockerPostgresTools(settings.Container, settings.SuperUser), TimeProvider.System, TextWriter.Null);
    }

    private async Task AppendAuditEventAsync(string eventType = "test.backup")
    {
        await using var scope = await factory.CreateTenantScopeAsync();
        await scope.ServiceProvider.GetRequiredService<IAuditTrail>()
            .AppendAsync(AuditEvent.Create(DateTimeOffset.UtcNow, eventType), CancellationToken.None);
    }

    private static async Task<List<string>> ListDatabasesAsync()
    {
        await using var connection = new NpgsqlConnection(Settings().AdminConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT datname FROM pg_database", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
