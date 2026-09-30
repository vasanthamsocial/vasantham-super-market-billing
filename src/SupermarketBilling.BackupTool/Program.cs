using SupermarketBilling.BackupTool.Format;
using SupermarketBilling.BackupTool.Postgres;

namespace SupermarketBilling.BackupTool;

// sb-backup: encrypted, checksum-verified PostgreSQL backups with automatic restore testing.
// Secrets (database password, backup passphrase) come only from environment variables, never arguments.
internal static class BackupCli
{
    private const string Usage = """
        Usage:
          sb-backup backup       --database <name> --out <dir> [--no-restore-test]
          sb-backup verify       --file <backup.sbbak>
          sb-backup restore-test --file <backup.sbbak> [--keep]
          sb-backup restore      --file <backup.sbbak> --target <database> [--replace --confirm <database>]

        Common options:
          --verification-dir <dir>   SQL verification scripts (default: database/verification)

        Environment: SB_DB_HOST, SB_DB_PORT, SB_DB_SUPERUSER, SB_DB_SUPERUSER_PASSWORD, SB_DB_MIGRATOR_USER,
                     SB_DB_APP_USER, SB_DB_CONTAINER, SB_BACKUP_PASSPHRASE
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var command = args[0];
            var options = CommandLine.Parse(args.AsSpan(1));
            var settings = BackupSettings.FromEnvironment(options.Value("verification-dir"));
            var service = new BackupService(
                settings, new DockerPostgresTools(settings.Container, settings.SuperUser), TimeProvider.System, Console.Out);

            switch (command)
            {
                case "backup":
                {
                    var result = await service.CreateBackupAsync(options.Required("database"), options.Required("out"), cancellation.Token);
                    if (!options.Flag("no-restore-test"))
                    {
                        var report = await service.RestoreTestAsync(result.FilePath, keepDatabase: false, cancellation.Token);
                        return report.Passed ? 0 : 1;
                    }

                    return 0;
                }

                case "verify":
                    await service.VerifyAsync(options.Required("file"), cancellation.Token);
                    return 0;

                case "restore-test":
                {
                    var report = await service.RestoreTestAsync(options.Required("file"), options.Flag("keep"), cancellation.Token);
                    return report.Passed ? 0 : 1;
                }

                case "restore":
                {
                    var target = options.Required("target");
                    var replace = options.Flag("replace");
                    if (replace && options.Value("confirm") != target)
                    {
                        throw new BackupException($"--replace requires --confirm {target} (the exact target database name).");
                    }

                    await service.RestoreAsync(options.Required("file"), target, replace, cancellation.Token);
                    return 0;
                }

                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (BackupException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
    }
}
