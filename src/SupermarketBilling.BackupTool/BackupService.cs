using System.Data;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using SupermarketBilling.BackupTool.Format;
using SupermarketBilling.BackupTool.Postgres;

namespace SupermarketBilling.BackupTool;

internal sealed record BackupResult(string FilePath, string Sha256, long FileBytes, BackupManifest Manifest);

internal sealed record VerifyResult(BackupManifest Manifest, long DumpBytes);

internal sealed record RestoreTestReport(
    bool Passed,
    DateTimeOffset TestedUtc,
    string BackupFile,
    string SourceDatabase,
    DateTimeOffset BackupCreatedUtc,
    int TablesCompared,
    long RowsCompared,
    long DumpBytes,
    IReadOnlyList<string> Problems);

internal sealed partial class BackupService(BackupSettings settings, IPostgresTools postgres, TimeProvider clock, TextWriter log)
{
    public const string BackupExtension = ".sbbak";
    public const string ChecksumExtension = ".sha256";
    public const string RestoreReportExtension = ".restore-test.json";

    private const string MaintenanceDatabase = "postgres";

    private static readonly string ToolVersion =
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Creates an encrypted backup of <paramref name="database"/>. Row counts and the dump come from one exported
    /// snapshot, so the manifest describes exactly what was dumped even while the system is in use.
    /// </summary>
    public async Task<BackupResult> CreateBackupAsync(string database, string outputDirectory, CancellationToken cancellationToken)
    {
        ValidateDatabaseName(database);
        BackupCrypto.ValidatePassphrase(settings.Passphrase);
        Directory.CreateDirectory(outputDirectory);

        var created = clock.GetUtcNow();
        var fileName = $"{database}_{created.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}{BackupExtension}";
        var finalPath = Path.GetFullPath(Path.Combine(outputDirectory, fileName));
        var partialPath = finalPath + ".partial";

        await using var connection = new NpgsqlConnection(settings.AdminConnectionString(database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
        var snapshotId = (string)(await ScalarAsync(connection, transaction, "SELECT pg_export_snapshot()", cancellationToken).ConfigureAwait(false))!;

        var manifest = new BackupManifest(
            "SupermarketBilling",
            ToolVersion,
            database,
            (string)(await ScalarAsync(connection, transaction, "SHOW server_version", cancellationToken).ConfigureAwait(false))!,
            created,
            await ReadMigrationsAsync(connection, transaction, cancellationToken).ConfigureAwait(false),
            await CountRowsAsync(connection, transaction, cancellationToken).ConfigureAwait(false));

        log.WriteLine($"Backing up '{database}' ({manifest.TableRowCounts.Count} tables, {manifest.TableRowCounts.Values.Sum()} rows) to {finalPath}");

        try
        {
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await postgres.DumpAsync(
                    database,
                    snapshotId,
                    async (dump, ct) =>
                    {
                        await using var payload = new PrefixedReadStream(BackupPayload.CreatePrefix(manifest), dump);
                        await BackupCrypto.EncryptAsync(payload, output, settings.Passphrase, created, ct).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            File.Move(partialPath, finalPath);
        }
        catch
        {
            // Only ever removes the incomplete file this run created.
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }

        var sha256 = await ComputeSha256Async(finalPath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(finalPath + ChecksumExtension, $"{sha256}  {fileName}\n", cancellationToken).ConfigureAwait(false);
        var bytes = new FileInfo(finalPath).Length;
        log.WriteLine($"Backup written: {bytes:N0} bytes, SHA-256 {sha256}");
        return new BackupResult(finalPath, sha256, bytes, manifest);
    }

    /// <summary>Checks the SHA-256 checksum file, then decrypts and authenticates the whole backup without restoring it.</summary>
    public async Task<VerifyResult> VerifyAsync(string backupPath, CancellationToken cancellationToken)
    {
        await VerifyChecksumAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var splitter = new PayloadSplitter(dumpSink: null);
        await using (var input = OpenBackup(backupPath))
        {
            await BackupCrypto.DecryptAsync(input, settings.Passphrase, splitter.WriteAsync, cancellationToken).ConfigureAwait(false);
        }

        var manifest = splitter.Complete();
        log.WriteLine($"Verified {Path.GetFileName(backupPath)}: database '{manifest.Database}' from {manifest.CreatedUtc:u}, " +
                      $"{manifest.TableRowCounts.Count} tables, dump {splitter.DumpBytes:N0} bytes. Checksum and encryption intact.");
        return new VerifyResult(manifest, splitter.DumpBytes);
    }

    /// <summary>
    /// Restores a backup into a temporary database, proves it matches the manifest and passes database verification,
    /// then drops the temporary database. Writes a JSON report next to the backup.
    /// </summary>
    public async Task<RestoreTestReport> RestoreTestAsync(string backupPath, bool keepDatabase, CancellationToken cancellationToken)
    {
        await VerifyChecksumAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var scratch = UniqueName("sb_restoretest");
        var problems = new List<string>();
        BackupManifest? manifest = null;
        long dumpBytes = 0;
        long rowsCompared = 0;

        try
        {
            await CreateDatabaseAsync(scratch, cancellationToken).ConfigureAwait(false);
            (manifest, dumpBytes) = await RestoreIntoAsync(backupPath, scratch, cancellationToken).ConfigureAwait(false);
            rowsCompared = await CompareWithManifestAsync(scratch, manifest, problems, cancellationToken).ConfigureAwait(false);
            await RunVerificationScriptsAsync(scratch, problems, cancellationToken).ConfigureAwait(false);
        }
        catch (BackupException ex)
        {
            problems.Add(ex.Message);
        }
        finally
        {
            if (!keepDatabase)
            {
                await DropScratchDatabaseAsync(scratch).ConfigureAwait(false);
            }
            else
            {
                log.WriteLine($"Keeping restored database '{scratch}' for inspection (--keep). Drop it when finished.");
            }
        }

        var report = new RestoreTestReport(
            problems.Count == 0,
            clock.GetUtcNow(),
            Path.GetFileName(backupPath),
            manifest?.Database ?? "unknown",
            manifest?.CreatedUtc ?? default,
            manifest?.TableRowCounts.Count ?? 0,
            rowsCompared,
            dumpBytes,
            problems);
        await File.WriteAllTextAsync(backupPath + RestoreReportExtension, JsonSerializer.Serialize(report, ReportJson), cancellationToken).ConfigureAwait(false);

        if (report.Passed)
        {
            log.WriteLine($"Restore test PASSED: {report.TablesCompared} tables and {report.RowsCompared} rows match; verification checks passed.");
        }
        else
        {
            log.WriteLine("Restore test FAILED:");
            foreach (var problem in problems)
            {
                log.WriteLine($"  - {problem}");
            }
        }

        return report;
    }

    /// <summary>
    /// Restores a backup as <paramref name="targetDatabase"/>. The backup is restored and verified in a staging
    /// database first. An existing target is renamed (never dropped), so the previous data remains available.
    /// </summary>
    public async Task RestoreAsync(string backupPath, string targetDatabase, bool replaceExisting, CancellationToken cancellationToken)
    {
        ValidateDatabaseName(targetDatabase);
        if (targetDatabase.Length > 40)
        {
            throw new BackupException("Target database names longer than 40 characters are not supported for restore.");
        }

        await VerifyChecksumAsync(backupPath, cancellationToken).ConfigureAwait(false);

        var targetExists = await DatabaseExistsAsync(targetDatabase, cancellationToken).ConfigureAwait(false);
        if (targetExists && !replaceExisting)
        {
            throw new BackupException(
                $"Database '{targetDatabase}' already exists. To replace it, use --replace --confirm {targetDatabase}. " +
                "The existing database will be renamed, not deleted.");
        }

        var stamp = clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture).ToLowerInvariant();
        var staging = $"{targetDatabase}_restoring_{stamp}";
        var problems = new List<string>();
        try
        {
            await CreateDatabaseAsync(staging, cancellationToken).ConfigureAwait(false);
            var (manifest, _) = await RestoreIntoAsync(backupPath, staging, cancellationToken).ConfigureAwait(false);
            await CompareWithManifestAsync(staging, manifest, problems, cancellationToken).ConfigureAwait(false);
            await RunVerificationScriptsAsync(staging, problems, cancellationToken).ConfigureAwait(false);
            if (problems.Count > 0)
            {
                throw new BackupException("The restored data failed verification; the target was not changed:\n  - " + string.Join("\n  - ", problems));
            }
        }
        catch
        {
            await DropScratchDatabaseAsync(staging).ConfigureAwait(false);
            throw;
        }

        await using var connection = new NpgsqlConnection(settings.AdminConnectionString(MaintenanceDatabase));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (targetExists)
        {
            var previous = $"{targetDatabase}_pre_restore_{stamp}";
            await TerminateConnectionsAsync(connection, targetDatabase, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, $"ALTER DATABASE {Quote(targetDatabase)} RENAME TO {Quote(previous)}", cancellationToken).ConfigureAwait(false);
            log.WriteLine($"Previous database kept as '{previous}'.");
        }

        await ExecuteAsync(connection, null, $"ALTER DATABASE {Quote(staging)} RENAME TO {Quote(targetDatabase)}", cancellationToken).ConfigureAwait(false);
        log.WriteLine($"Restore complete: '{targetDatabase}' now contains the data from {Path.GetFileName(backupPath)}.");
    }

    private async Task<(BackupManifest Manifest, long DumpBytes)> RestoreIntoAsync(string backupPath, string database, CancellationToken cancellationToken)
    {
        PayloadSplitter? splitter = null;
        await postgres.RestoreAsync(
            database,
            async (restoreInput, ct) =>
            {
                splitter = new PayloadSplitter((data, token) => restoreInput.WriteAsync(data, token));
                await using var input = OpenBackup(backupPath);
                await BackupCrypto.DecryptAsync(input, settings.Passphrase, splitter.WriteAsync, ct).ConfigureAwait(false);
                await restoreInput.FlushAsync(ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        var manifest = splitter!.Complete();
        log.WriteLine($"Restored {splitter.DumpBytes:N0} dump bytes into '{database}'.");
        return (manifest, splitter.DumpBytes);
    }

    private async Task<long> CompareWithManifestAsync(string database, BackupManifest manifest, List<string> problems, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.AdminConnectionString(database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var actualCounts = await CountRowsAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var actualMigrations = await ReadMigrationsAsync(connection, null, cancellationToken).ConfigureAwait(false);

        foreach (var (table, expected) in manifest.TableRowCounts)
        {
            if (!actualCounts.TryGetValue(table, out var actual))
            {
                problems.Add($"table '{table}' is missing after restore");
            }
            else if (actual != expected)
            {
                problems.Add($"table '{table}' has {actual} rows after restore; the backup recorded {expected}");
            }
        }

        foreach (var table in actualCounts.Keys.Except(manifest.TableRowCounts.Keys))
        {
            problems.Add($"table '{table}' exists after restore but was not in the backup manifest");
        }

        if (!actualMigrations.SequenceEqual(manifest.Migrations))
        {
            problems.Add("the applied migrations after restore differ from the backup manifest");
        }

        return actualCounts.Values.Sum();
    }

    private async Task RunVerificationScriptsAsync(string database, List<string> problems, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(settings.VerificationDirectory))
        {
            problems.Add($"verification directory '{settings.VerificationDirectory}' was not found");
            return;
        }

        var variables = new Dictionary<string, string>
        {
            ["app_role"] = settings.AppRole,
            ["migrator_role"] = settings.MigratorRole,
        };
        foreach (var file in Directory.GetFiles(settings.VerificationDirectory, "*.sql").Order(StringComparer.Ordinal))
        {
            try
            {
                var script = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                await postgres.RunSqlScriptAsync(database, script, variables, cancellationToken).ConfigureAwait(false);
            }
            catch (BackupException ex)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    /// <summary>Creates a database with the same ownership and connect rights as the init script.</summary>
    private async Task CreateDatabaseAsync(string database, CancellationToken cancellationToken)
    {
        ValidateDatabaseName(database);
        await using var connection = new NpgsqlConnection(settings.AdminConnectionString(MaintenanceDatabase));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            $"CREATE DATABASE {Quote(database)} OWNER {Quote(settings.MigratorRole)} ENCODING 'UTF8' TEMPLATE template0", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            $"REVOKE ALL ON DATABASE {Quote(database)} FROM PUBLIC; " +
            $"GRANT CONNECT, TEMPORARY ON DATABASE {Quote(database)} TO {Quote(settings.MigratorRole)}; " +
            $"GRANT CONNECT ON DATABASE {Quote(database)} TO {Quote(settings.AppRole)}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops only databases this tool created (restore-test and staging names).</summary>
    private async Task DropScratchDatabaseAsync(string database)
    {
        if (!database.StartsWith("sb_restoretest_", StringComparison.Ordinal) && !database.Contains("_restoring_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to drop '{database}': it is not a temporary restore database.");
        }

        await using var connection = new NpgsqlConnection(settings.AdminConnectionString(MaintenanceDatabase));
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"DROP DATABASE IF EXISTS {Quote(database)} WITH (FORCE)", CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> DatabaseExistsAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.AdminConnectionString(MaintenanceDatabase));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", database);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task TerminateConnectionsAsync(NpgsqlConnection connection, string database, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()", connection);
        command.Parameters.AddWithValue("name", database);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, long>> CountRowsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            counts[table] = (long)(await ScalarAsync(connection, transaction, $"SELECT count(*) FROM public.{Quote(table)}", cancellationToken).ConfigureAwait(false))!;
        }

        return counts;
    }

    private static async Task<List<string>> ReadMigrationsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var migrations = new List<string>();
        if (await ScalarAsync(connection, transaction, "SELECT to_regclass('public.__ef_migrations_history')::text", cancellationToken).ConfigureAwait(false) is not string)
        {
            return migrations;
        }

        await using var command = new NpgsqlCommand("SELECT migration_id FROM public.__ef_migrations_history ORDER BY migration_id", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            migrations.Add(reader.GetString(0));
        }

        return migrations;
    }

    private static async Task VerifyChecksumAsync(string backupPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(backupPath))
        {
            throw new BackupException($"Backup file not found: {backupPath}");
        }

        var checksumPath = backupPath + ChecksumExtension;
        if (!File.Exists(checksumPath))
        {
            throw new BackupIntegrityException($"Checksum file is missing: {checksumPath}");
        }

        var expected = (await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false)).Split(' ', 2)[0].Trim();
        var actual = await ComputeSha256Async(backupPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupIntegrityException(
                $"SHA-256 mismatch for {Path.GetFileName(backupPath)}: the file is corrupted or has been modified (expected {expected}, actual {actual}).");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static FileStream OpenBackup(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

    private string UniqueName(string prefix) =>
        $"{prefix}_{clock.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static void ValidateDatabaseName(string database)
    {
        if (!DatabaseNamePattern().IsMatch(database))
        {
            throw new BackupException($"'{database}' is not a valid database name (lowercase letters, digits and underscores; max 63).");
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex DatabaseNamePattern();
}
