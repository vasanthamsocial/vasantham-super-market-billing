import { DomainError } from '../core/errors.js';

/** Every permission (identical to the .NET API's Permissions). */
export const P = {
  BusinessesCreate: 'businesses.create',
  BusinessesManage: 'businesses.manage',
  StoresView: 'stores.view',
  StoresManage: 'stores.manage',
  UsersView: 'users.view',
  UsersManage: 'users.manage',
  UsersUnlock: 'users.unlock',
  RolesAssign: 'roles.assign',
  ApprovalsView: 'approvals.view',
  ApprovalsDecide: 'approvals.decide',
  AuditView: 'audit.view',
  SystemDiagnostics: 'system.diagnostics',
  CatalogView: 'catalog.view',
  CatalogManage: 'catalog.manage',
  PricesManage: 'prices.manage',
  TaxReview: 'tax.review',
  TaxApprove: 'tax.approve',
  StockView: 'stock.view',
  StockAdjust: 'stock.adjust',
  StockTransfer: 'stock.transfer',
  StockCount: 'stock.count',
  StockSettings: 'stock.settings',
  StockNegativeOverride: 'stock.negative_override',
  PosBill: 'pos.bill',
  PosPriceOverride: 'pos.price_override',
  PosDiscount: 'pos.discount',
  PosReturn: 'pos.return',
  PosCreditOverride: 'pos.credit_override',
  CountersManage: 'counters.manage',
  ShiftsManage: 'shifts.manage',
  SalesView: 'sales.view',
  SuppliersManage: 'suppliers.manage',
  PurchasesView: 'purchases.view',
  PurchasesManage: 'purchases.manage',
  PurchasesApprove: 'purchases.approve',
  DebtorsView: 'debtors.view',
  DebtorsManage: 'debtors.manage',
  PayablesManage: 'payables.manage',
  ReceivablesManage: 'receivables.manage',
  LedgersAdjust: 'ledgers.adjust',
  LedgersApprove: 'ledgers.approve',
  CollectionsView: 'collections.view',
  CollectionsManage: 'collections.manage',
  CollectionsCollect: 'collections.collect',
  CollectionsReceive: 'collections.receive',
  CollectionsAllocate: 'collections.allocate',
  MessagingView: 'messaging.view',
  MessagingManage: 'messaging.manage',
  DispatchView: 'dispatch.view',
  DispatchManage: 'dispatch.manage',
  ReportsView: 'reports.view',
  ReportsProfit: 'reports.profit',
  MonthsView: 'months.view',
  MonthsClose: 'months.close',
} as const;

export const AllPermissions: ReadonlySet<string> = new Set(Object.values(P));

export interface RoleDefinition {
  code: string;
  name: string;
  isPrivileged: boolean;
  businessWideOnly: boolean;
  permissions: ReadonlySet<string>;
}

const role = (code: string, name: string, isPrivileged: boolean, businessWideOnly: boolean, permissions: Iterable<string>): RoleDefinition => ({
  code,
  name,
  isPrivileged,
  businessWideOnly,
  permissions: new Set(permissions),
});

export const R = {
  Owner: 'owner',
  Manager: 'manager',
  Cashier: 'cashier',
  InventoryOperator: 'inventory_operator',
  PurchaseOperator: 'purchase_operator',
  Accountant: 'accountant',
  CollectionManager: 'collection_manager',
  CollectionPerson: 'collection_person',
  Auditor: 'auditor',
  SupportAdmin: 'support_admin',
} as const;

/** The system roles of the specification and what each may do (identical to the .NET API's Roles). */
const definitions = new Map<string, RoleDefinition>([
  [R.Owner, role(R.Owner, 'Owner', true, true, AllPermissions)],
  [R.Manager, role(R.Manager, 'Manager', true, false, [
    P.StoresView, P.StoresManage, P.UsersView, P.UsersManage, P.UsersUnlock, P.RolesAssign, P.ApprovalsView, P.ApprovalsDecide, P.AuditView,
    P.CatalogView, P.CatalogManage, P.PricesManage, P.TaxApprove, P.StockView, P.StockAdjust, P.StockTransfer, P.StockCount, P.StockSettings,
    P.StockNegativeOverride, P.PosBill, P.PosPriceOverride, P.PosDiscount, P.PosReturn, P.PosCreditOverride, P.CountersManage, P.ShiftsManage, P.SalesView,
    P.SuppliersManage, P.PurchasesView, P.PurchasesManage, P.PurchasesApprove, P.DebtorsView, P.DebtorsManage, P.PayablesManage, P.ReceivablesManage,
    P.LedgersAdjust, P.LedgersApprove, P.CollectionsView, P.CollectionsManage, P.CollectionsReceive, P.CollectionsAllocate, P.MessagingView,
    P.MessagingManage, P.DispatchView, P.DispatchManage, P.ReportsView, P.ReportsProfit, P.MonthsView,
  ])],
  [R.Accountant, role(R.Accountant, 'Accountant', true, true, [
    P.StoresView, P.ApprovalsView, P.ApprovalsDecide, P.AuditView, P.CatalogView, P.TaxReview, P.StockView, P.SalesView, P.PurchasesView,
    P.DebtorsView, P.DebtorsManage, P.PayablesManage, P.ReceivablesManage, P.LedgersAdjust, P.CollectionsView, P.CollectionsReceive, P.MessagingView,
    P.DispatchView, P.ReportsView, P.ReportsProfit, P.MonthsView, P.MonthsClose,
  ])],
  [R.Auditor, role(R.Auditor, 'Auditor', true, true, [
    P.StoresView, P.UsersView, P.ApprovalsView, P.AuditView, P.CatalogView, P.StockView, P.SalesView, P.PurchasesView, P.DebtorsView,
    P.CollectionsView, P.MessagingView, P.DispatchView, P.ReportsView, P.ReportsProfit, P.MonthsView,
  ])],
  [R.SupportAdmin, role(R.SupportAdmin, 'Restricted support administrator', true, true, [P.StoresView, P.UsersUnlock, P.SystemDiagnostics])],
  [R.Cashier, role(R.Cashier, 'Cashier', false, false, [P.StoresView, P.CatalogView, P.StockView, P.PosBill, P.SalesView])],
  [R.InventoryOperator, role(R.InventoryOperator, 'Inventory operator', false, false, [
    P.StoresView, P.CatalogView, P.CatalogManage, P.StockView, P.StockAdjust, P.StockTransfer, P.StockCount, P.DispatchView, P.DispatchManage,
  ])],
  [R.PurchaseOperator, role(R.PurchaseOperator, 'Purchase operator', false, false, [
    P.StoresView, P.CatalogView, P.StockView, P.SuppliersManage, P.PurchasesView, P.PurchasesManage,
  ])],
  [R.CollectionManager, role(R.CollectionManager, 'Collection manager', false, false, [
    P.StoresView, P.UsersView, P.CatalogView, P.DebtorsView, P.ReceivablesManage, P.CollectionsView, P.CollectionsManage, P.CollectionsReceive,
    P.CollectionsAllocate,
  ])],
  [R.CollectionPerson, role(R.CollectionPerson, 'Collection person', false, false, [P.StoresView, P.CollectionsCollect])],
]);

export const Roles = {
  all(): RoleDefinition[] {
    return [...definitions.values()];
  },
  exists(code: string): boolean {
    return definitions.has(code);
  },
  get(code: string): RoleDefinition {
    const found = definitions.get(code);
    if (!found) throw new DomainError('role.unknown', `Unknown role '${code}'.`);
    return found;
  },
};

/** An active role grant: the role, in a business, optionally limited to one store. */
export interface ActiveGrant {
  assignmentId: string;
  roleCode: string;
  businessId: string;
  storeId: string | null;
}

/** Business-level checks need a business-wide grant; store-level checks also accept a grant for that store. */
export function covers(grants: readonly ActiveGrant[], permission: string, businessId: string, storeId: string | null): boolean {
  return grants.some((g) => g.businessId === businessId && (g.storeId === null || (storeId !== null && g.storeId === storeId))
    && Roles.get(g.roleCode).permissions.has(permission));
}

/** Permissions the grants give at a scope (business-wide grants, plus a grant for that exact store). */
export function permissionsAt(grants: readonly ActiveGrant[], businessId: string, storeId: string | null): Set<string> {
  const held = new Set<string>();
  for (const g of grants) {
    if (g.businessId === businessId && (g.storeId === null || (storeId !== null && g.storeId === storeId))) {
      for (const p of Roles.get(g.roleCode).permissions) held.add(p);
    }
  }
  return held;
}

function isSubset(of: ReadonlySet<string>, held: ReadonlySet<string>): boolean {
  for (const p of of) if (!held.has(p)) return false;
  return true;
}

/** Nobody can grant beyond their own permissions: the actor holds the permission and everything the role gives. */
export function canGrant(actor: readonly ActiveGrant[], permission: string, role: RoleDefinition, businessId: string, storeId: string | null): boolean {
  const held = permissionsAt(actor, businessId, storeId);
  return held.has(permission) && isSubset(role.permissions, held);
}

/** The actor may manage the target only where, for every grant the target holds, they hold the permission and at least the same. */
export function canManage(actor: readonly ActiveGrant[], target: readonly ActiveGrant[], permission: string): boolean {
  return target.every((t) => {
    const held = permissionsAt(actor, t.businessId, t.storeId);
    return held.has(permission) && isSubset(Roles.get(t.roleCode).permissions, held);
  });
}

/** A role grant's approver: approvals.decide business-wide and every permission the role gives. */
export function canApproveGrant(grants: readonly ActiveGrant[], businessId: string, storeId: string | null, role: RoleDefinition): boolean {
  return covers(grants, P.ApprovalsDecide, businessId, null) && isSubset(role.permissions, permissionsAt(grants, businessId, storeId));
}
