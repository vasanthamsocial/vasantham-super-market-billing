using Microsoft.EntityFrameworkCore;
using Npgsql;
using SupermarketBilling.Application.Common;

namespace SupermarketBilling.Infrastructure.Persistence;

internal static class SaveChangesExtensions
{
    private static readonly Dictionary<string, string> UniqueMessages = new(StringComparer.Ordinal)
    {
        ["ux_businesses_tenant_code"] = "A business with this code already exists.",
        ["ux_businesses_tenant_gstin"] = "Another business already uses this GSTIN.",
        ["ix_stores_business_id_code"] = "This business already has a store with this code.",
        ["ux_users_tenant_username"] = "This username is already taken.",
        ["ix_tenants_code"] = "This company code is already in use.",
        ["ux_role_assignments_active"] = "The user already has this role at this scope.",
    };

    /// <summary>
    /// Saves and translates database constraint and concurrency failures into user-facing conflicts.
    /// The database remains the final authority; this only makes its answer readable.
    /// </summary>
    public static async Task SaveChangesCheckedAsync(this DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new AppException(ErrorKind.Conflict, "concurrency", "This record was changed by someone else. Reload it and try again.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            var message = pg.ConstraintName is { } name && UniqueMessages.TryGetValue(name, out var known)
                ? known
                : "A record with the same unique value already exists.";
            throw new AppException(ErrorKind.Conflict, "duplicate", message, ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation } pg)
        {
            throw new AppException(ErrorKind.Validation, "constraint", $"The change breaks a data rule ({pg.ConstraintName}).", ex);
        }
    }
}
