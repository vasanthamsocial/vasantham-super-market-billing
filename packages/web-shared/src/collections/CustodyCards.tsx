'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  AccountPermission,
  ChequeMoves,
  ChequeStatusLabels,
  ReceiptMethodLabels,
  type ChequeInfo,
  type CollectorSessionInfo,
  type DebtorReceipt,
} from '../types';
import { ErrorText, Notice } from '../ui';
import { moneyFormat, StoreSelect, useStoreChoice } from '../stock/StockPanel';
import { DenominationGrid, useDenominationCounts } from '../shifts/ShiftViews';

/** Rounds handed over and waiting to be counted: the receiver counts again, and a difference must be explained. */
export function HandoversCard({ business }: { business: string | null }) {
  const { stores, storeId, setStoreId } = useStoreChoice();
  const [version, setVersion] = useState(0);
  const waiting = useApiData<CollectorSessionInfo[]>(business && storeId ? `${business}/collections/sessions?storeId=${storeId}&status=HANDED_OVER&r=${version}` : null);
  const [counting, setCounting] = useState<CollectorSessionInfo | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  return (
    <section className="sb-card" aria-labelledby="handovers-heading">
      <header className="sb-card__header">
        <h2 id="handovers-heading">Collections to count</h2>
      </header>
      <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
      <ErrorText error={waiting.error} />
      {notice ? <Notice tone="success">{notice}</Notice> : null}
      {waiting.data && waiting.data.length === 0 ? <p className="sb-muted">Nothing is waiting to be counted.</p> : null}
      <ul className="sb-plain-list" data-testid="handovers">
        {(waiting.data ?? []).map((s) => (
          <li key={s.id}>
            {s.collector}: {s.receipts} receipts, declared Rs. {moneyFormat.format(s.declaredCash ?? 0)} cash (expected Rs. {moneyFormat.format(s.expectedCash ?? 0)})
            {s.instruments.length > 0 ? `, ${s.instruments.map((i) => `${i.kind === 'CHEQUE' ? 'cheque' : 'draft'} ${i.number} Rs. ${moneyFormat.format(i.amount)}`).join(', ')}` : ''}{' '}
            <button type="button" className="sb-button sb-button--small" onClick={() => setCounting(s)}>Count</button>
          </li>
        ))}
      </ul>
      {counting ? (
        <CountForm
          key={counting.id}
          business={business}
          session={counting}
          done={(confirmed) => {
            setCounting(null);
            setNotice(`Counted ${confirmed.collector}'s round: Rs. ${moneyFormat.format(confirmed.countedCash ?? 0)}${
              confirmed.variance ? ` (${confirmed.variance > 0 ? 'over' : 'short'} by Rs. ${moneyFormat.format(Math.abs(confirmed.variance))})` : ', no difference'}.`);
            setVersion((v) => v + 1);
          }}
        />
      ) : null}
    </section>
  );
}

function CountForm({ business, session, done }: { business: string | null; session: CollectorSessionInfo; done: (s: CollectorSessionInfo) => void }) {
  const counts = useDenominationCounts();
  const [note, setNote] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const difference = counts.total - (session.expectedCash ?? 0);
  return (
    <div className="sb-form" data-testid="count-form">
      <DenominationGrid state={counts} label={`Count ${session.collector}'s cash`} />
      {difference !== 0 ? <p className="sb-error">Difference from expected: Rs. {moneyFormat.format(difference)}</p> : null}
      <label className="sb-field">
        <span className="sb-field__label">Note (needed when the count differs)</span>
        <input className="sb-input" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} />
      </label>
      <ErrorText error={error} />
      <button
        type="button"
        className="sb-button"
        disabled={busy || !counts.valid}
        onClick={async () => {
          setBusy(true);
          setError(null);
          try {
            done(await api.post<CollectorSessionInfo>(`${business}/collections/sessions/${session.id}/confirm`, { counts: counts.list, note: note.trim() || null }));
          } catch (caught) {
            setError(caught);
          } finally {
            setBusy(false);
          }
        }}
      >
        Confirm count of Rs. {moneyFormat.format(counts.total)}
      </button>
    </div>
  );
}

/** Cheques and drafts until their money is in the bank; a bounce or cancellation puts the amount back on the account. */
export function ChequesCard({ business }: { business: string | null }) {
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState('RECEIVED');
  const [version, setVersion] = useState(0);
  const cheques = useApiData<ChequeInfo[]>(business ? `${business}/cheques?${status ? `status=${status}&` : ''}r=${version}` : null);
  const [error, setError] = useState<unknown>(null);
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [replacement, setReplacement] = useState<Record<string, string>>({});
  const canMove = hasPermission(AccountPermission.Receivables);

  async function move(cheque: ChequeInfo, to: string) {
    setError(null);
    try {
      await api.post(`${business}/cheques/${cheque.id}/move`, {
        to,
        note: notes[cheque.id]?.trim() || null,
        replacedByReceiptId: to === 'REPLACED' ? replacement[cheque.id] || null : null,
      });
      setVersion((v) => v + 1);
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <section className="sb-card" aria-labelledby="cheques-heading">
      <header className="sb-card__header">
        <h2 id="cheques-heading">Cheques and drafts</h2>
      </header>
      <label className="sb-field">
        <span className="sb-field__label">Status</span>
        <select className="sb-input" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">All</option>
          {Object.entries(ChequeStatusLabels).map(([value, label]) => (
            <option key={value} value={value}>{label}</option>
          ))}
        </select>
      </label>
      <ErrorText error={cheques.error ?? error} />
      <table className="sb-table" data-testid="cheques-table">
        <thead>
          <tr>
            <th>Number</th>
            <th>From</th>
            <th>Bank</th>
            <th className="sb-num">Amount</th>
            <th>Status</th>
            {canMove ? <th>Next</th> : null}
          </tr>
        </thead>
        <tbody>
          {(cheques.data ?? []).map((c) => (
            <tr key={c.id}>
              <td>{c.kind === 'DEMAND_DRAFT' ? 'DD ' : ''}{c.number}<span className="sb-muted"> ({c.receiptNumber})</span></td>
              <td>{c.debtorName}</td>
              <td>{c.bankName ?? '-'}{c.chequeDate ? `, dated ${c.chequeDate}` : ''}</td>
              <td className="sb-num">{moneyFormat.format(c.amount)}</td>
              <td title={c.history.map((h) => `${ChequeStatusLabels[h.status]} ${h.eventDate} by ${h.recordedBy}${h.note ? `: ${h.note}` : ''}`).join('\n')}>
                {ChequeStatusLabels[c.status] ?? c.status}{c.replacedByReceiptNumber ? ` by ${c.replacedByReceiptNumber}` : ''}
              </td>
              {canMove ? (
                <td>
                  {(ChequeMoves[c.status] ?? []).length > 0 ? (
                    <div className="sb-inline-form">
                      <input className="sb-input" aria-label={`Note for ${c.number}`} placeholder="Note" value={notes[c.id] ?? ''}
                        onChange={(e) => setNotes((n) => ({ ...n, [c.id]: e.target.value }))} />
                      {c.status === 'BOUNCED' || c.status === 'CANCELLED' ? (
                        <ReplacementSelect business={business} cheque={c} value={replacement[c.id] ?? ''} onChange={(id) => setReplacement((r) => ({ ...r, [c.id]: id }))} />
                      ) : null}
                      {(ChequeMoves[c.status] ?? []).map((to) => (
                        <button key={to} type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => void move(c, to)}>
                          {ChequeStatusLabels[to]}
                        </button>
                      ))}
                    </div>
                  ) : null}
                </td>
              ) : null}
            </tr>
          ))}
        </tbody>
      </table>
      {cheques.data && cheques.data.length === 0 ? <p className="sb-muted">No cheques with this status.</p> : null}
    </section>
  );
}

function ReplacementSelect({ business, cheque, value, onChange }: { business: string | null; cheque: ChequeInfo; value: string; onChange: (id: string) => void }) {
  const receipts = useApiData<DebtorReceipt[]>(business ? `${business}/debtor-receipts?debtorId=${cheque.debtorId}` : null);
  return (
    <select className="sb-input" aria-label={`Replaced by (for ${cheque.number})`} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">Replaced by...</option>
      {(receipts.data ?? []).filter((r) => r.id !== cheque.receiptId && !r.reversalKind).map((r) => (
        <option key={r.id} value={r.id}>{r.number} - Rs. {moneyFormat.format(r.amount)} ({ReceiptMethodLabels[r.method] ?? r.method})</option>
      ))}
    </select>
  );
}

/** A debtor's receipts; one recorded in error can be reversed with another person's approval (cheques via the register). */
export function ReceiptsCard({ business, debtorId }: { business: string | null; debtorId: string }) {
  const { hasPermission } = useAuth();
  const [version, setVersion] = useState(0);
  const receipts = useApiData<DebtorReceipt[]>(business ? `${business}/debtor-receipts?debtorId=${debtorId}&r=${version}` : null);
  const [asking, setAsking] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const canReverse = hasPermission(AccountPermission.Receivables);

  return (
    <details data-testid="receipts">
      <summary>Receipts</summary>
      <ErrorText error={receipts.error ?? error} />
      {notice ? <Notice>{notice}</Notice> : null}
      <ul className="sb-plain-list">
        {(receipts.data ?? []).map((r) => (
          <li key={r.id}>
            {r.number}, {r.receiptDate}: Rs. {moneyFormat.format(r.amount)} by {ReceiptMethodLabels[r.method] ?? r.method}
            {r.reference ? ` (${r.reference})` : ''}{r.chequeStatus ? `, cheque ${ChequeStatusLabels[r.chequeStatus]?.toLowerCase()}` : ''}
            {r.reversalKind ? <span className="sb-error"> - reversed: {r.reversalReason}</span> : null}
            {canReverse && !r.reversalKind && !r.chequeStatus ? (
              asking === r.id ? (
                <span className="sb-inline-form">
                  <input className="sb-input" aria-label="Why reverse it" value={reason} onChange={(e) => setReason(e.target.value)} />
                  <button
                    type="button"
                    className="sb-button sb-button--small"
                    onClick={async () => {
                      setError(null);
                      try {
                        const result = await api.post<{ message: string }>(`${business}/debtor-receipts/${r.id}/reversal`, { reason });
                        setNotice(result.message);
                        setAsking(null);
                        setReason('');
                        setVersion((v) => v + 1);
                      } catch (caught) {
                        setError(caught);
                      }
                    }}
                  >
                    Ask for approval
                  </button>
                </span>
              ) : (
                <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setAsking(r.id)}>Ask to reverse</button>
              )
            ) : null}
          </li>
        ))}
      </ul>
      {receipts.data && receipts.data.length === 0 ? <p className="sb-muted">No receipts yet.</p> : null}
    </details>
  );
}
