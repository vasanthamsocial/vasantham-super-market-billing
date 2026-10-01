'use client';

import { InvoicesPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="sales.view">
      <InvoicesPanel />
    </RequirePermission>
  );
}
