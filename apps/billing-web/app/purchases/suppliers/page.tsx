'use client';

import { SuppliersPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="purchases.view">
      <SuppliersPanel />
    </RequirePermission>
  );
}
