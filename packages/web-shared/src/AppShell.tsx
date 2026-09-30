'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import type { ReactNode } from 'react';
import { useAuth } from './auth/AuthContext';

export interface PlannedModule {
  name: string;
  description: string;
  stage: number;
}

export interface NavItem {
  href: string;
  label: string;
  /** Shown only when the user holds this permission in the selected business. */
  permission?: string;
}

/** Common page frame: product name, application name, optional navigation and header actions. */
export function AppShell({
  appTitle,
  variant = 'desktop',
  nav,
  actions,
  children,
}: {
  appTitle: string;
  variant?: 'desktop' | 'phone';
  nav?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className={`sb-shell sb-shell--${variant}`}>
      <header className="sb-topbar">
        <span className="sb-topbar__product">SupermarketBilling</span>
        <span className="sb-topbar__app">{appTitle}</span>
        <span className="sb-topbar__spacer" />
        {actions}
      </header>
      {nav}
      <main className="sb-main">{children}</main>
    </div>
  );
}

/** Navigation links, filtered by the signed-in user's permissions. */
export function MainNav({ items }: { items: NavItem[] }) {
  const pathname = usePathname();
  const { hasPermission } = useAuth();
  const visible = items.filter((item) => !item.permission || hasPermission(item.permission));
  return (
    <nav className="sb-nav" aria-label="Main">
      {visible.map((item) => (
        <Link key={item.href} href={item.href} className={pathname === item.href ? 'sb-nav__link sb-nav__link--active' : 'sb-nav__link'}>
          {item.label}
        </Link>
      ))}
    </nav>
  );
}

/** Signed-in user, business switcher (when the user works for several businesses) and sign out. */
export function UserMenu() {
  const { me, membership, selectBusiness, logout } = useAuth();
  if (!me) return null;
  return (
    <div className="sb-user-menu">
      {me.memberships.length > 1 ? (
        <select
          className="sb-input sb-input--inline"
          aria-label="Business"
          value={membership?.businessId}
          onChange={(event) => selectBusiness(event.target.value)}
        >
          {me.memberships.map((m) => (
            <option key={m.businessId} value={m.businessId}>{m.businessName}</option>
          ))}
        </select>
      ) : membership ? (
        <span className="sb-muted">{membership.businessName}</span>
      ) : null}
      <span data-testid="signed-in-user">{me.displayName}</span>
      <button type="button" className="sb-button sb-button--small sb-button--secondary" onClick={() => void logout()}>
        Sign out
      </button>
    </div>
  );
}

/**
 * Lists the modules this application will provide and the delivery stage for each.
 * Modules are shown as planned until their stage is complete; nothing here pretends to work.
 */
export function PlannedModules({ modules }: { modules: PlannedModule[] }) {
  return (
    <section className="sb-card" aria-labelledby="modules-heading">
      <header className="sb-card__header">
        <h2 id="modules-heading">Modules</h2>
      </header>
      <ul className="sb-module-list">
        {modules.map((module) => (
          <li key={module.name} className="sb-module">
            <div>
              <strong>{module.name}</strong>
              <p className="sb-muted">{module.description}</p>
            </div>
            <span className="sb-badge sb-badge--planned">Stage {module.stage}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** Wraps an admin page: renders the content only if the user holds the permission. */
export function RequirePermission({ permission, children }: { permission: string; children: ReactNode }) {
  const { hasPermission } = useAuth();
  if (!hasPermission(permission)) {
    return (
      <section className="sb-card sb-notice" role="status" data-testid="no-permission">
        <h2>Not available</h2>
        <p className="sb-muted">Your role does not include this area. Ask a manager if you need access.</p>
      </section>
    );
  }
  return <>{children}</>;
}
