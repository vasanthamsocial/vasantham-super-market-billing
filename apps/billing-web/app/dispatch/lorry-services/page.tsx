'use client';

import { LorryServicesPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="dispatch.view">
      <LorryServicesPanel />
    </RequirePermission>
  );
}
