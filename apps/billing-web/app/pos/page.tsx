'use client';

import { PosScreen, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="pos.bill">
      <PosScreen />
    </RequirePermission>
  );
}
