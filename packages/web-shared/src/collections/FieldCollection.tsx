'use client';

import { useRef, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api';
import { useApiData } from '../admin/useApiData';
import { ReceiptMethodLabels, VisitOutcomeLabels, type CollectorSessionInfo, type DayParty, type DebtorReceipt } from '../types';
import { ErrorText, Notice } from '../ui';
import { moneyFormat, StoreSelect, useStoreChoice } from '../stock/StockPanel';
import { DenominationGrid, useDenominationCounts } from '../shifts/ShiftViews';

function newKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
}

/** The collector's round: start it, see what was collected, hand it over (blind count) and wait for it to be counted. */
export function RoundBar({ business, onChange, version }: { business: string | null; onChange: () => void; version: number }) {
  const session = useApiData<CollectorSessionInfo | null>(business ? `${business}/collections/session?r=${version}` : null);
  const { stores, storeId, setStoreId } = useStoreChoice();
  const [handingOver, setHandingOver] = useState(false);
  const counts = useDenominationCounts();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const s = session.data;

  async function run(work: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await work();
      await session.reload();
      onChange();
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="sb-card" aria-label="Collection round" data-testid="round">
      <ErrorText error={session.error ?? error} />
      {session.data === null ? (
        <div className="sb-inline-form">
          <StoreSelect stores={stores} value={storeId} onChange={setStoreId} label="Hand over at" />
          <button type="button" className="sb-button" disabled={busy || !storeId} onClick={() => void run(() => api.post(`${business}/collections/sessions`, { storeId }))}>
            Start collecting
          </button>
        </div>
      ) : null}
      {s?.status === 'OPEN' ? (
        <>
          <p data-testid="round-status">
            Collecting: {s.receipts} receipt{s.receipts === 1 ? '' : 's'}
            {s.totals.length > 0 ? ` (${s.totals.map((t) => `${ReceiptMethodLabels[t.method] ?? t.method} Rs. ${moneyFormat.format(t.amount)}`).join(', ')})` : ''}
          </p>
          {handingOver ? (
            <div className="sb-form">
              <DenominationGrid state={counts} label="Count the cash you hand over" />
              {s.instruments.length > 0 ? (
                <p>Also hand over: {s.instruments.map((i) => `${i.kind === 'CHEQUE' ? 'cheque' : 'draft'} ${i.number} (Rs. ${moneyFormat.format(i.amount)})`).join(', ')}.</p>
              ) : null}
              <button
                type="button"
                className="sb-button"
                disabled={busy || !counts.valid}
                onClick={() => void run(async () => {
                  await api.post(`${business}/collections/sessions/${s.id}/handover`, { counts: counts.list });
                  setHandingOver(false);
                })}
              >
                Hand over Rs. {moneyFormat.format(counts.total)}
              </button>
            </div>
          ) : (
            <button type="button" className="sb-button sb-button--secondary" onClick={() => setHandingOver(true)}>End round and hand over</button>
          )}
        </>
      ) : null}
      {s?.status === 'HANDED_OVER' ? (
        <Notice tone="warning">
          Handed over Rs. {moneyFormat.format(s.declaredCash ?? 0)} in cash{s.instruments.length > 0 ? ` and ${s.instruments.length} cheque(s)` : ''}; waiting for it to be counted.
        </Notice>
      ) : null}
    </section>
  );
}

/** Collect from a party in the open round: cash, cheque, UPI, transfer, card, draft or other. Pays the oldest bills first. */
export function CollectForm({ business, party, onDone }: { business: string | null; party: DayParty; onDone: (receipt: DebtorReceipt) => void }) {
  const [method, setMethod] = useState('CASH');
  const [amount, setAmount] = useState(party.dueBalance > 0 ? party.dueBalance.toFixed(2) : '');
  const [reference, setReference] = useState('');
  const [bank, setBank] = useState('');
  const [chequeDate, setChequeDate] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  // One key per exact collection: a retry after a lost answer cannot record it twice.
  const attempt = useRef<{ body: string; key: string } | null>(null);
  const instrument = method === 'CHEQUE' || method === 'DEMAND_DRAFT';

  async function submit(event: FormEvent) {
    event.preventDefault();
    const value = Number(amount);
    if (!amount.trim() || !Number.isFinite(value) || value <= 0) {
      setError(new Error('Enter the amount collected.'));
      return;
    }
    const body = JSON.stringify({
      debtorId: party.debtorId,
      method,
      amount: Math.round(value * 100) / 100,
      reference: reference.trim() || null,
      bankName: instrument ? bank.trim() || null : null,
      chequeDate: instrument && chequeDate ? chequeDate : null,
    });
    if (attempt.current?.body !== body) attempt.current = { body, key: newKey() };
    setBusy(true);
    setError(null);
    try {
      onDone(await api.post<DebtorReceipt>(`${business}/collections/receipts`, { ...JSON.parse(body), idempotencyKey: attempt.current.key }));
    } catch (caught) {
      if (caught instanceof ApiError) attempt.current = null;
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="sb-form" onSubmit={submit} data-testid="collect-form">
      <label className="sb-field">
        <span className="sb-field__label">Paid by</span>
        <select className="sb-input" value={method} onChange={(e) => setMethod(e.target.value)}>
          {Object.entries(ReceiptMethodLabels).map(([value, label]) => (
            <option key={value} value={value}>{label}</option>
          ))}
        </select>
      </label>
      <label className="sb-field">
        <span className="sb-field__label">Amount (Rs.)</span>
        <input className="sb-input" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
      </label>
      {method !== 'CASH' ? (
        <label className="sb-field">
          <span className="sb-field__label">{method === 'CHEQUE' ? 'Cheque number' : method === 'DEMAND_DRAFT' ? 'Draft number' : method === 'OTHER' ? 'How it was paid' : 'Reference'}</span>
          <input className="sb-input" value={reference} maxLength={40} onChange={(e) => setReference(e.target.value)} />
        </label>
      ) : null}
      {instrument ? (
        <>
          <label className="sb-field">
            <span className="sb-field__label">Bank</span>
            <input className="sb-input" value={bank} maxLength={100} onChange={(e) => setBank(e.target.value)} />
          </label>
          <label className="sb-field">
            <span className="sb-field__label">Cheque date</span>
            <input className="sb-input" type="date" value={chequeDate} onChange={(e) => setChequeDate(e.target.value)} />
          </label>
        </>
      ) : null}
      <ErrorText error={error} />
      <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Saving...' : 'Record collection'}</button>
    </form>
  );
}

/** A visit that brought no money, and why. */
export function OutcomeForm({ business, party, onDone }: { business: string | null; party: DayParty; onDone: () => void }) {
  const [outcome, setOutcome] = useState('NO_PAYMENT');
  const [note, setNote] = useState('');
  const [error, setError] = useState<unknown>(null);
  return (
    <form
      className="sb-form"
      onSubmit={async (e) => {
        e.preventDefault();
        setError(null);
        try {
          await api.post(`${business}/collections/visit-outcomes`, { debtorId: party.debtorId, outcome, note: note.trim() || null });
          onDone();
        } catch (caught) {
          setError(caught);
        }
      }}
    >
      <label className="sb-field">
        <span className="sb-field__label">What happened</span>
        <select className="sb-input" value={outcome} onChange={(e) => setOutcome(e.target.value)}>
          {Object.entries(VisitOutcomeLabels).map(([value, label]) => (
            <option key={value} value={value}>{label}</option>
          ))}
        </select>
      </label>
      <label className="sb-field">
        <span className="sb-field__label">Note</span>
        <input className="sb-input" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} />
      </label>
      <ErrorText error={error} />
      <button className="sb-button sb-button--secondary" type="submit">Save visit</button>
    </form>
  );
}
