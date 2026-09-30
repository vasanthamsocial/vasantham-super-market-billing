using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.Domain.Tenancy;

/// <summary>
/// A customer of the SupermarketBilling service (the subscribing company). A tenant owns one or more businesses.
/// An edge (in-store) installation serves exactly one tenant; the cloud serves many.
/// </summary>
public sealed partial class Tenant
{
    private Tenant()
    {
        Code = Name = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Company code typed at cloud sign-in, for example <c>ACMESTORES</c>. Unique across the service.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static Tenant Create(string code, string name, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Code = NormalizeCode(code),
        Name = string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200
            ? throw new DomainException("tenant.name_invalid", "Company name is required (max 200 characters).")
            : name.Trim(),
        IsActive = true,
        CreatedAtUtc = now,
    };

    public static string NormalizeCode(string code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            throw new DomainException("tenant.code_invalid", "Company code must be 3-20 letters or digits.");
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Z0-9]{3,20}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// Marks an entity whose rows belong to exactly one tenant. The persistence layer stores the tenant id in a
/// <c>tenant_id</c> column, fills it automatically on insert, and PostgreSQL row-level security restricts every
/// read and write to the current tenant.
/// </summary>
#pragma warning disable CA1040 // Marker interface: the mapping layer discovers tenant-owned types through it.
public interface ITenantOwned;
#pragma warning restore CA1040
