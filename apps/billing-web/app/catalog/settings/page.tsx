import { CatalogSettingsPanel, RequirePermission } from '@sb/web-shared';

export default function Page() {
  return (
    <RequirePermission permission="catalog.view">
      <CatalogSettingsPanel />
    </RequirePermission>
  );
}