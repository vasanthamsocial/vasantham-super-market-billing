import { AuditPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="audit.view">
      <AuditPanel />
    </RequirePermission>
  );
}