namespace SupermarketBilling.Archiving;

/// <summary>
/// Which datasets of a package are master data (businesses, stores, products, parties, users...) and which are the
/// month's own records. Shared by the store server (which writes them) and the archive server (which keeps masters as
/// their latest copy and month records once, unchanged).
/// </summary>
public static class ArchiveDatasets
{
    /// <summary>Master tables written whole (every row of the business), as they are when the package is made.</summary>
    public static readonly IReadOnlyList<string> MasterTables =
    [
        "tax_registrations", "stores", "counters", "units", "categories", "brands", "customer_groups", "products", "product_variants", "variant_units",
        "variant_mrps", "variant_barcodes", "debtors", "suppliers", "routes", "transporters", "role_assignments",
    ];

    /// <summary>
    /// Datasets the archive keeps as their latest copy: the master tables, the business and its users, and cheques (a
    /// cheque appears in every month in which something happened to it).
    /// </summary>
    public static readonly IReadOnlySet<string> Masters =
        new HashSet<string>(MasterTables.Append("businesses").Append("users").Append("cheques"), StringComparer.Ordinal);

    public static bool IsMaster(string dataset) => Masters.Contains(dataset);
}
