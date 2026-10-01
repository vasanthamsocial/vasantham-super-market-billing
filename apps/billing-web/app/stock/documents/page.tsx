'use client';

import { StockDocumentsPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="stock.view">
      <StockDocumentsPanel />
    </RequirePermission>
  );
}
