using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Inventory;

/// <summary>
/// Gapless numbering per store and series. The number is taken with an UPDATE ... RETURNING inside the caller's
/// transaction, which locks the series row until commit; a rolled-back posting gives its number back.
/// </summary>
public sealed class DocumentNumbers(SupermarketBillingDbContext db, TenantContext tenant)
{
    public async Task<long> NextAsync(Guid businessId, Guid storeId, string series, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Document numbers must be taken inside the posting transaction.");
        }

        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant for document numbering.");
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO document_sequences (id, tenant_id, business_id, store_id, series, next_number)
            VALUES ({Guid.CreateVersion7()}, {tenantId}, {businessId}, {storeId}, {series}, 1)
            ON CONFLICT (store_id, series) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);

        // ToListAsync, not SingleAsync: an UPDATE ... RETURNING cannot be wrapped in a sub-select.
        var numbers = await db.Database.SqlQuery<long>(
                $"""
                UPDATE document_sequences SET next_number = next_number + 1
                WHERE store_id = {storeId} AND series = {series}
                RETURNING next_number - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return numbers.Single();
    }

    public static string Format(string storeCode, string prefix, long number) =>
        string.Create(CultureInfo.InvariantCulture, $"{storeCode}/{prefix}/{number:000000}");
}
