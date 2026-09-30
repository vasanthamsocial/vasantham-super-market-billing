using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Domain.Auditing;

namespace SupermarketBilling.Infrastructure.Persistence;

public sealed class SupermarketBillingDbContext(DbContextOptions<SupermarketBillingDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SupermarketBillingDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Money, quantities and rates are exact decimals. No floating point anywhere in the financial model.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }
}
