'use client';

import { MonthClosePanel, RequirePermission, RetentionPanel } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="months.view">
      <MonthClosePanel />
      <RetentionPanel />
    </RequirePermission>
  );
}
