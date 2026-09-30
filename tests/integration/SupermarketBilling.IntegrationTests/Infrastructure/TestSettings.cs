namespace SupermarketBilling.IntegrationTests.Infrastructure;

/// <summary>
/// Resolves test connection strings from the environment, falling back to the repository's .env file
/// (so tests also run from the VS Code test explorer). Integration tests only ever use the *_test database.
/// </summary>
internal static class TestSettings
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> DotEnv = new(LoadDotEnv);

    public static string AppConnectionString => Require("ConnectionStrings__Test");

    public static string MigratorConnectionString => Require("ConnectionStrings__TestMigrator");

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            DotEnv.Value.TryGetValue(name, out value);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{name} is not configured. Run scripts/setup-dev.ps1 and scripts/db-up.ps1 first.");
        }

        if (!value.Contains("_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} must point at a *_test database; refusing to run tests against it.");
        }

        return value;
    }

    private static Dictionary<string, string> LoadDotEnv()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return values;
        }

        foreach (var line in File.ReadAllLines(Path.Combine(directory.FullName, ".env")))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || separator < 1)
            {
                continue;
            }

            values[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return values;
    }
}
