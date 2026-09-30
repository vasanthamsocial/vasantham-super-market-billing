using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Auditing;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Persistence;

public sealed class SupermarketBillingDbContext(DbContextOptions<SupermarketBillingDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Business> Businesses => Set<Business>();

    public DbSet<Store> Stores => Set<Store>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();

    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Installation> Installation => Set<Installation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Tenant columns first, so configurations can build tenant-consistent composite keys on them.
        TenancyModelBuilder.Configure(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SupermarketBillingDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Money, quantities and rates are exact decimals. No floating point anywhere in the financial model.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }
}
