'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import type { Counter, CounterDevice } from '../types';
import { ActionForm, ErrorText, Field, formatDateTime, Notice, text } from '../ui';
import { useStoreChoice } from '../stock/StockPanel';

/** Billing counters, and the browsers trusted to bill on each. */
export function CountersPanel() {
  const { membership } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/counters` : null;
  const counters = useApiData<Counter[]>(base);
  const { stores, storeId, setStoreId } = useStoreChoice();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selected = counters.data?.find((c) => c.id === selectedId) ?? null;
  const storeName = (id: string) => stores.find((s) => s.id === id)?.name ?? '-';

  return (
    <>
      <section className="sb-card" aria-labelledby="counters-heading">
        <header className="sb-card__header">
          <h2 id="counters-heading">Billing counters</h2>
        </header>
        <p className="sb-muted">
          Each counter numbers its own invoices (for example C1-000001). A counter PC can bill only after a manager enrols its browser here.
        </p>
        <ErrorText error={counters.error} />
        <table className="sb-table" data-testid="counters-table">
          <thead>
            <tr>
              <th>Code</th>
              <th>Name</th>
              <th>Store</th>
              <th>Devices</th>
              <th>Next invoice</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {(counters.data ?? []).map((c) => (
              <tr key={c.id} className={c.isActive ? undefined : 'sb-row--closed'}>
                <td>
                  <button type="button" className="sb-link" onClick={() => setSelectedId(c.id)}>{c.code}</button>
                </td>
                <td>{c.name}</td>
                <td>{storeName(c.storeId)}</td>
                <td>{c.activeDevices}</td>
                <td>{c.nextInvoiceNumber}</td>
                <td>{c.isActive ? 'On' : 'Switched off'}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {counters.data && counters.data.length === 0 ? <p className="sb-muted">No counters yet.</p> : null}
        {base ? (
          <details className="sb-details">
            <summary>Add a counter</summary>
            <ActionForm
              testId="create-counter-form"
              submitLabel="Add counter"
              onSubmit={async (data) => {
                if (!storeId) throw new Error('Choose the store.');
                const counter = await api.post<Counter>(base, { storeId, code: text(data, 'code'), name: text(data, 'name') });
                await counters.reload();
                setSelectedId(counter.id);
              }}
            >
              <label className="sb-field">
                <span className="sb-field__label">Store</span>
                <select className="sb-input" name="storeId" value={storeId} onChange={(e) => setStoreId(e.target.value)}>
                  {stores.map((s) => (
                    <option key={s.id} value={s.id}>{s.code} - {s.name}</option>
                  ))}
                </select>
              </label>
              <Field label="Counter code" name="code" maxLength={6} hint="1 to 6 letters or digits, for example C1. It starts every invoice number and can never change." required />
              <Field label="Counter name" name="name" required />
            </ActionForm>
          </details>
        ) : null}
      </section>
      {selected && base ? <CounterDevices base={base} counter={selected} onChanged={() => void counters.reload()} /> : null}
    </>
  );
}

function CounterDevices({ base, counter, onChanged }: { base: string; counter: Counter; onChanged: () => void }) {
  const devices = useApiData<CounterDevice[]>(`${base}/${counter.id}/devices`);
  const [enrolled, setEnrolled] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [offlineFor, setOfflineFor] = useState<string | null>(null);

  async function revoke(deviceId: string) {
    setError(null);
    try {
      await api.post(`${base}/${counter.id}/devices/${deviceId}/revoke`);
      await devices.reload();
      onChanged();
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <section className="sb-card" aria-labelledby="devices-heading">
      <header className="sb-card__header">
        <h2 id="devices-heading">Counter {counter.code}: devices</h2>
      </header>
      {enrolled ? <Notice tone="success">This browser is now counter {counter.code}. Cashiers can bill on it after signing in.</Notice> : null}
      <ErrorText error={devices.error ?? error} />
      <table className="sb-table" data-testid="devices-table">
        <thead>
          <tr>
            <th>Device</th>
            <th>Enrolled</th>
            <th>Last bill</th>
            <th>Status</th>
            <th>Without the server</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {(devices.data ?? []).map((d) => (
            <tr key={d.id} className={d.revokedAtUtc ? 'sb-row--closed' : undefined}>
              <td>{d.name}{d.isThisDevice ? ' (this browser)' : ''}</td>
              <td>{formatDateTime(d.enrolledAtUtc)} by {d.enrolledBy}</td>
              <td>{formatDateTime(d.lastSeenAtUtc)}</td>
              <td>{d.revokedAtUtc ? `Revoked ${formatDateTime(d.revokedAtUtc)}` : 'Trusted'}</td>
              <td data-testid="device-offline">
                {d.offlineMaxBills ? `Up to ${d.offlineMaxBills} bills, Rs. ${d.offlineMaxAmount}, ${d.offlineMaxHours} h` : 'No'}
                {d.revokedAtUtc ? null : (
                  <button type="button" className="sb-link" onClick={() => setOfflineFor(offlineFor === d.id ? null : d.id)}>Change</button>
                )}
              </td>
              <td>
                {d.revokedAtUtc ? null : (
                  <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => void revoke(d.id)}>
                    Revoke
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {offlineFor ? (
        <ActionForm
          testId="device-offline-form"
          submitLabel="Save"
          onSubmit={async (data) => {
            const allow = data.get('allow') === 'yes';
            await api.put(`${base}/${counter.id}/devices/${offlineFor}/offline`, allow
              ? { maxBills: Number(text(data, 'bills')), maxAmount: Number(text(data, 'amount')), maxHours: Number(text(data, 'hours')) }
              : { maxBills: null, maxAmount: null, maxHours: null });
            setOfflineFor(null);
            await devices.reload();
          }}
        >
          <Notice>
            With the counter agent installed on it, this PC can go on issuing real invoices (numbered {counter.code}/OF-...) when it cannot reach the
            server, within these limits; they are posted when the server is back. Cash, card and UPI only. Only one PC per counter.
          </Notice>
          <label className="sb-field">
            <span className="sb-field__label">Bill without the server</span>
            <select className="sb-input" name="allow" defaultValue="yes">
              <option value="yes">Yes, within these limits</option>
              <option value="no">No</option>
            </select>
          </label>
          <div className="sb-form-row">
            <Field label="Most bills" name="bills" inputMode="numeric" defaultValue="200" />
            <Field label="Most amount (Rs.)" name="amount" inputMode="decimal" defaultValue="100000" />
            <Field label="For at most (hours)" name="hours" inputMode="numeric" defaultValue="8" />
          </div>
        </ActionForm>
      ) : null}
      {counter.isActive ? (
        <ActionForm
          testId="enrol-device-form"
          submitLabel="Enrol this browser"
          onSubmit={async (data) => {
            await api.post(`${base}/${counter.id}/devices`, { name: text(data, 'name') });
            setEnrolled(true);
            await devices.reload();
            onChanged();
          }}
        >
          <Field label="Device name" name="name" defaultValue={`Counter ${counter.code} PC`} required />
          <Notice>Do this on the counter PC itself. Any device this browser was enrolled as before is revoked.</Notice>
        </ActionForm>
      ) : (
        <p className="sb-muted">Switch the counter on to enrol devices.</p>
      )}
    </section>
  );
}
