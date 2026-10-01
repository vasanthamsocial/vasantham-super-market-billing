using System.Text;

namespace SupermarketBilling.CounterAgent;

/// <summary>
/// Builds ESC/POS commands, the language of most receipt printers. Text is sent in code page 1252 (Latin); other
/// characters print as '?'. The browser receipt remains available for other scripts.
/// </summary>
public sealed class EscPos
{
    public const byte Esc = 0x1B;
    public const byte Gs = 0x1D;
    private static readonly Encoding Latin = Encoding.Latin1;
    private readonly List<byte> _bytes = [];

    public EscPos() => Raw(Esc, (byte)'@', Esc, (byte)'t', 16); // initialise; character table 16 = WPC1252

    public byte[] ToArray() => [.. _bytes];

    public EscPos Raw(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    public EscPos Align(char where) => Raw(Esc, (byte)'a', where switch { 'C' => (byte)1, 'R' => (byte)2, _ => (byte)0 });

    public EscPos Bold(bool on) => Raw(Esc, (byte)'E', on ? (byte)1 : (byte)0);

    /// <summary>Double width and height (for the total).</summary>
    public EscPos Large(bool on) => Raw(Gs, (byte)'!', on ? (byte)0x11 : (byte)0x00);

    public EscPos Line(string text = "")
    {
        _bytes.AddRange(Latin.GetBytes(Clean(text)));
        _bytes.Add((byte)'\n');
        return this;
    }

    /// <summary>Feeds enough paper to tear past the cutter, then cuts (partial cut).</summary>
    public EscPos Cut() => Raw(Esc, (byte)'d', 4, Gs, (byte)'V', 66, 0);

    /// <summary>Pulses the drawer kick connector (pin 2), which opens the cash drawer.</summary>
    public EscPos OpenDrawer() => Raw(Esc, (byte)'p', 0, 25, 250);

    /// <summary>Text the printer's Latin code page can show: the rupee sign as "Rs.", anything else non-Latin as '?'.</summary>
    public static string Clean(string text) =>
        string.Concat((text ?? string.Empty).Replace("₹", "Rs.", StringComparison.Ordinal)
            .Select(c => c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ' ? c : '?'));

    /// <summary>Left and right text on one line of <paramref name="columns"/>, the left side shortened if needed.</summary>
    public static string TwoColumns(string left, string right, int columns)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var room = Math.Max(0, columns - right.Length - 1);
        var l = left.Length > room ? left[..room] : left;
        return l + new string(' ', Math.Max(1, columns - l.Length - right.Length)) + right;
    }

    /// <summary>Splits text into lines of at most <paramref name="columns"/>, at spaces where possible.</summary>
    public static IEnumerable<string> Wrap(string text, int columns)
    {
        var line = new StringBuilder();
        foreach (var word in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word;
            while (w.Length > columns)
            {
                if (line.Length > 0)
                {
                    yield return line.ToString();
                    line.Clear();
                }

                yield return w[..columns];
                w = w[columns..];
            }

            if (line.Length > 0 && line.Length + 1 + w.Length > columns)
            {
                yield return line.ToString();
                line.Clear();
            }

            line.Append(line.Length > 0 ? " " : string.Empty).Append(w);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
