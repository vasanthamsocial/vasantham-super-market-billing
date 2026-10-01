'use client';

import { StockSettingsPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="stock.view">
      <StockSettingsPanel />
    </RequirePermission>
  );
}
