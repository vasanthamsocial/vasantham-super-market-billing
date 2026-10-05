'use client';

import { ReportsPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="reports.view">
      <ReportsPanel />
    </RequirePermission>
  );
}
