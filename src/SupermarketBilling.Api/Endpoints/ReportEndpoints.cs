using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Reporting;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Reports: the list the user may run, and each report as JSON or CSV.</summary>
internal static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder routes)
    {
        var reports = routes.MapGroup("/api/v1/businesses/{businessId:guid}/reports").WithTags("Reports");
        reports.MapGet("/", (Guid businessId, ReportService s, CancellationToken ct) => s.DefinitionsAsync(businessId, ct))
            .WithSummary("The reports available, with how each can be grouped and whether profit is shown.");
        reports.MapGet("/{key}", async (Guid businessId, string key, DateOnly from, DateOnly to, Guid? storeId, Guid? counterId, Guid? cashierUserId, string? by,
                string? format, ReportService s, CancellationToken ct) =>
            {
                var report = await s.RunAsync(businessId, key, new ReportQuery(from, to, storeId, counterId, cashierUserId, by), ct).ConfigureAwait(false);
                return format == "csv" ? Results.File(ReportCsv.Write(report), "text/csv; charset=utf-8", ReportCsv.FileName(report)) : Results.Ok(report);
            })
            .WithSummary("A report for business dates from-to (at most 366 days), optionally for one store, counter or cashier; format=csv to export.");
        return routes;
    }
}
