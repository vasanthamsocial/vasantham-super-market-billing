'use client';

import { PackingPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="dispatch.view">
      <PackingPanel />
    </RequirePermission>
  );
}
