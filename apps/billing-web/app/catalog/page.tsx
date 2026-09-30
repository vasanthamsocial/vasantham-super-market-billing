'use client';

import { ProductsPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="catalog.view">
      <ProductsPanel detailHref={(id) => `/catalog/${id}`} />
    </RequirePermission>
  );
}