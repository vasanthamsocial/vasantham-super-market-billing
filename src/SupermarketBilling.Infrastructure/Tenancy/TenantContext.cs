using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Infrastructure.Tenancy;

public enum DeploymentMode
{
    /// <summary>In-store server: one tenant, offline-first billing on the LAN.</summary>
    Edge,

    /// <summary>Vendor-hosted service: many tenants, identified by company code at sign-in.</summary>
    Cloud,
}

/// <summary>Bound from the "Deployment" configuration section (environment: Deployment__*).</summary>
public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    public DeploymentMode Mode { get; set; } = DeploymentMode.Edge;

    /// <summary>
    /// Cloud only: the vendor's provisioning key, required to create a new tenant. Stored in the environment,
    /// never in source control.
    /// </summary>
    public string ProvisioningKey { get; set; } = string.Empty;
}

/// <summary>
/// The tenant the current request works in. Every database connection opened for the request carries it as the
/// PostgreSQL setting <c>sb.tenant_id</c>, which the row-level security policies compare against. With no tenant
/// set, tenant-owned tables appear empty and cannot be written.
/// </summary>
public sealed class TenantContext
{
    public const string SettingName = "sb.tenant_id";

    public Guid? TenantId { get; private set; }

    /// <summary>Sets the tenant and applies it immediately to the context's connection if it is already open.</summary>
    public async Task SetAsync(Guid tenantId, DbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (TenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException("The tenant of a request cannot change once set.");
        }

        TenantId = tenantId;
        var connection = db.Database.GetDbConnection();
        if (connection.State == System.Data.ConnectionState.Open)
        {
            await ApplyAsync(connection, tenantId, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task ApplyAsync(DbConnection connection, Guid? tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('" + SettingName + "', @tenant, false)";
        command.Parameters.Add(new NpgsqlParameter("tenant", tenantId?.ToString() ?? string.Empty));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Applies the request's tenant to every connection as it opens (pooled connections are reset on return).</summary>
internal sealed class TenantConnectionInterceptor(TenantContext tenant) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await TenantContext.ApplyAsync(connection, tenant.TenantId, cancellationToken).ConfigureAwait(false);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        TenantContext.ApplyAsync(connection, tenant.TenantId, CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>Stamps new tenant-owned rows with the current tenant, so no code path can forget or choose it.</summary>
internal sealed class TenantStampingInterceptor(TenantContext tenant) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added && e.Entity is ITenantOwned))
        {
            var property = entry.Property(TenancyModel.TenantIdProperty);
            if (property.CurrentValue is Guid existing && existing != Guid.Empty)
            {
                continue;
            }

            if (tenant.TenantId is { } tenantId)
            {
                property.CurrentValue = tenantId;
            }
            else if (!property.Metadata.IsNullable)
            {
                throw new InvalidOperationException($"Cannot save a new {entry.Metadata.ClrType.Name} without a current tenant.");
            }
        }
    }
}

/// <summary>Model conventions for tenant-owned entities.</summary>
public static class TenancyModel
{
    public const string TenantIdProperty = "TenantId";
}
