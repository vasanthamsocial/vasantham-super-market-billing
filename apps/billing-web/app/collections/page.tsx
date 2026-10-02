'use client';

import { CollectionsPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="collections.view">
      <CollectionsPanel />
    </RequirePermission>
  );
}
