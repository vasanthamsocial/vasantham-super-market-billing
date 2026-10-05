using SupermarketBilling.Application.Contracts;

namespace SupermarketBilling.Infrastructure.Reporting;

public static class ColumnKinds
{
    public const string Text = "text";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Count = "count";
    public const string Quantity = "quantity";
    public const string Money = "money";
    public const string Percent = "percent";

    /// <summary>Kinds that add up in the totals row.</summary>
    public static bool Sums(string kind) => kind is Count or Quantity or Money;
}

/// <summary>
/// Builds a report table. The totals row is the plain sum of the rows for every count, quantity and money column, so a
/// consolidated figure always equals its components; ratios in the totals are worked out from the summed figures.
/// </summary>
internal sealed class ReportTable
{
    private readonly List<ReportColumn> _columns = [];
    private readonly List<Dictionary<string, object?>> _rows = [];
    private readonly List<(string Key, string Numerator, string Denominator, decimal Factor)> _ratios = [];
    private readonly List<string> _notes = [];

    public ReportTable Column(string key, string label, string kind)
    {
        _columns.Add(new ReportColumn(key, label, kind));
        return this;
    }

    /// <summary>A percentage column: numerator / denominator x 100, in the rows and in the totals.</summary>
    public ReportTable Ratio(string key, string label, string numerator, string denominator)
    {
        _columns.Add(new ReportColumn(key, label, ColumnKinds.Percent));
        _ratios.Add((key, numerator, denominator, 100m));
        return this;
    }

    /// <summary>An average in money: numerator / denominator, in the rows and in the totals (for example sales per bill).</summary>
    public ReportTable Average(string key, string label, string numerator, string denominator)
    {
        _columns.Add(new ReportColumn(key, label, ColumnKinds.Money));
        _ratios.Add((key, numerator, denominator, 1m));
        return this;
    }

    public ReportTable Note(string note)
    {
        _notes.Add(note);
        return this;
    }

    public void Row(params (string Key, object? Value)[] values)
    {
        var row = values.ToDictionary(v => v.Key, v => v.Value);
        foreach (var (key, numerator, denominator, factor) in _ratios)
        {
            row[key] = Divide(row.GetValueOrDefault(numerator), row.GetValueOrDefault(denominator), factor);
        }

        _rows.Add(row);
    }

    public ReportDto Build(string key, string title, ReportQuery query, DateTimeOffset now, bool totals = true)
    {
        Dictionary<string, object?>? sum = null;
        if (totals)
        {
            sum = new Dictionary<string, object?> { [_columns[0].Key] = "Total" };
            foreach (var column in _columns.Skip(1).Where(c => ColumnKinds.Sums(c.Kind) && _ratios.All(r => r.Key != c.Key)))
            {
                // Counts stay whole numbers (a conditional expression would widen them to decimal).
                if (column.Kind == ColumnKinds.Count)
                {
                    sum[column.Key] = _rows.Sum(r => Convert.ToInt64(r.GetValueOrDefault(column.Key) ?? 0L, System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    sum[column.Key] = _rows.Sum(r => Convert.ToDecimal(r.GetValueOrDefault(column.Key) ?? 0m, System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            foreach (var (ratio, numerator, denominator, factor) in _ratios)
            {
                sum[ratio] = Divide(sum.GetValueOrDefault(numerator), sum.GetValueOrDefault(denominator), factor);
            }
        }

        return new ReportDto(key, title, query.From, query.To, query.By, _columns, _rows, sum, _notes, now);
    }

    private static decimal? Divide(object? numerator, object? denominator, decimal factor)
    {
        var d = Convert.ToDecimal(denominator ?? 0m, System.Globalization.CultureInfo.InvariantCulture);
        return d == 0 ? null : decimal.Round(Convert.ToDecimal(numerator ?? 0m, System.Globalization.CultureInfo.InvariantCulture) * factor / d, 2, MidpointRounding.AwayFromZero);
    }
}
