import type { Metadata, Viewport } from 'next';
import type { ReactNode } from 'react';
import '@sb/web-shared/styles.css';

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
      <body>{children}</body>
    </html>
  );
}
