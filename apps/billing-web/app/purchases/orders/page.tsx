'use client';

import { PurchaseOrdersPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="purchases.view">
      <PurchaseOrdersPanel />
    </RequirePermission>
  );
}
