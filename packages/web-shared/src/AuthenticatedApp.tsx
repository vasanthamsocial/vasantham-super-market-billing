'use client';

import type { ReactNode } from 'react';
import { AppShell, MainNav, UserMenu, type NavItem } from './AppShell';
import { AuthProvider } from './auth/AuthContext';
import { AuthGate } from './auth/AuthGate';

/** Sign-in handling plus the standard frame, for applications that require a signed-in user. */
export function AuthenticatedApp({
  appTitle,
  variant = 'desktop',
  nav,
  offline = false,
  setup,
  children,
}: {
  appTitle: string;
  variant?: 'desktop' | 'phone';
  nav: NavItem[];
  /** Opens without signal for the collector of an enrolled phone (Collection App only). */
  offline?: boolean;
  /** First-time setup to show instead of the store's (Owner Archive). */
  setup?: ReactNode;
  children: ReactNode;
}) {
  return (
    <AuthProvider offline={offline}>
      <AuthGate appTitle={appTitle} setup={setup}>
        <AppShell appTitle={appTitle} variant={variant} nav={<MainNav items={nav} />} actions={<UserMenu />}>
          {children}
        </AppShell>
      </AuthGate>
    </AuthProvider>
  );
}
