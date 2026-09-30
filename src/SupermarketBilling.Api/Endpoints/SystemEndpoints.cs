using System.Reflection;

namespace SupermarketBilling.Api.Endpoints;

internal static class SystemEndpoints
{
    public static readonly string ApplicationVersion =
        typeof(SystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/system").WithTags("System").AllowAnonymous();

        group.MapGet("/info", (IHostEnvironment environment, IConfiguration configuration, TimeProvider clock) =>
                new SystemInfoResponse(
                    "SupermarketBilling",
                    ApplicationVersion,
                    environment.EnvironmentName,
                    clock.GetUtcNow(),
                    configuration.GetValue<bool>("ARCHIVE_WEB_ENABLED")))
            .WithName("GetSystemInfo")
            .WithSummary("Returns application version, environment, server time and licensed optional features.");

        return routes;
    }
}

internal sealed record SystemInfoResponse(
    string Application,
    string Version,
    string Environment,
    DateTimeOffset ServerTimeUtc,
    bool ArchiveWebEnabled);
