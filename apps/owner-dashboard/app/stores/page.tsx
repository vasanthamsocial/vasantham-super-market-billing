import { StoresPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="stores.view">
      <StoresPanel />
    </RequirePermission>
  );
}