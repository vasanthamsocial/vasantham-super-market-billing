import type { Metadata, Viewport } from 'next';
import type { ReactNode } from 'react';
import '@sb/web-shared/styles.css';

export const metadata: Metadata = {
  title: 'SupermarketBilling - Owner Dashboard',
  description: 'Live business monitoring, reports, approvals and configuration for owners.',
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
