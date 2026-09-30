using Npgsql;
using SupermarketBilling.BackupTool.Format;

namespace SupermarketBilling.BackupTool;

/// <summary>Backup configuration, normally read from the environment populated from .env.</summary>
internal sealed record BackupSettings(
    string Host,
    int Port,
    string SuperUser,
    string SuperUserPassword,
    string MigratorRole,
    string AppRole,
    string Container,
    string Passphrase,
    string VerificationDirectory)
{
    public static BackupSettings FromEnvironment(string? verificationDirectory)
    {
        return new BackupSettings(
            Optional("SB_DB_HOST", "localhost"),
            int.Parse(Optional("SB_DB_PORT", "5442"), System.Globalization.CultureInfo.InvariantCulture),
            Required("SB_DB_SUPERUSER"),
            Required("SB_DB_SUPERUSER_PASSWORD"),
            Required("SB_DB_MIGRATOR_USER"),
            Required("SB_DB_APP_USER"),
            Optional("SB_DB_CONTAINER", "supermarketbilling-db"),
            Required("SB_BACKUP_PASSPHRASE"),
            verificationDirectory ?? Optional("SB_VERIFICATION_DIR", Path.Combine("database", "verification")));
    }

    /// <summary>Administrative connection. Used only for snapshots, row counts and creating/renaming databases.</summary>
    public string AdminConnectionString(string database) => new NpgsqlConnectionStringBuilder
    {
        Host = Host,
        Port = Port,
        Username = SuperUser,
        Password = SuperUserPassword,
        Database = database,
        Pooling = false,
        CommandTimeout = 0,
        ApplicationName = "sb-backup",
    }.ConnectionString;

    public override string ToString() =>
        $"BackupSettings {{ Host = {Host}, Port = {Port}, SuperUser = {SuperUser}, Container = {Container} }}";

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || value.Contains("CHANGE_ME", StringComparison.Ordinal))
        {
            throw new BackupException($"{name} is not set. Load .env (the scripts do this) or set it in the environment.");
        }

        return value;
    }

    private static string Optional(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
