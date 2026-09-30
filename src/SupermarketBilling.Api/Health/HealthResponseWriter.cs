using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SupermarketBilling.Api.Health;

internal static class HealthTags
{
    public const string Ready = "ready";
}

/// <summary>
/// Writes a compact JSON health report. Exception details are never exposed; they go to the server log.
/// </summary>
internal static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        var body = new HealthResponse(
            report.Status.ToString(),
            Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            DateTimeOffset.UtcNow,
            report.Entries
                .Select(entry => new HealthCheckEntry(
                    entry.Key,
                    entry.Value.Status.ToString(),
                    entry.Value.Description,
                    Math.Round(entry.Value.Duration.TotalMilliseconds, 1)))
                .ToList());

        return JsonSerializer.SerializeAsync(context.Response.Body, body, JsonOptions, context.RequestAborted);
    }
}

internal sealed record HealthResponse(
    string Status,
    double TotalDurationMs,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<HealthCheckEntry> Checks);

internal sealed record HealthCheckEntry(string Name, string Status, string? Description, double DurationMs);
