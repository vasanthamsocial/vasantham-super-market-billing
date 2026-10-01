'use client';

import { StockPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="stock.view">
      <StockPanel />
    </RequirePermission>
  );
}
