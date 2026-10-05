'use client';

import { MonthClosePanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="months.view">
      <MonthClosePanel />
    </RequirePermission>
  );
}
