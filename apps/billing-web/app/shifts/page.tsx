'use client';

import { RequirePermission, ShiftsPanel } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="sales.view">
      <ShiftsPanel />
    </RequirePermission>
  );
}
