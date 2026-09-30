import type { Metadata, Viewport } from 'next';
import type { ReactNode } from 'react';
import { AuthenticatedApp } from '@sb/web-shared';
import '@sb/web-shared/styles.css';
import { nav } from './nav';

export const metadata: Metadata = {
  title: 'SupermarketBilling - Billing and Operations',
  description: 'POS billing, purchasing, inventory, collections, dispatch, accounts and reports.',
};

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en-IN">
      <body>
        <AuthenticatedApp appTitle="Billing and Operations" variant="desktop" nav={nav}>
          {children}
        </AuthenticatedApp>
      </body>
    </html>
  );
}