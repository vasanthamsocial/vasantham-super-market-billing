namespace SupermarketBilling.BackupTool.Postgres;

/// <summary>
/// Runs the PostgreSQL client programs (pg_dump, pg_restore, psql). Development uses the Docker container;
/// a native implementation for store servers without Docker is added with the Stage 17 installer.
/// </summary>
internal interface IPostgresTools
{
    /// <summary>Streams a custom-format dump of <paramref name="database"/> taken at an exported snapshot.</summary>
    Task DumpAsync(string database, string snapshotId, Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken);

    /// <summary>Restores a custom-format dump written by <paramref name="produce"/> in a single transaction.</summary>
    Task RestoreAsync(string database, Func<Stream, CancellationToken, Task> produce, CancellationToken cancellationToken);

    /// <summary>Runs a SQL script with psql (ON_ERROR_STOP) and returns its combined output.</summary>
    Task<string> RunSqlScriptAsync(string database, string script, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken);
}
