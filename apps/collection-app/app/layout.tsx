import type { Metadata, Viewport } from 'next';
import type { ReactNode } from 'react';
import '@sb/web-shared/styles.css';

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
      <body>{children}</body>
    </html>
  );
}
