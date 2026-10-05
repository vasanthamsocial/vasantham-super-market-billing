import type { Metadata, Viewport } from 'next';
import type { ReactNode } from 'react';
import { AuthenticatedApp } from '@sb/web-shared';
import '@sb/web-shared/styles.css';
import { nav } from './nav';
import { ServiceWorker } from './ServiceWorker';

export const metadata: Metadata = {
  title: 'SupermarketBilling - Collections',
  description: 'Route visits, party balances and collection receipts for collection personnel.',
};

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  viewportFit: 'cover',
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en-IN">
      <body>
        <ServiceWorker />
        <AuthenticatedApp appTitle="Collections" variant="phone" nav={nav} offline>
          {children}
        </AuthenticatedApp>
      </body>
    </html>
  );
}