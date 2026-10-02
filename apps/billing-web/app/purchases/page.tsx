'use client';

import { GrnEntryPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="purchases.manage">
      <GrnEntryPanel />
    </RequirePermission>
  );
}
