import type { NavItem } from '@sb/web-shared';

export const nav: NavItem[] = [
  { href: '/', label: 'Home' },
  { href: '/catalog', label: 'Products', permission: 'catalog.view' },
  { href: '/catalog/settings', label: 'Catalogue settings', permission: 'catalog.view' },
  { href: '/admin/stores', label: 'Stores', permission: 'stores.view' },
  { href: '/admin/users', label: 'Users', permission: 'users.view' },
  { href: '/admin/approvals', label: 'Approvals', permission: 'approvals.view' },
  { href: '/admin/audit', label: 'Audit trail', permission: 'audit.view' },
  { href: '/admin/tax', label: 'GST registration', permission: 'stores.view' },
  { href: '/account', label: 'My account' },
];