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

    /// <summary>Sell on account beyond a debtor's credit limit, or approve a cashier doing so.</summary>
    public const string PosCreditOverride = "pos.credit_override";

    public const string CountersManage = "counters.manage";

    /// <summary>See every shift's figures, review drawer differences, close a shift for a cashier, pay out cash.</summary>
    public const string ShiftsManage = "shifts.manage";
    public const string SalesView = "sales.view";

    public const string SuppliersManage = "suppliers.manage";
    public const string PurchasesView = "purchases.view";

    /// <summary>Enter goods receipts and change purchase settings.</summary>
    public const string PurchasesManage = "purchases.manage";

    /// <summary>Approve receipts with a large cost change or a loss-leader price.</summary>
    public const string PurchasesApprove = "purchases.approve";

    public const string DebtorsView = "debtors.view";

    /// <summary>Add and change debtors (customers on credit), their credit limits and account status.</summary>
    public const string DebtorsManage = "debtors.manage";

    /// <summary>Pay suppliers and apply payments to their bills.</summary>
    public const string PayablesManage = "payables.manage";

    /// <summary>Take payments from debtors and apply them to invoices.</summary>
    public const string ReceivablesManage = "receivables.manage";

    /// <summary>Enter opening balances and request corrections to supplier and debtor accounts.</summary>
    public const string LedgersAdjust = "ledgers.adjust";

    /// <summary>Approve corrections to supplier and debtor accounts (never one's own).</summary>
    public const string LedgersApprove = "ledgers.approve";

    /// <summary>See routes, collection plans and every collector's day list.</summary>
    public const string CollectionsView = "collections.view";

    /// <summary>Set up routes and collection plans, assign visits, record absences.</summary>
    public const string CollectionsManage = "collections.manage";

    /// <summary>Collect from one's own parties (the Collection App).</summary>
    public const string CollectionsCollect = "collections.collect";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        BusinessesCreate, BusinessesManage, StoresView, StoresManage, UsersView, UsersManage, UsersUnlock,
        RolesAssign, ApprovalsView, ApprovalsDecide, AuditView, SystemDiagnostics,
        CatalogView, CatalogManage, PricesManage, TaxReview, TaxApprove,
        StockView, StockAdjust, StockTransfer, StockCount, StockSettings, StockNegativeOverride,
        PosBill, PosPriceOverride, PosDiscount, PosReturn, PosCreditOverride, CountersManage, ShiftsManage, SalesView,
        SuppliersManage, PurchasesView, PurchasesManage, PurchasesApprove,
        DebtorsView, DebtorsManage, PayablesManage, ReceivablesManage, LedgersAdjust, LedgersApprove,
        CollectionsView, CollectionsManage, CollectionsCollect,
    };
}
