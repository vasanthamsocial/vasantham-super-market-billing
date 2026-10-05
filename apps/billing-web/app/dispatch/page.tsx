'use client';

import { DispatchPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="dispatch.view">
      <DispatchPanel />
    </RequirePermission>
  );
}
