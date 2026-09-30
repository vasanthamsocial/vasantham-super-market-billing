import { RequirePermission, TaxRegistrationPanel } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="stores.view">
      <TaxRegistrationPanel />
    </RequirePermission>
  );
}