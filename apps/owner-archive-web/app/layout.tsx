import type { Metadata } from 'next';
import type { ReactNode } from 'react';
import '@sb/web-shared/styles.css';
import { ArchiveGate } from './ArchiveGate';

export const metadata: Metadata = {
  title: 'SupermarketBilling - Owner Archive',
  description: 'Read-only long-term historical reporting on a separate archive database.',
};

// Desktop-oriented by design: no mobile viewport configuration and no PWA manifest.
export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en-IN">
      <body>
        <ArchiveGate>{children}</ArchiveGate>
      </body>
    </html>
  );
}
