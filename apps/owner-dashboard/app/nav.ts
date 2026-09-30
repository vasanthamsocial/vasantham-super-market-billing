import type { NavItem } from '@sb/web-shared';

export const nav: NavItem[] = [
  { href: '/', label: 'Overview' },
  { href: '/approvals', label: 'Approvals', permission: 'approvals.view' },
  { href: '/users', label: 'Users', permission: 'users.view' },
  { href: '/stores', label: 'Stores', permission: 'stores.view' },
  { href: '/tax', label: 'GST registration', permission: 'stores.view' },
  { href: '/audit', label: 'Audit trail', permission: 'audit.view' },
  { href: '/account', label: 'My account' },
];