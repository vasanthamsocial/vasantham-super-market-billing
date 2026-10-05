using System.Globalization;
using System.Text;
using SupermarketBilling.Application.Contracts;

namespace SupermarketBilling.Infrastructure.Reporting;

/// <summary>
/// Writes a report as CSV for Excel: UTF-8 with a byte-order mark, every cell quoted, numbers without grouping, and text
/// that a spreadsheet would run as a formula (starting with =, +, -, @, tab or carriage return) prefixed with an apostrophe.
/// </summary>
public static class ReportCsv
{
    public static byte[] Write(ReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var csv = new StringBuilder();
        csv.AppendLine(Line(report.Columns.Select(c => c.Label)));
        foreach (var row in report.Rows)
        {
            csv.AppendLine(Line(report.Columns.Select(c => Cell(c, row.GetValueOrDefault(c.Key)))));
        }

        if (report.Totals is { } totals)
        {
            csv.AppendLine(Line(report.Columns.Select(c => Cell(c, totals.GetValueOrDefault(c.Key)))));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    public static string FileName(ReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return $"{report.Key}_{report.From:yyyy-MM-dd}_{report.To:yyyy-MM-dd}.csv";
    }

    private static string Line(IEnumerable<string> cells) => string.Join(",", cells.Select(c => "\"" + c.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));

    private static string Cell(ReportColumn column, object? value) => value switch
    {
        null => string.Empty,
        decimal d => column.Kind == ColumnKinds.Quantity ? d.ToString("0.###", CultureInfo.InvariantCulture) : d.ToString("0.00", CultureInfo.InvariantCulture),
        int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset at => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        _ => Safe(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static string Safe(string text) => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;
}
