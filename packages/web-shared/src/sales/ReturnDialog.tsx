'use client';

import { useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError, errorMessage } from '../api';
import { RefundMethodLabels, type CreditNote, type ReturnableInvoice, type ReturnPreview, type SupervisorApproval } from '../types';
import { SupervisorApprovalForm } from './SupervisorApprovalForm';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const qty = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });

function newKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
}

interface Choice {
  quantity: string;
  restock: boolean;
}

/**
 * Goods back against an invoice of this store: choose the quantities, see the refund the server computes, settle it
 * (cash, card, UPI, wallet, store credit for an exchange, or off the customer's account) and get a credit note.
 */
export function ReturnDialog({ businessId, onClose }: { businessId: string; onClose: () => void }) {
  const [number, setNumber] = useState('');
  const [found, setFound] = useState<ReturnableInvoice | null>(null);
  const [choices, setChoices] = useState<Record<string, Choice>>({});
  const [preview, setPreview] = useState<ReturnPreview | null>(null);
  const [reason, setReason] = useState('');
  const [method, setMethod] = useState('CASH');
  const [reference, setReference] = useState('');
  const [approval, setApproval] = useState<{ token: string; max: number; by: string } | null>(null);
  const [asking, setAsking] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<CreditNote | null>(null);
  const attempt = useRef<{ body: string; key: string } | null>(null);

  const lines = found
    ? found.lines
        .filter((l) => Number(choices[l.originalLineId]?.quantity || 0) > 0)
        .map((l) => ({ originalLineId: l.originalLineId, quantity: Number(choices[l.originalLineId]!.quantity), restock: choices[l.originalLineId]!.restock }))
    : [];
  const linesKey = JSON.stringify(lines);

  useEffect(() => {
    if (!found || lines.length === 0) {
      setPreview(null);
      return;
    }
    let current = true;
    const timer = window.setTimeout(() => {
      api
        .post<ReturnPreview>('/api/v1/pos/returns/preview', { originalInvoiceId: found.invoice.id, lines })
        .then((p) => { if (current) { setPreview(p); setError(null); } })
        .catch((e) => { if (current) { setPreview(null); setError(e); } });
    }, 150);
    return () => { current = false; window.clearTimeout(timer); };
    // linesKey stands for the line choices (a new array every render).
  }, [found, linesKey]);

  async function find(event: FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      const invoice = await api.get<ReturnableInvoice>(`/api/v1/pos/returns/invoice?number=${encodeURIComponent(number.trim())}`);
      setFound(invoice);
      setChoices(Object.fromEntries(invoice.lines.map((l) => [l.originalLineId, { quantity: '', restock: true }])));
    } catch (caught) {
      setError(caught);
    }
  }

  async function issue() {
    if (!found || !preview) return;
    const body = {
      originalInvoiceId: found.invoice.id,
      reason: reason.trim(),
      lines,
      refunds: [{ method, amount: preview.grandTotal, reference: reference.trim() || null }],
      expectedGrandTotal: preview.grandTotal,
      approvalToken: approval && preview.grandTotal <= approval.max ? approval.token : null,
    };
    const text = JSON.stringify(body);
    if (attempt.current?.body !== text) attempt.current = { body: text, key: newKey() };
    setBusy(true);
    setError(null);
    try {
      setDone(await api.post<CreditNote>('/api/v1/pos/returns', { ...body, idempotencyKey: attempt.current.key }));
    } catch (caught) {
      if (caught instanceof ApiError) attempt.current = null;
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  const needsApproval = !!preview && preview.needsApproval && !(approval && preview.grandTotal <= approval.max);

  return (
    <div className="sb-modal" role="dialog" aria-modal="true" aria-label="Return goods" data-testid="pos-return">
      <div className="sb-modal__box sb-modal__box--wide">
        <header className="sb-card__header">
          <h2>{done ? `Credit note ${done.number}` : 'Return goods'}</h2>
          <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={onClose}>Close (Esc)</button>
        </header>

        {done ? (
          <div className="sb-stack" data-testid="return-done">
            <p className="sb-pos__change">
              {done.storeCredit > 0
                ? `Store credit Rs. ${money.format(done.storeCredit)} on ${done.number}`
                : `Refund Rs. ${money.format(done.grandTotal)} (${RefundMethodLabels[done.refunds[0]?.method ?? ''] ?? ''})`}
            </p>
            <p className="sb-muted">Against invoice {done.originalInvoiceNumber}. {done.lines.length} line(s).</p>
            <div className="sb-actions">
              <a className="sb-button" href={`/api/v1/businesses/${businessId}/sales/returns/${done.id}/pdf`} target="_blank" rel="noopener" data-testid="return-pdf">
                Credit note PDF
              </a>
              <button type="button" className="sb-button sb-button--secondary" autoFocus onClick={onClose}>Back to billing</button>
            </div>
          </div>
        ) : asking && preview ? (
          <SupervisorApprovalForm
            what={`A refund of Rs. ${money.format(preview.grandTotal)}`}
            request={{ kind: 'RETURN', maxAmount: preview.grandTotal }}
            onApproved={(a: SupervisorApproval) => { setApproval({ token: a.token, max: preview.grandTotal, by: a.approvedBy }); setAsking(false); }}
          />
        ) : (
          <>
            <form className="sb-inline-form" onSubmit={find}>
              <input className="sb-input" aria-label="Invoice number" placeholder="Invoice number, for example C1-000123" value={number} autoFocus onChange={(e) => setNumber(e.target.value)} />
              <button className="sb-button" type="submit">Find invoice</button>
            </form>
            {found ? (
              <>
                <p className="sb-muted">
                  {found.invoice.number}, {new Date(found.invoice.issuedAtUtc).toLocaleString('en-IN', { dateStyle: 'short', timeStyle: 'short' })}, Rs. {money.format(found.invoice.grandTotal)}
                </p>
                <table className="sb-table" data-testid="return-lines">
                  <thead>
                    <tr>
                      <th>Item</th>
                      <th className="sb-num">Sold</th>
                      <th className="sb-num">Can return</th>
                      <th>Return</th>
                      <th>Back on shelf</th>
                    </tr>
                  </thead>
                  <tbody>
                    {found.lines.map((l) => (
                      <tr key={l.originalLineId}>
                        <td>{l.description}</td>
                        <td className="sb-num">{qty.format(l.sold)} {l.unitCode}</td>
                        <td className="sb-num">{qty.format(l.returnable)}</td>
                        <td>
                          <input
                            className="sb-input"
                            inputMode="decimal"
                            aria-label={`Return quantity of ${l.description}`}
                            disabled={l.returnable <= 0}
                            value={choices[l.originalLineId]?.quantity ?? ''}
                            onChange={(e) => setChoices((c) => ({ ...c, [l.originalLineId]: { ...c[l.originalLineId]!, quantity: e.target.value } }))}
                          />
                        </td>
                        <td>
                          <input
                            type="checkbox"
                            aria-label={`${l.description} goes back on the shelf`}
                            checked={choices[l.originalLineId]?.restock ?? true}
                            onChange={(e) => setChoices((c) => ({ ...c, [l.originalLineId]: { ...c[l.originalLineId]!, restock: e.target.checked } }))}
                          />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {preview ? (
                  <p className="sb-pos__change" data-testid="return-total">Refund: Rs. {money.format(preview.grandTotal)}</p>
                ) : null}
                <div className="sb-form-row">
                  <div className="sb-field">
                    <label className="sb-field__label" htmlFor="return-reason">Reason</label>
                    <input id="return-reason" className="sb-input" value={reason} onChange={(e) => setReason(e.target.value)} />
                  </div>
                  <label className="sb-field">
                    <span className="sb-field__label">Refund as</span>
                    <select className="sb-input" value={method} onChange={(e) => setMethod(e.target.value)}>
                      {Object.entries(RefundMethodLabels)
                        .filter(([value]) => value !== 'ON_ACCOUNT' || found.invoice.debtorId)
                        .map(([value, label]) => (
                          <option key={value} value={value}>{label}</option>
                        ))}
                    </select>
                  </label>
                  {method !== 'CASH' && method !== 'STORE_CREDIT' && method !== 'ON_ACCOUNT' ? (
                    <div className="sb-field">
                      <label className="sb-field__label" htmlFor="return-reference">Reference</label>
                      <input id="return-reference" className="sb-input" value={reference} onChange={(e) => setReference(e.target.value)} />
                    </div>
                  ) : null}
                </div>
                {approval ? <p className="sb-notice sb-notice--success" role="status">Approved by {approval.by}.</p> : null}
                {needsApproval ? (
                  <button type="button" className="sb-button" onClick={() => setAsking(true)}>Ask a supervisor to approve</button>
                ) : (
                  <button type="button" className="sb-button" disabled={!preview || busy} onClick={() => void issue()}>
                    {busy ? 'Saving...' : 'Issue credit note'}
                  </button>
                )}
              </>
            ) : null}
            {error ? <p className="sb-error" role="alert" data-testid="return-error">{errorMessage(error)}</p> : null}
          </>
        )}
      </div>
    </div>
  );
}
