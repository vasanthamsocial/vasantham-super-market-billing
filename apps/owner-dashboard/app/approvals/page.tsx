import { ApprovalsPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="approvals.view">
      <ApprovalsPanel />
    </RequirePermission>
  );
}