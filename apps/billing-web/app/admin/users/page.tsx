import { UsersPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="users.view">
      <UsersPanel />
    </RequirePermission>
  );
}