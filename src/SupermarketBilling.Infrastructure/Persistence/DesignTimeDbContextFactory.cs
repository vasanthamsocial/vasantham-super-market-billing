using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SupermarketBilling.Infrastructure.Persistence;

/// <summary>
/// Used only by the <c>dotnet ef</c> tooling. Migrations are applied with the migrator (schema-owner)
/// account, never with the least-privilege runtime account.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SupermarketBillingDbContext>
{
    public SupermarketBillingDbContext CreateDbContext(string[] args)
    {
        // A connection string is only needed to apply migrations; generating them works without one.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=design_time_only";

        var options = new DbContextOptionsBuilder<SupermarketBillingDbContext>();
        DependencyInjection.ConfigureDbContext(options, connectionString);
        return new SupermarketBillingDbContext(options.Options);
    }
}
