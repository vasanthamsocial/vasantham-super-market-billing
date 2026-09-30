using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupermarketBilling.Application.Auditing;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Name of the least-privilege runtime connection string (DML only, no DDL).</summary>
    public const string MainConnectionStringName = "Main";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(MainConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{MainConnectionStringName}' is not configured. " +
                "Set ConnectionStrings__Main (see .env.example and scripts/setup-dev.ps1).");
        }

        services.AddDbContext<SupermarketBillingDbContext>(options => ConfigureDbContext(options, connectionString));
        services.AddScoped<IAuditTrail, EfAuditTrail>();
        services.AddScoped<AuditRecorder>();
        services.AddScoped<AuditQueryService>();

        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.SectionName));
        services.AddSingleton<PasswordHashing>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<SetupCodeStore>();
        services.AddHostedService<SetupCodeInitializer>();

        services.AddScoped<IAccessControl, AccessControl>();
        services.AddScoped<SessionService>();
        services.AddScoped<AuthService>();
        services.AddScoped<SetupService>();
        services.AddScoped<OrganisationService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<ApprovalService>();
        return services;
    }

    public static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
                npgsql.MigrationsAssembly(typeof(SupermarketBillingDbContext).Assembly.GetName().Name);
            })
            .UseSnakeCaseNamingConvention();
    }
}
