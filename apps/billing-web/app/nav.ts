import type { NavItem } from '@sb/web-shared';

export const nav: NavItem[] = [
  { href: '/', label: 'Home' },
  { href: '/pos', label: 'Billing (POS)', permission: 'pos.bill' },
  { href: '/sales', label: 'Sales invoices', permission: 'sales.view' },
  { href: '/shifts', label: 'Shifts', permission: 'sales.view' },
  { href: '/catalog', label: 'Products', permission: 'catalog.view' },
  { href: '/catalog/settings', label: 'Catalogue settings', permission: 'catalog.view' },
  { href: '/stock', label: 'Stock', permission: 'stock.view' },
  { href: '/stock/documents', label: 'Stock documents', permission: 'stock.view' },
  { href: '/stock/settings', label: 'Stock settings', permission: 'stock.view' },
  { href: '/purchases', label: 'Receive goods', permission: 'purchases.manage' },
  { href: '/purchases/receipts', label: 'Goods receipts', permission: 'purchases.view' },
  { href: '/purchases/orders', label: 'Purchase orders', permission: 'purchases.view' },
  { href: '/purchases/suppliers', label: 'Suppliers', permission: 'purchases.view' },
  { href: '/admin/stores', label: 'Stores', permission: 'stores.view' },
  { href: '/admin/counters', label: 'Counters', permission: 'counters.manage' },
  { href: '/admin/users', label: 'Users', permission: 'users.view' },
  { href: '/admin/approvals', label: 'Approvals', permission: 'approvals.view' },
  { href: '/admin/audit', label: 'Audit trail', permission: 'audit.view' },
  { href: '/admin/tax', label: 'GST registration', permission: 'stores.view' },
  { href: '/account', label: 'My account' },
];