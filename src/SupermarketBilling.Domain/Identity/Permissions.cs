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

    /// <summary>Bill on a counter (also needs an enrolled counter device).</summary>
    public const string PosBill = "pos.bill";

    /// <summary>Sell at a price other than the price rules give, or approve a cashier doing so.</summary>
    public const string PosPriceOverride = "pos.price_override";

    /// <summary>Give item or bill discounts, or approve a cashier doing so.</summary>
    public const string PosDiscount = "pos.discount";

    /// <summary>Take back goods and refund (credit notes), or approve a cashier doing so.</summary>
    public const string PosReturn = "pos.return";

    public const string CountersManage = "counters.manage";

    /// <summary>See every shift's figures, review drawer differences, close a shift for a cashier, pay out cash.</summary>
    public const string ShiftsManage = "shifts.manage";
    public const string SalesView = "sales.view";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        BusinessesCreate, BusinessesManage, StoresView, StoresManage, UsersView, UsersManage, UsersUnlock,
        RolesAssign, ApprovalsView, ApprovalsDecide, AuditView, SystemDiagnostics,
        CatalogView, CatalogManage, PricesManage, TaxReview, TaxApprove,
        StockView, StockAdjust, StockTransfer, StockCount, StockSettings, StockNegativeOverride,
        PosBill, PosPriceOverride, PosDiscount, PosReturn, CountersManage, ShiftsManage, SalesView,
    };
}
