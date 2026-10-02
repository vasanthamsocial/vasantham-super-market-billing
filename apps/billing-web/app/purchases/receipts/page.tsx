'use client';

import { GrnListPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="purchases.view">
      <GrnListPanel />
    </RequirePermission>
  );
}
