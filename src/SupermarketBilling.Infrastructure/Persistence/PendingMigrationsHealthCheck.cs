using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SupermarketBilling.Infrastructure.Persistence;

/// <summary>Reports unhealthy when the database schema is behind the application's migrations.</summary>
public sealed class PendingMigrationsHealthCheck(SupermarketBillingDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (pending.Count == 0)
        {
            return HealthCheckResult.Healthy("Schema is up to date.");
        }

        return HealthCheckResult.Unhealthy(
            $"{pending.Count} pending migration(s). Run scripts/db-migrate.ps1.",
            data: new Dictionary<string, object> { ["pending"] = pending });
    }
}
