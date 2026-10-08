'use client';

import { useEffect, useState, type ReactNode } from 'react';
import { AppShell, ArchiveSetupForm, AuthenticatedApp, getSystemInfo } from '@sb/web-shared';
import { ArchiveStatus } from './ArchiveStatus';
import { nav } from './nav';

/**
 * The Owner Archive is a licensed feature: with ARCHIVE_WEB_ENABLED=true on the archive server it signs people in and
 * shows the archive; otherwise (or while the server cannot be reached) it shows only the status and why.
 */
export function ArchiveGate({ children }: { children: ReactNode }) {
  const [enabled, setEnabled] = useState<boolean | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    getSystemInfo(controller.signal)
      .then((info) => setEnabled(info.archiveWebEnabled))
      .catch(() => setEnabled(false));
    return () => controller.abort();
  }, []);

  if (enabled === null) return <p className="sb-muted">Opening the archive...</p>;
  if (!enabled) {
    return (
      <AppShell appTitle="Owner Archive">
        <h1>Owner Archive</h1>
        <ArchiveStatus />
      </AppShell>
    );
  }

  return (
    <AuthenticatedApp appTitle="Owner Archive" nav={nav} setup={<ArchiveSetupForm />}>
      {children}
    </AuthenticatedApp>
  );
}
