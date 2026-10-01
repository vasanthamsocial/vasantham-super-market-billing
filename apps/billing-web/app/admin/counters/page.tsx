'use client';

import { CountersPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="counters.manage">
      <CountersPanel />
    </RequirePermission>
  );
}
