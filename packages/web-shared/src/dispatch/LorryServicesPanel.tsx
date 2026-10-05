'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { DispatchPermission, type Transporter } from '../types';
import { ActionForm, ErrorText, Field, optional, text } from '../ui';

/** The lorry services the business books goods with: offices that book, branches that receive, and the routes between them. */
export function LorryServicesPanel() {
  const { membership, hasPermission } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/transporters` : null;
  const transporters = useApiData<Transporter[]>(base);
  const [openedId, setOpenedId] = useState<string | null>(null);
  const canManage = hasPermission(DispatchPermission.Manage);
  if (!base) return null;
  const opened = (transporters.data ?? []).find((t) => t.id === openedId) ?? null;

  return (
    <>
      <section className="sb-card" aria-labelledby="lorry-services-heading">
        <header className="sb-card__header">
          <h2 id="lorry-services-heading">Lorry services</h2>
        </header>
        <ErrorText error={transporters.error} />
        <table className="sb-table" data-testid="transporters-table">
          <thead>
            <tr>
              <th>Code</th>
              <th>Name</th>
              <th>GSTIN</th>
              <th>Phone</th>
              <th>Offices and branches</th>
              <th>Routes</th>
              <th>Active</th>
            </tr>
          </thead>
          <tbody>
            {(transporters.data ?? []).map((t) => (
              <tr key={t.id}>
                <td>{t.code}</td>
                <td>
                  <button type="button" className="sb-link" onClick={() => setOpenedId(t.id)}>{t.name}</button>
                </td>
                <td>{t.gstin ?? '-'}</td>
                <td>{t.phone ?? '-'}</td>
                <td>{t.branches.length}</td>
                <td>{t.routes.length}</td>
                <td>{t.isActive ? 'Yes' : 'No'}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {transporters.data && transporters.data.length === 0 ? <p className="sb-muted">No lorry services yet.</p> : null}
        {canManage ? (
          <ActionForm
            submitLabel="Add lorry service"
            testId="add-transporter-form"
            onSubmit={async (data) => {
              const created = await api.post<Transporter>(base, {
                code: text(data, 'code'),
                name: text(data, 'name'),
                gstin: optional(data, 'gstin'),
                phone: optional(data, 'phone'),
                address: optional(data, 'address'),
              });
              await transporters.reload();
              setOpenedId(created.id);
            }}
          >
            <div className="sb-form-row">
              <Field label="Code" name="code" required />
              <Field label="Name" name="name" required />
              <Field label="GSTIN (if registered)" name="gstin" />
              <Field label="Phone" name="phone" />
            </div>
            <Field label="Head office address" name="address" />
          </ActionForm>
        ) : null}
      </section>

      {opened ? <TransporterCard key={opened.id} base={base} transporter={opened} canManage={canManage} onChanged={() => void transporters.reload()} /> : null}
    </>
  );
}

function TransporterCard({ base, transporter: t, canManage, onChanged }: { base: string; transporter: Transporter; canManage: boolean; onChanged: () => void }) {
  const bookingOffices = t.branches.filter((b) => b.isBookingOffice && b.isActive);
  const destinations = t.branches.filter((b) => b.isDestination && b.isActive);
  const [error, setError] = useState<unknown>(null);

  async function toggleBranch(branchId: string) {
    const b = t.branches.find((x) => x.id === branchId)!;
    setError(null);
    try {
      await api.put(`${base}/${t.id}/branches/${b.id}`, { ...b, isActive: !b.isActive });
      onChanged();
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <section className="sb-card" aria-labelledby="transporter-heading" data-testid="transporter-card">
      <header className="sb-card__header">
        <h2 id="transporter-heading">{t.name}</h2>
      </header>
      <ErrorText error={error} />
      {canManage ? (
        <details>
          <summary>Edit {t.name}</summary>
          <ActionForm
            submitLabel="Save lorry service"
            onSubmit={async (data) => {
              await api.put(`${base}/${t.id}`, {
                name: text(data, 'name'),
                gstin: optional(data, 'gstin'),
                phone: optional(data, 'phone'),
                address: optional(data, 'address'),
                isActive: data.get('isActive') === 'on',
                rowVersion: t.rowVersion,
              });
              onChanged();
            }}
          >
            <div className="sb-form-row">
              <Field label="Name" name="name" defaultValue={t.name} required />
              <Field label="GSTIN (if registered)" name="gstin" defaultValue={t.gstin ?? ''} />
              <Field label="Phone" name="phone" defaultValue={t.phone ?? ''} />
            </div>
            <Field label="Head office address" name="address" defaultValue={t.address ?? ''} />
            <label className="sb-check">
              <input type="checkbox" name="isActive" defaultChecked={t.isActive} /> In use
            </label>
          </ActionForm>
        </details>
      ) : null}

      <h3>Offices and branches</h3>
      <table className="sb-table" data-testid="branches-table">
        <thead>
          <tr>
            <th>Branch</th>
            <th>City</th>
            <th>Phone</th>
            <th>Books goods</th>
            <th>Receives goods</th>
            <th>In use</th>
            {canManage ? <th /> : null}
          </tr>
        </thead>
        <tbody>
          {t.branches.map((b) => (
            <tr key={b.id}>
              <td>{b.name}</td>
              <td>{b.city}</td>
              <td>{b.phone ?? '-'}</td>
              <td>{b.isBookingOffice ? 'Yes' : '-'}</td>
              <td>{b.isDestination ? 'Yes' : '-'}</td>
              <td>{b.isActive ? 'Yes' : 'No'}</td>
              {canManage ? (
                <td>
                  <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => void toggleBranch(b.id)}>
                    {b.isActive ? 'Stop using' : 'Use again'}
                  </button>
                </td>
              ) : null}
            </tr>
          ))}
        </tbody>
      </table>
      {canManage ? (
        <ActionForm
          submitLabel="Add branch"
          testId="add-branch-form"
          onSubmit={async (data) => {
            await api.post(`${base}/${t.id}/branches`, {
              name: text(data, 'name'),
              city: text(data, 'city'),
              phone: optional(data, 'phone'),
              address: optional(data, 'address'),
              isBookingOffice: data.get('isBookingOffice') === 'on',
              isDestination: data.get('isDestination') === 'on',
            });
            onChanged();
          }}
        >
          <div className="sb-form-row">
            <Field label="Branch name" name="name" required />
            <Field label="City" name="city" required />
            <Field label="Branch phone" name="phone" />
          </div>
          <Field label="Branch address" name="address" />
          <label className="sb-check">
            <input type="checkbox" name="isBookingOffice" /> We book goods here
          </label>
          <label className="sb-check">
            <input type="checkbox" name="isDestination" defaultChecked /> Customers collect goods here
          </label>
        </ActionForm>
      ) : null}

      <h3>Routes</h3>
      <table className="sb-table" data-testid="transporter-routes-table">
        <thead>
          <tr>
            <th>From</th>
            <th>To</th>
            <th className="sb-num">Transit days</th>
            <th>In use</th>
          </tr>
        </thead>
        <tbody>
          {t.routes.map((r) => (
            <tr key={r.id}>
              <td>{r.fromBranch}</td>
              <td>{r.toBranch}</td>
              <td className="sb-num">{r.transitDays}</td>
              <td>{r.isActive ? 'Yes' : 'No'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {t.routes.length === 0 ? <p className="sb-muted">No routes yet. A route gives the expected delivery date of a dispatch.</p> : null}
      {canManage && bookingOffices.length > 0 && destinations.length > 0 ? (
        <ActionForm
          submitLabel="Add route"
          testId="add-route-form"
          onSubmit={async (data) => {
            await api.post(`${base}/${t.id}/routes`, {
              fromBranchId: text(data, 'fromBranchId'),
              toBranchId: text(data, 'toBranchId'),
              transitDays: Number(text(data, 'transitDays') || '0'),
            });
            onChanged();
          }}
        >
          <div className="sb-form-row">
            <label className="sb-field">
              <span className="sb-field__label">From (booking office)</span>
              <select className="sb-input" name="fromBranchId">
                {bookingOffices.map((b) => <option key={b.id} value={b.id}>{b.name}, {b.city}</option>)}
              </select>
            </label>
            <label className="sb-field">
              <span className="sb-field__label">To (destination branch)</span>
              <select className="sb-input" name="toBranchId">
                {destinations.map((b) => <option key={b.id} value={b.id}>{b.name}, {b.city}</option>)}
              </select>
            </label>
            <Field label="Transit days" name="transitDays" inputMode="numeric" defaultValue="1" />
          </div>
        </ActionForm>
      ) : null}
    </section>
  );
}
