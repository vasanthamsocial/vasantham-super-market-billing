'use client';

import { DebtorsPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="debtors.view">
      <DebtorsPanel />
    </RequirePermission>
  );
}
