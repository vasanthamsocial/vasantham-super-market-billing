'use client';

import { useState, type FormEvent } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  DeliveryOutcomeLabels,
  DispatchPermission,
  FreightTermLabels,
  FulfilmentModeLabels,
  type Consignment,
  type DispatchQueueItem,
  type Transporter,
} from '../types';
import { ErrorText, Notice, formatDateTime } from '../ui';
import { moneyFormat } from '../stock/StockPanel';

function today(): string {
  return new Date().toLocaleDateString('en-CA');
}

function newKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
}

/** Bills waiting to leave the store, recording each dispatch (LR/GR or trip), and the register of dispatches. */
export function DispatchPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const queue = useApiData<DispatchQueueItem[]>(business ? `${business}/dispatch/queue` : null);
  const transporters = useApiData<Transporter[]>(business ? `${business}/transporters` : null);
  const [selected, setSelected] = useState<string[]>([]);
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const register = useApiData<Consignment[]>(business ? `${business}/consignments${query ? `?search=${encodeURIComponent(query)}` : ''}` : null);
  const canManage = hasPermission(DispatchPermission.Manage);
  if (!business) return null;

  const chosen = (queue.data ?? []).filter((q) => selected.includes(q.invoiceId));
  const first = chosen[0];
  // Bills go together only for one customer, one way of delivery and one lorry service.
  const compatible = (q: DispatchQueueItem) =>
    !first || (q.partyName === first.partyName && q.debtorId === first.debtorId && q.mode === first.mode && q.transporterId === first.transporterId && q.storeId === first.storeId);

  return (
    <>
      <section className="sb-card" aria-labelledby="dispatch-queue-heading">
        <header className="sb-card__header">
          <h2 id="dispatch-queue-heading">Waiting for dispatch</h2>
        </header>
        <ErrorText error={queue.error} />
        <table className="sb-table" data-testid="dispatch-queue">
          <thead>
            <tr>
              {canManage ? <th /> : null}
              <th>Bill</th>
              <th>Billed</th>
              <th>Customer</th>
              <th>Delivery</th>
              <th>Deliver to</th>
              <th className="sb-num">Amount</th>
            </tr>
          </thead>
          <tbody>
            {(queue.data ?? []).map((q) => (
              <tr key={q.invoiceId}>
                {canManage ? (
                  <td>
                    <input
                      type="checkbox"
                      aria-label={`Dispatch ${q.invoiceNumber}`}
                      checked={selected.includes(q.invoiceId)}
                      disabled={!selected.includes(q.invoiceId) && !compatible(q)}
                      onChange={(e) => setSelected((s) => (e.target.checked ? [...s, q.invoiceId] : s.filter((id) => id !== q.invoiceId)))}
                    />
                  </td>
                ) : null}
                <td>
                  {q.invoiceNumber}
                  {q.challanNumber ? <span className="sb-muted"> ({q.challanNumber})</span> : null}
                </td>
                <td>{formatDateTime(q.issuedAtUtc)}</td>
                <td>{q.partyName}</td>
                <td>
                  {FulfilmentModeLabels[q.mode] ?? q.mode}
                  {q.transporterName ? `: ${q.transporterName}${q.destinationBranch ? ` to ${q.destinationBranch}` : ''}` : ''}
                </td>
                <td>{q.deliveryAddress ?? '-'}</td>
                <td className="sb-num">{moneyFormat.format(q.grandTotal)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {queue.data && queue.data.length === 0 ? <p className="sb-muted">Nothing packed is waiting for dispatch (see Packing).</p> : null}
        {canManage && first ? (
          <DispatchForm
            key={selected.join(',')}
            business={business}
            bills={chosen}
            transporter={(transporters.data ?? []).find((t) => t.id === first.transporterId) ?? null}
            onDone={async () => {
              setSelected([]);
              await Promise.all([queue.reload(), register.reload()]);
            }}
          />
        ) : null}
      </section>

      <section className="sb-card" aria-labelledby="register-heading">
        <header className="sb-card__header">
          <h2 id="register-heading">Dispatches (LR/GR register)</h2>
        </header>
        <form
          className="sb-inline-form"
          role="search"
          onSubmit={(e) => {
            e.preventDefault();
            setQuery(search.trim());
          }}
        >
          <input className="sb-input" aria-label="Find a dispatch" placeholder="LR/GR, dispatch, e-way bill or invoice number" value={search}
            onChange={(e) => setSearch(e.target.value)} />
          <button className="sb-button sb-button--secondary" type="submit">Search</button>
        </form>
        <ErrorText error={register.error} />
        <table className="sb-table" data-testid="consignments-table">
          <thead>
            <tr>
              <th>Dispatch</th>
              <th>Date</th>
              <th>Customer</th>
              <th>Bills</th>
              <th>By</th>
              <th>LR/GR</th>
              <th className="sb-num">Packages</th>
              <th>Freight</th>
              <th>Expected</th>
              <th>Status</th>
              {canManage ? <th /> : null}
            </tr>
          </thead>
          <tbody>
            {(register.data ?? []).map((c) => (
              <ConsignmentRow key={c.id} business={business} consignment={c} canManage={canManage}
                onChanged={async () => { await Promise.all([queue.reload(), register.reload()]); }} />
            ))}
          </tbody>
        </table>
        {register.data && register.data.length === 0 ? <p className="sb-muted">No dispatches found.</p> : null}
      </section>
    </>
  );
}

function DispatchForm({
  business,
  bills,
  transporter,
  onDone,
}: {
  business: string;
  bills: DispatchQueueItem[];
  transporter: Transporter | null;
  onDone: () => Promise<void>;
}) {
  const mode = bills[0]!.mode;
  const offices = (transporter?.branches ?? []).filter((b) => b.isBookingOffice && b.isActive);
  const destinations = (transporter?.branches ?? []).filter((b) => b.isDestination && b.isActive);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<Consignment | null>(null);
  const total = bills.reduce((sum, b) => sum + b.grandTotal, 0);
  const key = useState(newKey)[0];

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const value = (name: string) => (String(data.get(name) ?? '').trim() || null);
    const number = (name: string) => (value(name) === null ? null : Number(value(name)));
    setBusy(true);
    setError(null);
    try {
      const consignment = await api.post<Consignment>(`${business}/consignments`, {
        idempotencyKey: key,
        invoiceIds: bills.map((b) => b.invoiceId),
        packageCount: number('packageCount') ?? 0,
        dispatchDate: value('dispatchDate'),
        bookingBranchId: value('bookingBranchId'),
        destinationBranchId: value('destinationBranchId'),
        vehicleNumber: value('vehicleNumber'),
        driverName: value('driverName'),
        driverPhone: value('driverPhone'),
        lrNumber: value('lrNumber'),
        lrDate: value('lrDate'),
        weightKg: number('weightKg'),
        freightTerms: value('freightTerms'),
        freightAmount: number('freightAmount') ?? 0,
        expectedDeliveryDate: value('expectedDeliveryDate'),
        ewayBillNumber: value('ewayBillNumber'),
      });
      setDone(consignment);
      await onDone();
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="sb-form" onSubmit={(e) => void submit(e)} data-testid="dispatch-form" noValidate>
      <h3>
        Dispatch {bills.map((b) => b.invoiceNumber).join(', ')} to {bills[0]!.partyName} ({FulfilmentModeLabels[mode]}
        {transporter ? `: ${transporter.name}` : ''})
      </h3>
      {done ? <Notice tone="success">Recorded {done.number}.</Notice> : null}
      {total >= 50000 ? (
        <Notice tone="warning">These goods are worth Rs. {moneyFormat.format(total)}: an e-way bill is usually needed (check your state&apos;s rule).</Notice>
      ) : null}
      <div className="sb-form-row">
        <label className="sb-field">
          <span className="sb-field__label">Dispatch date</span>
          <input className="sb-input" type="date" name="dispatchDate" defaultValue={today()} required />
        </label>
        <label className="sb-field">
          <span className="sb-field__label">Packages</span>
          <input className="sb-input" name="packageCount" inputMode="numeric" defaultValue="1" required />
        </label>
        <label className="sb-field">
          <span className="sb-field__label">Weight (kg, optional)</span>
          <input className="sb-input" name="weightKg" inputMode="decimal" />
        </label>
      </div>
      {mode === 'LORRY' ? (
        <>
          <div className="sb-form-row">
            <label className="sb-field">
              <span className="sb-field__label">Booked at</span>
              <select className="sb-input" name="bookingBranchId">
                {offices.map((b) => <option key={b.id} value={b.id}>{b.name}, {b.city}</option>)}
              </select>
            </label>
            <label className="sb-field">
              <span className="sb-field__label">Destination branch</span>
              <select className="sb-input" name="destinationBranchId" defaultValue={bills[0]!.destinationBranchId ?? ''}>
                <option value="">Choose...</option>
                {destinations.map((b) => <option key={b.id} value={b.id}>{b.name}, {b.city}</option>)}
              </select>
            </label>
          </div>
          <div className="sb-form-row">
            <label className="sb-field">
              <span className="sb-field__label">LR/GR number</span>
              <input className="sb-input" name="lrNumber" required />
            </label>
            <label className="sb-field">
              <span className="sb-field__label">LR/GR date</span>
              <input className="sb-input" type="date" name="lrDate" defaultValue={today()} required />
            </label>
            <label className="sb-field">
              <span className="sb-field__label">Freight</span>
              <select className="sb-input" name="freightTerms" defaultValue="TO_PAY">
                {Object.entries(FreightTermLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
              </select>
            </label>
            <label className="sb-field">
              <span className="sb-field__label">Freight amount (Rs.)</span>
              <input className="sb-input" name="freightAmount" inputMode="decimal" defaultValue="0" />
            </label>
          </div>
        </>
      ) : null}
      <div className="sb-form-row">
        <label className="sb-field">
          <span className="sb-field__label">{mode === 'OWN_VEHICLE' ? 'Vehicle number' : 'Vehicle number (optional)'}</span>
          <input className="sb-input" name="vehicleNumber" />
        </label>
        <label className="sb-field">
          <span className="sb-field__label">{mode === 'LOCAL_DELIVERY' ? 'Delivered by' : 'Driver (optional)'}</span>
          <input className="sb-input" name="driverName" />
        </label>
        <label className="sb-field">
          <span className="sb-field__label">Driver phone (optional)</span>
          <input className="sb-input" name="driverPhone" />
        </label>
      </div>
      <div className="sb-form-row">
        <label className="sb-field">
          <span className="sb-field__label">Expected delivery (optional)</span>
          <input className="sb-input" type="date" name="expectedDeliveryDate" />
        </label>
        <label className="sb-field">
          <span className="sb-field__label">E-way bill number</span>
          <input className="sb-input" name="ewayBillNumber" inputMode="numeric" maxLength={12} />
        </label>
      </div>
      <ErrorText error={error} />
      <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Please wait...' : 'Record dispatch'}</button>
    </form>
  );
}

function ConsignmentRow({ business, consignment: c, canManage, onChanged }: { business: string; consignment: Consignment; canManage: boolean; onChanged: () => Promise<void> }) {
  const [cancelling, setCancelling] = useState(false);
  const [reporting, setReporting] = useState<'delivery' | 'return' | null>(null);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<unknown>(null);
  const lines = c.lines ?? [];

  async function cancel() {
    setError(null);
    try {
      await api.post(`${business}/consignments/${c.id}/cancel`, { reason, rowVersion: c.rowVersion });
      setCancelling(false);
      await onChanged();
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <>
      <tr>
        <td>{c.number}</td>
        <td>{c.dispatchDate}</td>
        <td>{c.partyName}</td>
        <td>{c.invoices.map((i) => i.number).join(', ')}</td>
        <td>
          {FulfilmentModeLabels[c.mode] ?? c.mode}
          {c.transporterName ? `: ${c.transporterName}, ${c.bookingOffice} to ${c.destinationBranch}` : ''}
          {c.vehicleNumber ? ` (${c.vehicleNumber})` : ''}
        </td>
        <td>{c.lrNumber ? `${c.lrNumber} of ${c.lrDate}` : '-'}</td>
        <td className="sb-num">{c.packageCount}</td>
        <td>{c.freightTerms ? `${c.freightTerms === 'PAID' ? 'Paid' : 'To pay'} Rs. ${moneyFormat.format(c.freightAmount)}` : '-'}</td>
        <td>{c.expectedDeliveryDate ?? '-'}</td>
        <td className={c.ewayBillMissing && c.status === 'DISPATCHED' ? 'sb-error' : undefined}>
          {c.status === 'CANCELLED' ? `Cancelled: ${c.cancelReason}` : c.deliveryOutcome ? `${DeliveryOutcomeLabels[c.deliveryOutcome]} on ${c.deliveredOn}` : 'On the way'}
          {c.deliveryNote ? ` (${c.deliveryNote})` : ''}
          {c.returnRecorded ? ', goods back in store' : ''}
          {c.ewayBillNumber ? `, e-way bill ${c.ewayBillNumber}` : c.ewayBillMissing ? ', no e-way bill' : ''}
        </td>
        {canManage ? (
          <td>
            {c.status === 'DISPATCHED' && !c.deliveryOutcome ? (
              <>
                <button type="button" className="sb-button sb-button--small" onClick={() => setReporting(reporting === 'delivery' ? null : 'delivery')}>Report delivery</button>
                <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setCancelling(!cancelling)}>Cancel</button>
              </>
            ) : null}
            {c.status === 'DISPATCHED' && (c.deliveryOutcome === 'FAILED' || c.deliveryOutcome === 'PARTLY_DELIVERED') && !c.returnRecorded ? (
              <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setReporting(reporting === 'return' ? null : 'return')}>Goods back</button>
            ) : null}
          </td>
        ) : null}
      </tr>
      {lines.length > 0 ? (
        <tr>
          <td colSpan={canManage ? 11 : 10} className="sb-muted">
            Carried: {lines.map((l) => `${l.itemName} ${l.quantity} ${l.unitCode}${l.delivered !== null && l.delivered !== l.quantity ? ` (delivered ${l.delivered}${l.returned ? `, back ${l.returned}` : ''})` : ''}`).join('; ')}
          </td>
        </tr>
      ) : null}
      {reporting ? (
        <tr>
          <td colSpan={canManage ? 11 : 10}>
            <ReportForm business={business} consignment={c} kind={reporting} onDone={async () => { setReporting(null); await onChanged(); }} />
          </td>
        </tr>
      ) : null}
      {cancelling ? (
        <tr>
          <td colSpan={canManage ? 11 : 10}>
            <div className="sb-inline-form">
              <input className="sb-input" aria-label={`Why cancel ${c.number}`} placeholder="Why it is cancelled" value={reason} onChange={(e) => setReason(e.target.value)} />
              <button type="button" className="sb-button sb-button--small" onClick={() => void cancel()}>Cancel dispatch</button>
            </div>
            <ErrorText error={error} />
          </td>
        </tr>
      ) : null}
    </>
  );
}

/** What reached the customer (once), or what came back to the store after a failed or partial delivery (once). */
function ReportForm({ business, consignment: c, kind, onDone }: { business: string; consignment: Consignment; kind: 'delivery' | 'return'; onDone: () => Promise<void> }) {
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const lines = c.lines ?? [];

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const quantities = lines.map((l) => ({ challanLineId: l.challanLineId, quantity: Number(String(data.get(`d-${l.challanLineId}`) ?? '0')) }));
    setBusy(true);
    setError(null);
    try {
      await api.post(
        `${business}/consignments/${c.id}/${kind}`,
        kind === 'delivery'
          ? { deliveredOn: String(data.get('deliveredOn') ?? ''), lines: quantities, note: String(data.get('note') ?? '').trim() || null, rowVersion: c.rowVersion }
          : { lines: quantities, rowVersion: c.rowVersion },
      );
      await onDone();
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="sb-form" onSubmit={(e) => void submit(e)} data-testid={`${kind}-form`} noValidate>
      {lines.map((l) => (
        <label className="sb-field" key={l.challanLineId}>
          <span className="sb-field__label">{kind === 'delivery' ? `Delivered: ${l.itemName} (${l.unitCode})` : `Back in store: ${l.itemName} (${l.unitCode})`}</span>
          <input className="sb-input" name={`d-${l.challanLineId}`} inputMode="decimal"
            defaultValue={String(kind === 'delivery' ? l.quantity : l.quantity - (l.delivered ?? 0))} />
        </label>
      ))}
      {kind === 'delivery' ? (
        <div className="sb-form-row">
          <label className="sb-field">
            <span className="sb-field__label">Delivered on</span>
            <input className="sb-input" type="date" name="deliveredOn" defaultValue={today()} />
          </label>
          <label className="sb-field">
            <span className="sb-field__label">Received by / why not delivered</span>
            <input className="sb-input" name="note" />
          </label>
        </div>
      ) : null}
      <ErrorText error={error} />
      <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Please wait...' : kind === 'delivery' ? 'Save delivery' : 'Save goods back'}</button>
    </form>
  );
}
