'use client';

import { useState, type FormEvent } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { ChallanProgressLabels, DeliveryOutcomeLabels, DispatchPermission, FulfilmentModeLabels, type Challan, type ChallanSummary } from '../types';
import { ErrorText, formatDateTime } from '../ui';

const qty = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });

/** Packing challans being worked on: pick, check (by someone else), pack, print the challan and the package labels. */
export function PackingPanel() {
  const { membership, hasPermission } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/packing-challans` : null;
  const challans = useApiData<ChallanSummary[]>(base);
  const [openedId, setOpenedId] = useState<string | null>(null);
  const canManage = hasPermission(DispatchPermission.Manage);
  if (!base) return null;

  return (
    <>
      <section className="sb-card" aria-labelledby="packing-heading">
        <header className="sb-card__header">
          <h2 id="packing-heading">Packing</h2>
        </header>
        <p className="sb-muted">Bills going by delivery or lorry. The challan shows goods only: no prices, cost or balance.</p>
        <ErrorText error={challans.error} />
        <table className="sb-table" data-testid="challans-table">
          <thead>
            <tr>
              <th>Challan</th>
              <th>Bill</th>
              <th>Customer</th>
              <th>Delivery</th>
              <th>Where the goods are</th>
            </tr>
          </thead>
          <tbody>
            {(challans.data ?? []).map((c) => (
              <tr key={c.id}>
                <td>
                  <button type="button" className="sb-link" onClick={() => setOpenedId(c.id)}>{c.number}</button>
                </td>
                <td>{c.invoiceNumber}</td>
                <td>{c.partyName}</td>
                <td>
                  {FulfilmentModeLabels[c.mode] ?? c.mode}
                  {c.transporterName ? `: ${c.transporterName}${c.destinationBranch ? ` to ${c.destinationBranch}` : ''}` : ''}
                </td>
                <td className={c.hasDifference ? 'sb-error' : undefined}>
                  {ChallanProgressLabels[c.progress] ?? c.progress}
                  {c.readyToSend ? ', ready to send' : ''}
                  {c.hasDifference ? ', difference to settle' : ''}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {challans.data && challans.data.length === 0 ? <p className="sb-muted">Nothing to pack.</p> : null}
      </section>
      {openedId ? <ChallanCard key={openedId} base={base} challanId={openedId} canManage={canManage} onChanged={() => void challans.reload()} /> : null}
    </>
  );
}

function ChallanCard({ base, challanId, canManage, onChanged }: { base: string; challanId: string; canManage: boolean; onChanged: () => void }) {
  const challan = useApiData<Challan>(`${base}/${challanId}`);
  const c = challan.data;
  if (!c) return <ErrorText error={challan.error} />;
  const step = c.progress === 'TO_PICK' ? 'pick' : c.progress === 'TO_CHECK' ? 'check' : null;
  const canPack = c.status === 'OPEN' && c.checker !== null && c.lines.some((l) => (l.checked ?? 0) > l.packed);

  async function changed() {
    await challan.reload();
    onChanged();
  }

  return (
    <section className="sb-card" aria-labelledby="challan-heading" data-testid="challan-card">
      <header className="sb-card__header">
        <h2 id="challan-heading">Challan {c.number}</h2>
        <div className="sb-actions">
          <a className="sb-button sb-button--secondary sb-button--small" href={`${base}/${c.id}/pdf`} target="_blank" rel="noopener">Print challan</a>
          {c.packageCount > 0 ? (
            <a className="sb-button sb-button--secondary sb-button--small" href={`${base}/${c.id}/labels`} target="_blank" rel="noopener">
              Package labels ({c.packageCount})
            </a>
          ) : null}
        </div>
      </header>
      <dl className="sb-status-grid">
        <dt>Bill</dt>
        <dd>{c.invoiceNumber} of {c.invoiceDate}</dd>
        <dt>Customer</dt>
        <dd>{c.partyName}</dd>
        <dt>Deliver to</dt>
        <dd>{c.deliveryAddress ?? '-'}{c.contactPhone ? ` (${c.contactPhone})` : ''}</dd>
        {c.route ? (
          <>
            <dt>Route</dt>
            <dd>{c.route}</dd>
          </>
        ) : null}
        <dt>Delivery</dt>
        <dd>
          {FulfilmentModeLabels[c.mode] ?? c.mode}
          {c.transporterName ? `: ${c.transporterName}${c.destinationBranch ? ` to ${c.destinationBranch}` : ''}` : ''}
        </dd>
        <dt>Status</dt>
        <dd data-testid="challan-progress">{ChallanProgressLabels[c.progress] ?? c.progress}{c.cancelReason ? ` (${c.cancelReason})` : ''}</dd>
        <dt>Picked / checked / packed by</dt>
        <dd>{[c.picker, c.checker, c.packer].map((n) => n ?? '-').join(' / ')}</dd>
      </dl>

      <table className="sb-table" data-testid="challan-lines">
        <thead>
          <tr>
            <th>#</th>
            <th>Item</th>
            <th>Batch</th>
            <th className="sb-num">Billed</th>
            <th className="sb-num">Picked</th>
            <th className="sb-num">Checked</th>
            <th className="sb-num">Packed</th>
            <th className="sb-num">On the way</th>
            <th className="sb-num">Delivered</th>
            <th className="sb-num">Ready to send</th>
            <th className="sb-num">Difference</th>
          </tr>
        </thead>
        <tbody>
          {c.lines.map((l) => (
            <tr key={l.id}>
              <td>{l.lineNumber}</td>
              <td>
                {l.itemName}
                {l.variantName ? ` - ${l.variantName}` : ''} ({l.unitCode}){l.shortReason ? <span className="sb-muted"> Short: {l.shortReason}</span> : null}
              </td>
              <td>{l.batches ?? '-'}</td>
              <td className="sb-num">{qty.format(l.quantity)}</td>
              <td className="sb-num">{l.picked === null ? '-' : qty.format(l.picked)}</td>
              <td className="sb-num">{l.checked === null ? '-' : qty.format(l.checked)}</td>
              <td className="sb-num">{qty.format(l.packed)}</td>
              <td className="sb-num">{qty.format(l.inTransit)}</td>
              <td className="sb-num">{qty.format(l.delivered)}</td>
              <td className="sb-num">{qty.format(l.readyToSend)}</td>
              <td className={`sb-num${l.difference > 0 ? ' sb-error' : ''}`}>
                {l.difference > 0 ? qty.format(l.difference) : '-'}
                {l.credited > 0 ? <span className="sb-muted"> ({qty.format(l.credited)} credited)</span> : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {c.lines.some((l) => l.difference > 0) ? (
        <p className="sb-notice sb-notice--warning" role="status">
          The bill is not changed here. Settle the difference with a credit note (Returns at the counter) or send the goods again.
        </p>
      ) : null}

      {canManage && c.status === 'OPEN' && step ? <CountForm base={base} challan={c} step={step} onDone={changed} /> : null}
      {canManage && canPack ? <PackForm base={base} challan={c} onDone={changed} /> : null}

      {c.dispatches.length > 0 ? (
        <>
          <h3>Dispatches</h3>
          <ul className="sb-plain-list">
            {c.dispatches.map((d) => (
              <li key={d.id}>
                {d.number} on {d.dispatchDate}
                {d.lrNumber ? `, LR ${d.lrNumber}` : ''}: {d.status === 'CANCELLED' ? 'cancelled' : d.deliveryOutcome ? DeliveryOutcomeLabels[d.deliveryOutcome] : 'on the way'}
              </li>
            ))}
          </ul>
        </>
      ) : null}
      <details>
        <summary>History</summary>
        <ul className="sb-plain-list" data-testid="challan-history">
          {c.events.map((e, i) => (
            <li key={i}>
              {formatDateTime(e.atUtc)} {e.by}: {e.kind.toLowerCase().replace(/_/g, ' ')}{e.detail ? ` - ${e.detail}` : ''}
            </li>
          ))}
        </ul>
      </details>
    </section>
  );
}

/** Picking or checking: a count for every line, with the reason where it is short. */
function CountForm({ base, challan, step, onDone }: { base: string; challan: Challan; step: 'pick' | 'check'; onDone: () => Promise<void> }) {
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const expected = (l: Challan['lines'][number]) => (step === 'pick' ? l.quantity : l.picked ?? 0);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    setBusy(true);
    setError(null);
    try {
      await api.post(`${base}/${challan.id}/${step}`, {
        lines: challan.lines.map((l) => ({
          lineId: l.id,
          quantity: Number(String(data.get(`q-${l.id}`) ?? '0')),
          reason: String(data.get(`r-${l.id}`) ?? '').trim() || null,
        })),
        rowVersion: challan.rowVersion,
      });
      await onDone();
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="sb-form" onSubmit={(e) => void submit(e)} data-testid={`${step}-form`} noValidate>
      <h3>{step === 'pick' ? 'Picked from the shelves' : 'Checked (by someone other than the picker)'}</h3>
      {challan.lines.map((l) => (
        <div className="sb-form-row" key={l.id}>
          <label className="sb-field">
            <span className="sb-field__label">{`${l.itemName} (${l.unitCode})`}</span>
            <input className="sb-input" name={`q-${l.id}`} inputMode="decimal" defaultValue={String(expected(l))} />
          </label>
          <label className="sb-field">
            <span className="sb-field__label">{`Why short: ${l.itemName}`}</span>
            <input className="sb-input" name={`r-${l.id}`} placeholder="Only if less" />
          </label>
        </div>
      ))}
      <ErrorText error={error} />
      <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Please wait...' : step === 'pick' ? 'Record picking' : 'Record check'}</button>
    </form>
  );
}

function PackForm({ base, challan, onDone }: { base: string; challan: Challan; onDone: () => Promise<void> }) {
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const open = challan.lines.filter((l) => (l.checked ?? 0) > l.packed);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    setBusy(true);
    setError(null);
    try {
      await api.post(`${base}/${challan.id}/pack`, {
        lines: open.map((l) => ({ lineId: l.id, quantity: Number(String(data.get(`p-${l.id}`) ?? '0')) })),
        packages: Number(String(data.get('packages') ?? '0')),
        rowVersion: challan.rowVersion,
      });
      await onDone();
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="sb-form" onSubmit={(e) => void submit(e)} data-testid="pack-form" noValidate>
      <h3>Pack (all now, or some now and the rest later)</h3>
      {open.map((l) => (
        <label className="sb-field" key={l.id}>
          <span className="sb-field__label">{`Pack ${l.itemName} (${l.unitCode})`}</span>
          <input className="sb-input" name={`p-${l.id}`} inputMode="decimal" defaultValue={String((l.checked ?? 0) - l.packed)} />
        </label>
      ))}
      <label className="sb-field">
        <span className="sb-field__label">Packages</span>
        <input className="sb-input" name="packages" inputMode="numeric" defaultValue="1" />
      </label>
      <ErrorText error={error} />
      <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Please wait...' : 'Record packing'}</button>
    </form>
  );
}
