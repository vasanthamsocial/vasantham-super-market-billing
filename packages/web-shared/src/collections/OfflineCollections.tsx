'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  history,
  loadDevice,
  offlineSupported,
  pending,
  synchronise,
  unreachable,
  type OfflineDevice,
  type QueuedCollection,
  type SyncedCollection,
  type SyncResult,
} from '../offline/collectionQueue';
import { CollectionPermission, ReceiptMethodLabels, type CollectionDevice, type Collector, type OfflineSubmission } from '../types';
import { ActionForm, ErrorText, Field, Notice, text } from '../ui';
import { moneyFormat, StoreSelect, useStoreChoice } from '../stock/StockPanel';

const time = new Intl.DateTimeFormat('en-IN', { dateStyle: 'medium', timeStyle: 'short' });
const when = (utc: string) => time.format(new Date(utc));

const resultLabels: Record<SyncResult['status'], string> = {
  ACCEPTED: 'Posted',
  QUARANTINED: 'Waiting for a manager',
  DUPLICATE: 'Already received',
  REJECTED: 'Not kept',
  NOT_PROCESSED: 'Not sent yet',
};

/** This phone's enrolment and the collections waiting on it; synchronises when the server can be reached. */
export function useOfflineQueue(offline: boolean, onSynced: () => void) {
  const [device, setDevice] = useState<OfflineDevice | null>(null);
  const [items, setItems] = useState<QueuedCollection[]>([]);
  const [done, setDone] = useState<SyncedCollection[]>([]);
  const [syncing, setSyncing] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const running = useRef(false);
  const synced = useRef(onSynced);
  synced.current = onSynced;

  const reload = useCallback(async () => {
    if (!offlineSupported()) return;
    const [waiting, kept] = await Promise.all([pending(), history()]);
    setItems(waiting);
    setDone(kept.slice(0, 20));
  }, []);

  const sync = useCallback(async () => {
    if (running.current || !offlineSupported()) return;
    running.current = true;
    setSyncing(true);
    setError(null);
    try {
      const results = await synchronise();
      if (results.some((r) => r.status !== 'NOT_PROCESSED')) synced.current();
    } catch (caught) {
      // No signal yet: they stay on the phone for the next attempt.
      if (!unreachable(caught)) setError(caught);
    } finally {
      running.current = false;
      setSyncing(false);
      await reload();
    }
  }, [reload]);

  // Whenever the app comes online (or opens online), the enrolment is refreshed and anything waiting is sent.
  useEffect(() => {
    if (!offlineSupported()) return;
    let live = true;
    void (async () => {
      try {
        const loaded = await loadDevice();
        if (!live) return;
        setDevice(loaded.device);
        if (!offline && loaded.online) await sync();
        else await reload();
      } catch (caught) {
        if (live) setError(caught);
      }
    })();
    return () => {
      live = false;
    };
  }, [offline, sync, reload]);

  useEffect(() => {
    const retry = () => void sync();
    window.addEventListener('online', retry);
    return () => window.removeEventListener('online', retry);
  }, [sync]);

  return { device, setDevice, items, done, syncing, error, sync, reload };
}

export type OfflineQueue = ReturnType<typeof useOfflineQueue>;

/** What a collection taken without signal gives the party: provisional, with no balance until it is synchronised. */
export function ProvisionalReceipt({ item }: { item: QueuedCollection }) {
  return (
    <Notice tone="warning">
      <span data-testid="provisional-receipt">
        Provisional receipt P-{item.sequence}: Rs. {moneyFormat.format(item.amount)} ({ReceiptMethodLabels[item.method] ?? item.method}) from {item.debtorName},{' '}
        {when(item.recordedAtUtc)}. Recorded on this phone without signal: it is posted when the phone synchronises, and the party&apos;s balance is
        confirmed then.
      </span>
    </Notice>
  );
}

/** The collections waiting on this phone (count and amount), synchronising them, and what became of the last ones. */
export function PendingCollections({ queue, offline }: { queue: OfflineQueue; offline: boolean }) {
  const { device, items, done, syncing, error, sync } = queue;
  const held = items.reduce((sum, i) => sum + i.amount, 0);
  if (!device && items.length === 0 && done.length === 0) return null;
  const problems = done.filter((d) => d.status === 'QUARANTINED' || d.status === 'REJECTED');
  return (
    <section className="sb-card" aria-labelledby="pending-heading" data-testid="pending-collections">
      <header className="sb-card__header">
        <h2 id="pending-heading">On this phone</h2>
      </header>
      {device ? (
        <p className="sb-muted">
          {device.name}: may hold up to Rs. {moneyFormat.format(device.offlineLimit)} for up to {device.maxOfflineHours} hours without signal.
        </p>
      ) : items.length > 0 ? (
        <Notice tone="warning">This phone is no longer enrolled. Show these collections to a manager; they cannot be sent from here.</Notice>
      ) : null}
      <p data-testid="pending-summary">
        {items.length === 0 ? 'Nothing waiting to be sent.' : `${items.length} collection${items.length === 1 ? '' : 's'} waiting to be sent: Rs. ${moneyFormat.format(held)}.`}
      </p>
      {items.length > 0 ? (
        <>
          <ul className="sb-plain-list">
            {items.map((i) => (
              <li key={i.id}>
                P-{i.sequence}: {i.debtorName}, Rs. {moneyFormat.format(i.amount)} ({ReceiptMethodLabels[i.method] ?? i.method}), {when(i.recordedAtUtc)}
              </li>
            ))}
          </ul>
          <button type="button" className="sb-button" disabled={syncing || offline || !device} onClick={() => void sync()}>
            {syncing ? 'Sending...' : 'Sync now'}
          </button>
          {offline ? <p className="sb-muted">No signal: they are sent as soon as the server can be reached.</p> : null}
        </>
      ) : null}
      <ErrorText error={error} />
      {problems.length > 0 ? (
        <Notice tone="warning">
          {problems.length} collection{problems.length === 1 ? ' was' : 's were'} not posted automatically; a manager will look at {problems.length === 1 ? 'it' : 'them'}.
        </Notice>
      ) : null}
      {done.length > 0 ? (
        <details>
          <summary>Sent from this phone</summary>
          <ul className="sb-plain-list" data-testid="synced-collections">
            {done.map((d) => (
              <li key={d.id}>
                P-{d.sequence}: {d.debtorName}, Rs. {moneyFormat.format(d.amount)} - {resultLabels[d.status]}
                {d.receiptNumber ? `, receipt ${d.receiptNumber}` : ''}
                {d.balanceAfter !== null ? `, now owes Rs. ${moneyFormat.format(d.balanceAfter)}` : ''}
                {d.reason ? ` (${d.reason})` : ''}
              </li>
            ))}
          </ul>
        </details>
      ) : null}
    </section>
  );
}

/**
 * A manager, signed in on the collector's phone, lets it collect without signal (within limits). Not while
 * collections wait on it: enrolling again would strand them.
 */
export function EnrolPhoneCard({ business, queue }: { business: string | null; queue: OfflineQueue }) {
  const { hasPermission } = useAuth();
  const collectors = useApiData<Collector[]>(business && hasPermission(CollectionPermission.Manage) ? `${business}/collectors` : null);
  const [message, setMessage] = useState<string | null>(null);
  if (!hasPermission(CollectionPermission.Manage) || !offlineSupported()) return null;
  return (
    <details className="sb-card" data-testid="enrol-phone">
      <summary>Collect without signal on this phone</summary>
      {queue.device ? <p className="sb-muted">This phone is enrolled as {queue.device.name}.</p> : null}
      {message ? <Notice tone="success">{message}</Notice> : null}
      <ErrorText error={collectors.error} />
      {queue.items.length > 0 ? (
        <Notice tone="warning">Collections are waiting on this phone. Synchronise them before enrolling it again.</Notice>
      ) : (
        <ActionForm
          submitLabel="Enrol this phone"
          onSubmit={async (data) => {
            const device = await api.post<CollectionDevice>(`${business}/collections/devices`, {
              collectorUserId: text(data, 'collector'),
              name: text(data, 'name'),
              offlineLimit: Number(text(data, 'limit')),
              maxOfflineHours: Number(text(data, 'hours')),
            });
            queue.setDevice((await loadDevice()).device);
            setMessage(`This phone now collects without signal for ${device.collector}. Sign out and let them sign in.`);
          }}
        >
          <label className="sb-field">
            <span className="sb-field__label">Collector</span>
            <select className="sb-input" name="collector" required>
              {(collectors.data ?? []).map((c) => (
                <option key={c.userId} value={c.userId}>{c.displayName}</option>
              ))}
            </select>
          </label>
          <Field label="Phone name" name="name" placeholder="e.g. Ravi's phone" maxLength={60} required />
          <div className="sb-form-row">
            <Field label="Most it may hold (Rs.)" name="limit" inputMode="decimal" defaultValue="5000" required />
            <Field label="Within (hours)" name="hours" inputMode="numeric" defaultValue="24" required />
          </div>
          <p className="sb-muted">
            Collections taken without signal stay encrypted on this phone until they are sent. Anyone who can use the unlocked phone can see them,
            so keep a screen lock on it.
          </p>
        </ActionForm>
      )}
    </details>
  );
}

/** Managers: the phones that may collect without signal, their limits, and revoking one. */
export function CollectionDevicesCard({ business }: { business: string | null }) {
  const [version, setVersion] = useState(0);
  const devices = useApiData<CollectionDevice[]>(business ? `${business}/collections/devices?r=${version}` : null);
  const [editing, setEditing] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  return (
    <section className="sb-card" aria-labelledby="devices-heading" data-testid="collection-devices">
      <header className="sb-card__header">
        <h2 id="devices-heading">Phones collecting without signal</h2>
      </header>
      <p className="sb-muted">Enrol a phone from the Collection App on that phone (signed in as a manager).</p>
      <ErrorText error={devices.error ?? error} />
      {devices.data && devices.data.length === 0 ? <p className="sb-muted">No phones enrolled.</p> : null}
      <ul className="sb-plain-list">
        {(devices.data ?? []).map((d) => (
          <li key={d.id}>
            <strong>{d.name}</strong> ({d.collector}): up to Rs. {moneyFormat.format(d.offlineLimit)} for {d.maxOfflineHours} h; {d.lastSequence} sent
            {d.lastSyncedAtUtc ? `, last ${when(d.lastSyncedAtUtc)}` : ''}. {d.isActive ? null : <span className="sb-muted">Revoked.</span>}
            {d.isActive ? (
              <>
                <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setEditing(editing === d.id ? null : d.id)}>
                  Limits
                </button>
                <button
                  type="button"
                  className="sb-button sb-button--secondary sb-button--small"
                  onClick={async () => {
                    if (!window.confirm(`Revoke ${d.name}? Collections still on it can no longer be sent from it.`)) return;
                    setError(null);
                    try {
                      await api.post(`${business}/collections/devices/${d.id}/revoke`);
                      setVersion((v) => v + 1);
                    } catch (caught) {
                      setError(caught);
                    }
                  }}
                >
                  Revoke
                </button>
              </>
            ) : null}
            {editing === d.id ? (
              <ActionForm
                submitLabel="Save limits"
                onSubmit={async (data) => {
                  await api.put(`${business}/collections/devices/${d.id}`, {
                    offlineLimit: Number(text(data, 'limit')),
                    maxOfflineHours: Number(text(data, 'hours')),
                    rowVersion: d.rowVersion,
                  });
                  setEditing(null);
                  setVersion((v) => v + 1);
                }}
              >
                <div className="sb-form-row">
                  <Field label="Most it may hold (Rs.)" name="limit" inputMode="decimal" defaultValue={String(d.offlineLimit)} required />
                  <Field label="Within (hours)" name="hours" inputMode="numeric" defaultValue={String(d.maxOfflineHours)} required />
                </div>
              </ActionForm>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  );
}

/** Managers: collections that arrived from a phone but could not be posted automatically; post or refuse each, with why. */
export function QuarantineCard({ business }: { business: string | null }) {
  const [version, setVersion] = useState(0);
  const waiting = useApiData<OfflineSubmission[]>(business ? `${business}/collections/offline?status=QUARANTINED&r=${version}` : null);
  const { stores, storeId, setStoreId } = useStoreChoice();
  const [open, setOpen] = useState<string | null>(null);
  return (
    <section className="sb-card" aria-labelledby="quarantine-heading" data-testid="offline-quarantine">
      <header className="sb-card__header">
        <h2 id="quarantine-heading">Offline collections to check</h2>
      </header>
      <ErrorText error={waiting.error} />
      {waiting.data && waiting.data.length === 0 ? <p className="sb-muted">Nothing waiting.</p> : null}
      <ul className="sb-plain-list">
        {(waiting.data ?? []).map((s) => (
          <li key={s.id}>
            {s.collector} ({s.device}, P-{s.sequence}): Rs. {moneyFormat.format(s.amount)} {ReceiptMethodLabels[s.method] ?? s.method} from {s.debtor},{' '}
            {when(s.recordedAtUtc)}. <strong>{s.reason}</strong>{' '}
            <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setOpen(open === s.id ? null : s.id)}>
              Decide
            </button>
            {open === s.id ? (
              <ActionForm
                submitLabel="Save decision"
                onSubmit={async (data) => {
                  const accept = data.get('decision') === 'accept';
                  if (accept && !storeId) throw new Error('Choose the store where the money was received.');
                  await api.post(`${business}/collections/offline/${s.id}/resolve`, {
                    accept,
                    note: text(data, 'note'),
                    storeId: accept ? storeId : null,
                    rowVersion: s.rowVersion,
                  });
                  setOpen(null);
                  setVersion((v) => v + 1);
                }}
              >
                <label className="sb-field">
                  <span className="sb-field__label">Decision</span>
                  <select className="sb-input" name="decision" defaultValue="accept">
                    <option value="accept">Post it as a receipt (the money was received)</option>
                    <option value="reject">Do not post it</option>
                  </select>
                </label>
                <StoreSelect stores={stores} value={storeId} onChange={setStoreId} label="Received at" />
                <Field label="Why" name="note" maxLength={300} required />
              </ActionForm>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  );
}
