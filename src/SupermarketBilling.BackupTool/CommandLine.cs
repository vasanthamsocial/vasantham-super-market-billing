using SupermarketBilling.BackupTool.Format;

namespace SupermarketBilling.BackupTool;

/// <summary>Minimal "--name value" / "--flag" parser. Unknown options are rejected.</summary>
internal sealed class CommandLine
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "database", "out", "file", "target", "confirm", "verification-dir",
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "no-restore-test", "keep", "replace",
    };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public static CommandLine Parse(ReadOnlySpan<string> arguments)
    {
        var result = new CommandLine();
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new BackupException($"Unexpected argument '{argument}'.");
            }

            var name = argument[2..];
            if (FlagOptions.Contains(name))
            {
                result._flags.Add(name);
            }
            else if (ValueOptions.Contains(name))
            {
                if (i + 1 >= arguments.Length)
                {
                    throw new BackupException($"Option --{name} needs a value.");
                }

                result._values[name] = arguments[++i];
            }
            else
            {
                throw new BackupException($"Unknown option '--{name}'.");
            }
        }

        return result;
    }

    public string? Value(string name) => _values.GetValueOrDefault(name);

    public string Required(string name) =>
        Value(name) ?? throw new BackupException($"Missing required option --{name}.");

    public bool Flag(string name) => _flags.Contains(name);
}
