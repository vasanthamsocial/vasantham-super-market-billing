import type { Metadata, Viewport } from 'next';
import type { ReactNode } from 'react';
import { AuthenticatedApp } from '@sb/web-shared';
import '@sb/web-shared/styles.css';
import { nav } from './nav';

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
      <body>
        <AuthenticatedApp appTitle="Owner Dashboard" variant="desktop" nav={nav}>
          {children}
        </AuthenticatedApp>
      </body>
    </html>
  );
}