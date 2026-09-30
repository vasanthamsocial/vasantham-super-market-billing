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
  children,
}: {
  appTitle: string;
  variant?: 'desktop' | 'phone';
  nav: NavItem[];
  children: ReactNode;
}) {
  return (
    <AuthProvider>
      <AuthGate appTitle={appTitle}>
        <AppShell appTitle={appTitle} variant={variant} nav={<MainNav items={nav} />} actions={<UserMenu />}>
          {children}
        </AppShell>
      </AuthGate>
    </AuthProvider>
  );
}
