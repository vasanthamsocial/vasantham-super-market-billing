using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SupermarketBilling.Infrastructure.Documents;

public enum TextAlign
{
    Left,
    Right,
    Center,
}

/// <summary>
/// A small, dependency-free PDF writer for business documents (A4, text and lines). It uses the standard Helvetica
/// fonts that every PDF viewer has built in, so no font files are shipped and nothing is fetched from the internet.
/// The trade-off: only Latin (Windows-1252) text; other characters print as '?' (the browser receipt prints any script).
/// Coordinates are in points from the top-left corner of the page.
/// </summary>
public sealed class SimplePdf
{
    public const double PageWidth = 595.28;
    public const double PageHeight = 841.89;

    // Advance widths (1/1000 em) of Helvetica and Helvetica-Bold for ASCII 32-126, from the Adobe font metrics.
    private static readonly int[] RegularWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,
        556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly int[] BoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889,
        611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private readonly List<StringBuilder> _pages = [];
    private readonly string _title;

    public SimplePdf(string title)
    {
        _title = title;
        NewPage();
    }

    public int PageCount => _pages.Count;

    private int _current;

    private StringBuilder Content => _pages[_current];

    /// <summary>Starts a new page and draws on it from now on.</summary>
    public void NewPage()
    {
        _pages.Add(new StringBuilder());
        _current = _pages.Count - 1;
    }

    /// <summary>Draws on an earlier page (0-based), for example to add "Page i of n" once the count is known.</summary>
    public void SelectPage(int index) =>
        _current = index >= 0 && index < _pages.Count ? index : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Width of <paramref name="text"/> in points.</summary>
    public static double Width(string text, double size, bool bold = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var widths = bold ? BoldWidths : RegularWidths;
        return Clean(text).Sum(c => c is >= ' ' and <= '~' ? widths[c - ' '] : 556) * size / 1000;
    }

    /// <summary>Shortens <paramref name="text"/> with "..." so it fits <paramref name="maxWidth"/>.</summary>
    public static string Fit(string text, double size, double maxWidth, bool bold = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Width(text, size, bold) <= maxWidth)
        {
            return text;
        }

        var cut = text;
        while (cut.Length > 0 && Width(cut + "...", size, bold) > maxWidth)
        {
            cut = cut[..^1];
        }

        return cut.TrimEnd() + "...";
    }

    /// <summary>Splits <paramref name="text"/> into lines no wider than <paramref name="maxWidth"/>, breaking at spaces.</summary>
    public static IReadOnlyList<string> Wrap(string text, double size, double maxWidth, bool bold = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Width(candidate, size, bold) <= maxWidth || line.Length == 0)
                {
                    line = candidate;
                }
                else
                {
                    lines.Add(line);
                    line = word;
                }
            }

            lines.Add(Fit(line, size, maxWidth, bold));
        }

        return lines;
    }

    public void Text(double x, double top, string text, double size, bool bold = false, TextAlign align = TextAlign.Left)
    {
        ArgumentNullException.ThrowIfNull(text);
        var width = Width(text, size, bold);
        var left = align switch
        {
            TextAlign.Right => x - width,
            TextAlign.Center => x - (width / 2),
            _ => x,
        };
        Content.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(left)} {N(PageHeight - top - size)} Td ({Escape(text)}) Tj ET\n");
    }

    public void Line(double x1, double top1, double x2, double top2, double width = 0.5) =>
        Content.Append(CultureInfo.InvariantCulture, $"{N(width)} w {N(x1)} {N(PageHeight - top1)} m {N(x2)} {N(PageHeight - top2)} l S\n");

    public void Box(double x, double top, double width, double height, double lineWidth = 0.5) =>
        Content.Append(CultureInfo.InvariantCulture, $"{N(lineWidth)} w {N(x)} {N(PageHeight - top - height)} {N(width)} {N(height)} re S\n");

    public byte[] ToBytes()
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();

        void Write(string s)
        {
            var bytes = Latin1.GetBytes(s);
            output.Write(bytes, 0, bytes.Length);
        }

        void BeginObject(int number)
        {
            while (offsets.Count < number)
            {
                offsets.Add(0);
            }

            offsets[number - 1] = output.Position;
            Write(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n"));
        }

        // 1 catalog, 2 pages, 3 regular font, 4 bold font, 5 info, then a page and its content per page.
        const int firstPage = 6;
        Write("%PDF-1.4\n%âãÏÓ\n");
        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2);
        var kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => string.Create(CultureInfo.InvariantCulture, $"{firstPage + (2 * i)} 0 R")));
        Write(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>\nendobj\n"));
        BeginObject(3);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        BeginObject(4);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
        BeginObject(5);
        Write($"<< /Title ({Escape(_title)}) /Producer (SupermarketBilling) >>\nendobj\n");

        for (var i = 0; i < _pages.Count; i++)
        {
            var pageObject = firstPage + (2 * i);
            BeginObject(pageObject);
            Write(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageObject + 1} 0 R >>\nendobj\n"));

            var compressed = Compress(Latin1.GetBytes(_pages[i].ToString()));
            BeginObject(pageObject + 1);
            Write(string.Create(CultureInfo.InvariantCulture, $"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n"));
            output.Write(compressed, 0, compressed.Length);
            Write("\nendstream\nendobj\n");
        }

        var xref = output.Position;
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(builder.ToString());
        return output.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>Maps text to what the standard fonts can show: the rupee sign as "Rs.", Latin-1 kept, everything else '?'.</summary>
    private static string Clean(string text) =>
        string.Concat(text.Replace("₹", "Rs.", StringComparison.Ordinal).Select(c => c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ' ? c : c is '\t' ? ' ' : '?'));

    private static string Escape(string text) =>
        Clean(text).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
