namespace SupermarketBilling.Application.Contracts;

/// <param name="Kind">text, date, count, quantity, money or percent: how the column is shown and exported.</param>
public sealed record ReportColumn(string Key, string Label, string Kind);

/// <summary>
/// A report as a table. Every report has the same shape, so one screen shows them all and one writer exports them.
/// The totals row adds up the rows exactly (money is summed, never re-derived).
/// </summary>
public sealed record ReportDto(
    string Key, string Title, DateOnly From, DateOnly To, string? By, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyDictionary<string, object?>? Totals, IReadOnlyList<string> Notes, DateTimeOffset GeneratedAtUtc);

/// <param name="Groupings">The ways the report can be grouped (the "by" option), first is the default; empty when it has none.</param>
public sealed record ReportDefinitionDto(string Key, string Title, string Group, string Description, IReadOnlyList<string> Groupings, bool ShowsProfit);

/// <summary>The filters every report takes. Dates are business dates (the store's calendar day).</summary>
public sealed record ReportQuery(DateOnly From, DateOnly To, Guid? StoreId = null, Guid? CounterId = null, Guid? CashierUserId = null, string? By = null);
