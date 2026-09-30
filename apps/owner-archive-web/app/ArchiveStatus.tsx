'use client';

import { useState } from 'react';
import { SystemStatus, type SystemInfo } from '@sb/web-shared';

/** The archive is a licensed feature. The API's ARCHIVE_WEB_ENABLED setting is authoritative. */
export function ArchiveStatus() {
  const [info, setInfo] = useState<SystemInfo | null>(null);

  return (
    <>
      {info && !info.archiveWebEnabled ? (
        <section className="sb-card sb-notice" role="status" data-testid="archive-disabled">
          <h2>Owner Archive is not enabled</h2>
          <p className="sb-muted">
            This optional feature is available only to businesses that have purchased it. It is enabled with
            ARCHIVE_WEB_ENABLED=true on the archive server. Daily billing never depends on it.
          </p>
        </section>
      ) : null}
      <SystemStatus onInfo={setInfo} />
    </>
  );
}
