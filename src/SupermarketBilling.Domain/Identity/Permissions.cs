namespace SupermarketBilling.Domain.Identity;

/// <summary>
/// Permission catalogue. Authorization is always checked against permissions, never role names.
/// Later stages add module permissions (billing, stock, purchases, collections, ...) here.
/// </summary>
public static class Permissions
{
    public const string BusinessesCreate = "businesses.create";
    public const string BusinessesManage = "businesses.manage";
    public const string StoresView = "stores.view";
    public const string StoresManage = "stores.manage";
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string UsersUnlock = "users.unlock";
    public const string RolesAssign = "roles.assign";
    public const string ApprovalsView = "approvals.view";
    public const string ApprovalsDecide = "approvals.decide";
    public const string AuditView = "audit.view";
    public const string SystemDiagnostics = "system.diagnostics";

    public const string CatalogView = "catalog.view";
    public const string CatalogManage = "catalog.manage";
    public const string PricesManage = "prices.manage";

    /// <summary>Prepare (review) a tax-registration change. Held by accountants.</summary>
    public const string TaxReview = "tax.review";

    /// <summary>Independently approve a tax-registration change. Held by owners and managers.</summary>
    public const string TaxApprove = "tax.approve";

    public const string StockView = "stock.view";
    public const string StockAdjust = "stock.adjust";
    public const string StockTransfer = "stock.transfer";
    public const string StockCount = "stock.count";
    public const string StockSettings = "stock.settings";

    /// <summary>Confirm a posting that takes stock below zero (when the rule is warn-with-override).</summary>
    public const string StockNegativeOverride = "stock.negative_override";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        BusinessesCreate, BusinessesManage, StoresView, StoresManage, UsersView, UsersManage, UsersUnlock,
        RolesAssign, ApprovalsView, ApprovalsDecide, AuditView, SystemDiagnostics,
        CatalogView, CatalogManage, PricesManage, TaxReview, TaxApprove,
        StockView, StockAdjust, StockTransfer, StockCount, StockSettings, StockNegativeOverride,
    };
}
