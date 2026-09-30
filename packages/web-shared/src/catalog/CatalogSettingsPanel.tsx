'use client';

import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { CatalogPermission, type CategoryInfo, type CustomerGroupInfo, type NamedItem, type UnitInfo } from '../types';
import { ActionForm, ErrorText, Field, text } from '../ui';

/** Categories, brands, units and customer groups. */
export function CatalogSettingsPanel() {
  const { membership, hasPermission } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/catalog` : null;
  const categories = useApiData<CategoryInfo[]>(base ? `${base}/categories` : null);
  const brands = useApiData<NamedItem[]>(base ? `${base}/brands` : null);
  const units = useApiData<UnitInfo[]>(base ? `${base}/units` : null);
  const groups = useApiData<CustomerGroupInfo[]>(base ? `${base}/customer-groups` : null);
  const canManage = hasPermission(CatalogPermission.Manage);
  const canPrice = hasPermission(CatalogPermission.PricesManage);
  if (!base) return null;

  return (
    <div className="sb-grid-2">
      <section className="sb-card" aria-labelledby="categories-heading">
        <h2 id="categories-heading">Categories</h2>
        <ErrorText error={categories.error} />
        <ul className="sb-plain-list">{(categories.data ?? []).map((c) => <li key={c.id}>{c.name}</li>)}</ul>
        {canManage ? (
          <ActionForm submitLabel="Add category" onSubmit={async (data) => {
            await api.post(`${base}/categories`, { name: text(data, 'name'), parentId: null });
            await categories.reload();
          }}>
            <Field label="New category" name="name" required />
          </ActionForm>
        ) : null}
      </section>

      <section className="sb-card" aria-labelledby="brands-heading">
        <h2 id="brands-heading">Brands</h2>
        <ErrorText error={brands.error} />
        <ul className="sb-plain-list">{(brands.data ?? []).map((b) => <li key={b.id}>{b.name}</li>)}</ul>
        {canManage ? (
          <ActionForm submitLabel="Add brand" onSubmit={async (data) => {
            await api.post(`${base}/brands`, { name: text(data, 'name') });
            await brands.reload();
          }}>
            <Field label="New brand" name="name" required />
          </ActionForm>
        ) : null}
      </section>

      <section className="sb-card" aria-labelledby="units-heading">
        <h2 id="units-heading">Units</h2>
        <ErrorText error={units.error} />
        <ul className="sb-plain-list">
          {(units.data ?? []).map((u) => <li key={u.id}>{u.code} - {u.name} ({u.decimalPlaces} decimals)</li>)}
        </ul>
        {canManage ? (
          <ActionForm submitLabel="Add unit" onSubmit={async (data) => {
            await api.post(`${base}/units`, { code: text(data, 'code'), name: text(data, 'name'), decimalPlaces: Number(text(data, 'decimals') || 0) });
            await units.reload();
          }}>
            <Field label="Code" name="code" required />
            <Field label="Name" name="name" required />
            <Field label="Decimal places" name="decimals" inputMode="numeric" defaultValue="0" />
          </ActionForm>
        ) : null}
      </section>

      <section className="sb-card" aria-labelledby="groups-heading">
        <h2 id="groups-heading">Customer groups</h2>
        <p className="sb-muted">Groups with their own prices, for example hotels or retailers on a route.</p>
        <ErrorText error={groups.error} />
        <ul className="sb-plain-list">{(groups.data ?? []).map((g) => <li key={g.id}>{g.code} - {g.name}</li>)}</ul>
        {canPrice ? (
          <ActionForm submitLabel="Add group" onSubmit={async (data) => {
            await api.post(`${base}/customer-groups`, { code: text(data, 'code'), name: text(data, 'name') });
            await groups.reload();
          }}>
            <Field label="Code" name="code" required />
            <Field label="Name" name="name" required />
          </ActionForm>
        ) : null}
      </section>
    </div>
  );
}
