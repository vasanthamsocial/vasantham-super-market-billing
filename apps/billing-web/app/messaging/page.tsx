'use client';

import { MessagingPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="messaging.view">
      <MessagingPanel />
    </RequirePermission>
  );
}
