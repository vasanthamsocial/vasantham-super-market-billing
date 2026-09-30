'use client';

import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { Permission, type Store } from '../types';
import { ActionForm, ErrorText, Field, optional, text } from '../ui';
import { useApiData } from './useApiData';

export function StoresPanel() {
  const { membership, hasPermission } = useAuth();
  const path = membership ? `/api/v1/businesses/${membership.businessId}/stores` : null;
  const { data: stores, error, reload } = useApiData<Store[]>(path);

  return (
    <section className="sb-card" aria-labelledby="stores-heading">
      <header className="sb-card__header">
        <h2 id="stores-heading">Stores</h2>
      </header>
      <ErrorText error={error} />
      <table className="sb-table" data-testid="stores-table">
        <thead>
          <tr>
            <th>Code</th>
            <th>Name</th>
            <th>State</th>
            <th>GSTIN</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {(stores ?? []).map((store) => (
            <tr key={store.id}>
              <td>{store.code}</td>
              <td>{store.name}</td>
              <td>{store.stateCode}</td>
              <td>{store.gstin ?? '-'}</td>
              <td>{store.isActive ? 'Active' : 'Inactive'}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {hasPermission(Permission.StoresManage) && membership ? (
        <details className="sb-details">
          <summary>Add a store</summary>
          <ActionForm
            testId="create-store-form"
            submitLabel="Add store"
            onSubmit={async (data) => {
              await api.post(`/api/v1/businesses/${membership.businessId}/stores`, {
                code: text(data, 'code'),
                name: text(data, 'name'),
                stateCode: text(data, 'stateCode'),
                gstin: optional(data, 'gstin'),
                address: optional(data, 'address'),
              });
              await reload();
            }}
          >
            <Field label="Store code" name="code" placeholder="e.g. S02" required />
            <Field label="Store name" name="name" required />
            <Field label="GST state code" name="stateCode" inputMode="numeric" required />
            <Field label="GSTIN (if different from the business)" name="gstin" />
            <Field label="Address" name="address" />
          </ActionForm>
        </details>
      ) : null}
    </section>
  );
}
