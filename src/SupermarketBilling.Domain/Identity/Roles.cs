using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.Domain.Identity;

/// <param name="Code">Stable identifier stored in role assignments.</param>
/// <param name="IsPrivileged">Privileged roles need maker-checker approval to grant and can be forced to use MFA.</param>
/// <param name="BusinessWideOnly">The role cannot be limited to a single store.</param>
public sealed record RoleDefinition(string Code, string Name, bool IsPrivileged, bool BusinessWideOnly, IReadOnlySet<string> Permissions);

/// <summary>System roles from the specification and the permissions each grants.</summary>
public static class Roles
{
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Cashier = "cashier";
    public const string InventoryOperator = "inventory_operator";
    public const string PurchaseOperator = "purchase_operator";
    public const string Accountant = "accountant";
    public const string CollectionManager = "collection_manager";
    public const string CollectionPerson = "collection_person";
    public const string Auditor = "auditor";
    public const string SupportAdmin = "support_admin";

    private static readonly Dictionary<string, RoleDefinition> Definitions = new(StringComparer.Ordinal)
    {
        [Owner] = new(Owner, "Owner", IsPrivileged: true, BusinessWideOnly: true, Permissions.All),
        [Manager] = new(Manager, "Manager", true, false, Set(
            Permissions.StoresView, Permissions.StoresManage, Permissions.UsersView, Permissions.UsersManage,
            Permissions.UsersUnlock, Permissions.RolesAssign, Permissions.ApprovalsView, Permissions.ApprovalsDecide,
            Permissions.AuditView, Permissions.CatalogView, Permissions.CatalogManage, Permissions.PricesManage, Permissions.TaxApprove,
            Permissions.StockView, Permissions.StockAdjust, Permissions.StockTransfer, Permissions.StockCount, Permissions.StockSettings,
            Permissions.StockNegativeOverride, Permissions.PosBill, Permissions.PosPriceOverride, Permissions.PosDiscount,
            Permissions.PosReturn, Permissions.PosCreditOverride, Permissions.CountersManage, Permissions.ShiftsManage, Permissions.SalesView,
            Permissions.SuppliersManage, Permissions.PurchasesView, Permissions.PurchasesManage, Permissions.PurchasesApprove,
            Permissions.DebtorsView, Permissions.DebtorsManage, Permissions.PayablesManage, Permissions.ReceivablesManage,
            Permissions.LedgersAdjust, Permissions.LedgersApprove, Permissions.CollectionsView, Permissions.CollectionsManage, Permissions.CollectionsReceive,
            Permissions.CollectionsAllocate, Permissions.MessagingView, Permissions.MessagingManage, Permissions.DispatchView,
            Permissions.DispatchManage, Permissions.ReportsView, Permissions.ReportsProfit)),
        [Accountant] = new(Accountant, "Accountant", true, true, Set(
            Permissions.StoresView, Permissions.ApprovalsView, Permissions.ApprovalsDecide, Permissions.AuditView,
            Permissions.CatalogView, Permissions.TaxReview, Permissions.StockView, Permissions.SalesView, Permissions.PurchasesView,
            Permissions.DebtorsView, Permissions.DebtorsManage, Permissions.PayablesManage, Permissions.ReceivablesManage, Permissions.LedgersAdjust,
            Permissions.CollectionsView, Permissions.CollectionsReceive, Permissions.MessagingView, Permissions.DispatchView,
            Permissions.ReportsView, Permissions.ReportsProfit)),
        [Auditor] = new(Auditor, "Auditor", true, true, Set(
            Permissions.StoresView, Permissions.UsersView, Permissions.ApprovalsView, Permissions.AuditView, Permissions.CatalogView, Permissions.StockView,
            Permissions.SalesView, Permissions.PurchasesView, Permissions.DebtorsView, Permissions.CollectionsView, Permissions.MessagingView,
            Permissions.DispatchView, Permissions.ReportsView, Permissions.ReportsProfit)),
        [SupportAdmin] = new(SupportAdmin, "Restricted support administrator", true, true, Set(
            Permissions.StoresView, Permissions.UsersUnlock, Permissions.SystemDiagnostics)),
        [Cashier] = new(Cashier, "Cashier", false, false, Set(
            Permissions.StoresView, Permissions.CatalogView, Permissions.StockView, Permissions.PosBill, Permissions.SalesView)),
        [InventoryOperator] = new(InventoryOperator, "Inventory operator", false, false, Set(
            Permissions.StoresView, Permissions.CatalogView, Permissions.CatalogManage, Permissions.StockView, Permissions.StockAdjust,
            Permissions.StockTransfer, Permissions.StockCount, Permissions.DispatchView, Permissions.DispatchManage)),
        [PurchaseOperator] = new(PurchaseOperator, "Purchase operator", false, false, Set(
            Permissions.StoresView, Permissions.CatalogView, Permissions.StockView, Permissions.SuppliersManage, Permissions.PurchasesView, Permissions.PurchasesManage)),
        [CollectionManager] = new(CollectionManager, "Collection manager", false, false, Set(
            Permissions.StoresView, Permissions.UsersView, Permissions.CatalogView, Permissions.DebtorsView, Permissions.ReceivablesManage,
            Permissions.CollectionsView, Permissions.CollectionsManage, Permissions.CollectionsReceive, Permissions.CollectionsAllocate)),
        [CollectionPerson] = new(CollectionPerson, "Collection person", false, false, Set(Permissions.StoresView, Permissions.CollectionsCollect)),
    };

    public static IReadOnlyCollection<RoleDefinition> All => Definitions.Values;

    public static IReadOnlyCollection<string> Codes => Definitions.Keys;

    public static bool Exists(string code) => Definitions.ContainsKey(code);

    public static RoleDefinition Get(string code) =>
        Definitions.TryGetValue(code, out var role)
            ? role
            : throw new DomainException("role.unknown", $"Unknown role '{code}'.");

    private static HashSet<string> Set(params string[] permissions) => new(permissions, StringComparer.Ordinal);
}
