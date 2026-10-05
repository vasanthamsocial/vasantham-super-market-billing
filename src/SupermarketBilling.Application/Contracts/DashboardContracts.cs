namespace SupermarketBilling.Application.Contracts;

/// <param name="Kind">money, count, quantity, percent, text or datetime.</param>
/// <param name="Tone">good, warning or bad when the figure needs attention; null otherwise.</param>
public sealed record DashboardMetric(string Label, string Kind, decimal? Value = null, string? Text = null, string? Tone = null);

/// <param name="Status">ok, or unavailable (the figures could not be worked out; see Message).</param>
/// <param name="ReportKey">The report with the full detail, if any.</param>
/// <param name="Table">A short table (for example the top items), in the report table format.</param>
public sealed record DashboardSection(
    string Key, string Title, string Status, string? Message, IReadOnlyList<DashboardMetric> Metrics, ReportDto? Table = null, string? ReportKey = null);

/// <summary>The Owner Dashboard (spec section 21): today and the month so far, for one store or the whole business.</summary>
public sealed record DashboardDto(Guid BusinessId, Guid? StoreId, DateOnly Today, DateTimeOffset GeneratedAtUtc, IReadOnlyList<DashboardSection> Sections);

/// <param name="Status">OK, WARNING (old or not restore-tested), FAILED (restore test failed), NONE (no backup found) or NOT_CONFIGURED.</param>
public sealed record BackupStatusDto(
    string Status, string? LatestFile, DateTimeOffset? LatestBackupUtc, long? LatestBytes, bool? RestoreTestPassed, DateTimeOffset? RestoreTestedUtc, int BackupCount,
    string Message);
