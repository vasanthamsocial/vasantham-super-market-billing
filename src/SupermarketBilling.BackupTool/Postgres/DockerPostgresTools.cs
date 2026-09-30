using System.Diagnostics;
using System.Text;
using SupermarketBilling.BackupTool.Format;

namespace SupermarketBilling.BackupTool.Postgres;

/// <summary>
/// Runs PostgreSQL client programs inside the database container with <c>docker exec</c>.
/// Binary data is streamed through process pipes, so the unencrypted dump never touches the host disk.
/// </summary>
internal sealed class DockerPostgresTools(string container, string superUser) : IPostgresTools
{
    public async Task DumpAsync(
        string database, string snapshotId, Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consume);
        var startInfo = Docker(
            "exec", container, "pg_dump",
            "--username", superUser, "--no-password",
            "--format=custom", "--compress=6",
            $"--snapshot={snapshotId}",
            "--dbname", database);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Start(startInfo);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await consume(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new BackupException($"pg_dump failed with exit code {process.ExitCode}: {(await stderr.ConfigureAwait(false)).Trim()}");
        }
    }

    public async Task RestoreAsync(string database, Func<Stream, CancellationToken, Task> produce, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(produce);
        var startInfo = Docker(
            "exec", "-i", container, "pg_restore",
            "--username", superUser, "--no-password",
            "--exit-on-error", "--single-transaction",
            "--dbname", database);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Start(startInfo);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await produce(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException ex)
        {
            // pg_restore stopped reading (it failed); report its own error rather than the broken pipe.
            await WaitBrieflyAsync(process).ConfigureAwait(false);
            TryKill(process);
            throw new BackupException($"pg_restore failed: {(await stderr.ConfigureAwait(false)).Trim()}", ex);
        }
        catch
        {
            // Killing pg_restore mid-stream aborts its single transaction, so nothing is left half-restored.
            TryKill(process);
            throw;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new BackupException($"pg_restore failed with exit code {process.ExitCode}: {(await stderr.ConfigureAwait(false)).Trim()}");
        }
    }

    public async Task<string> RunSqlScriptAsync(
        string database, string script, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var arguments = new List<string>
        {
            "exec", "-i", container, "psql", "--no-psqlrc", "--quiet",
            "-v", "ON_ERROR_STOP=1", "--username", superUser, "--dbname", database,
        };
        foreach (var (name, value) in variables)
        {
            arguments.Add("-v");
            arguments.Add($"{name}={value}");
        }

        arguments.Add("--file=-");
        var startInfo = Docker([.. arguments]);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding = new UTF8Encoding(false);

        using var process = Start(startInfo);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(script.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (process.ExitCode != 0)
        {
            throw new BackupException($"psql failed with exit code {process.ExitCode}: {output.Trim()}");
        }

        return output;
    }

    private static ProcessStartInfo Docker(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static Process Start(ProcessStartInfo startInfo) =>
        Process.Start(startInfo) ?? throw new BackupException("Could not start the docker command. Is Docker installed and on PATH?");

    private static async Task WaitBrieflyAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killed by the caller.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
