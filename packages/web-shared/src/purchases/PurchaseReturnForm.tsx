'use client';

import { useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import { useApiData } from '../admin/useApiData';
import type { PurchaseReturn, PurchaseReturnSummary, ReturnableGrn } from '../types';
import { ErrorText, Notice } from '../ui';
import { moneyFormat, quantityFormat } from '../stock/StockPanel';
import { newKey, parseNumber } from './PurchaseShared';

/** Sends goods back to the supplier against a posted receipt; the server previews the debit note as quantities are entered. */
export function PurchaseReturnForm({ business, grnId, onReturned }: { business: string | null; grnId: string; onReturned?: () => void }) {
  const [version, setVersion] = useState(0);
  const returnable = useApiData<ReturnableGrn>(business ? `${business}/grns/${grnId}/returnable?r=${version}` : null);
  const [quantities, setQuantities] = useState<Record<string, string>>({});
  const [reason, setReason] = useState('');
  const [idempotencyKey, setIdempotencyKey] = useState(newKey);
  const [preview, setPreview] = useState<PurchaseReturn | null>(null);
  const [previewError, setPreviewError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState<PurchaseReturn | null>(null);
  const sequence = useRef(0);

  const lines = Object.entries(quantities)
    .map(([grnLineId, value]) => ({ grnLineId, quantity: parseNumber(value) }))
    .filter((l): l is { grnLineId: string; quantity: number } => l.quantity !== null && !Number.isNaN(l.quantity) && l.quantity > 0);
  const request = lines.length > 0 && reason.trim().length >= 3 ? JSON.stringify({ grnId, reason: reason.trim(), lines }) : null;

  useEffect(() => {
    if (!business || !request) {
      setPreview(null);
      setPreviewError(null);
      return;
    }
    const current = ++sequence.current;
    const timer = setTimeout(async () => {
      try {
        const result = await api.post<PurchaseReturn>(`${business}/purchase-returns/preview`, JSON.parse(request));
        if (current === sequence.current) {
          setPreview(result);
          setPreviewError(null);
        }
      } catch (caught) {
        if (current === sequence.current) {
          setPreview(null);
          setPreviewError(caught);
        }
      }
    }, 400);
    return () => clearTimeout(timer);
  }, [business, request]);

  async function save() {
    if (!request) return;
    setBusy(true);
    setError(null);
    try {
      const note = await api.post<PurchaseReturn>(`${business}/purchase-returns`, { ...JSON.parse(request), idempotencyKey });
      setSaved(note);
      setQuantities({});
      setReason('');
      setIdempotencyKey(newKey());
      setVersion((v) => v + 1);
      onReturned?.();
    } catch (caught) {
      if (caught instanceof ApiError && caught.status !== 0) setIdempotencyKey(newKey());
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  const g = returnable.data;
  if (g && g.status !== 'POSTED') return null;
  const anythingLeft = g?.lines.some((l) => l.returnable > 0) ?? false;

  return (
    <section aria-label="Return goods to supplier" data-testid="purchase-return-form">
      <h3>Return goods to supplier</h3>
      <ErrorText error={returnable.error} />
      {saved ? (
        <Notice tone="success">
          Debit note {saved.number}: Rs. {moneyFormat.format(saved.total)} deducted from what is owed to {saved.supplierName}.{' '}
          <a href={`${business}/purchase-returns/${saved.id}/pdf`} download>
            Download debit note
          </a>
        </Notice>
      ) : null}
      {g && g.returnNumbers.length > 0 ? <p className="sb-muted">Returned so far: {g.returnNumbers.join(', ')}.</p> : null}
      {g && !anythingLeft ? <p className="sb-muted">Everything received on this receipt has been returned.</p> : null}
      {g && anythingLeft ? (
        <div className="sb-form">
          <table className="sb-table sb-table--compact">
            <thead>
              <tr>
                <th>Item</th>
                <th className="sb-num">Received</th>
                <th className="sb-num">Returned</th>
                <th className="sb-num">Value each</th>
                <th>Return now</th>
              </tr>
            </thead>
            <tbody>
              {g.lines.map((l) => (
                <tr key={l.grnLineId}>
                  <td>
                    {l.description} ({l.unitCode}){l.batchNumber ? `, batch ${l.batchNumber}` : ''}
                  </td>
                  <td className="sb-num">{quantityFormat.format(l.received)}</td>
                  <td className="sb-num">{quantityFormat.format(l.returned)}</td>
                  <td className="sb-num">{moneyFormat.format(l.unitValue)}</td>
                  <td>
                    {l.returnable > 0 ? (
                      <input
                        className="sb-input"
                        inputMode="decimal"
                        aria-label={`Return quantity of ${l.description}`}
                        placeholder={`up to ${quantityFormat.format(l.returnable)}`}
                        value={quantities[l.grnLineId] ?? ''}
                        onChange={(e) => setQuantities((c) => ({ ...c, [l.grnLineId]: e.target.value }))}
                      />
                    ) : (
                      <span className="sb-muted">All returned</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <label className="sb-field">
            <span className="sb-field__label">Reason for the return</span>
            <input className="sb-input" value={reason} maxLength={200} onChange={(e) => setReason(e.target.value)} />
          </label>
          <ErrorText error={previewError} />
          {preview ? (
            <p data-testid="return-preview">
              Debit note: Rs. {moneyFormat.format(preview.total)} (taxable {moneyFormat.format(preview.taxable)}, tax{' '}
              {moneyFormat.format(preview.cgst + preview.sgst + preview.igst + preview.cess)}); stock out at about Rs. {moneyFormat.format(preview.stockValue)}.
            </p>
          ) : null}
          <ErrorText error={error} />
          <button className="sb-button" type="button" disabled={busy || !preview} onClick={() => void save()}>
            {busy ? 'Please wait...' : 'Save debit note'}
          </button>
        </div>
      ) : null}
    </section>
  );
}

/** Debit notes of a store, with their PDFs. */
export function PurchaseReturnsList({ business, storeId, refreshKey }: { business: string | null; storeId: string; refreshKey: string }) {
  const notes = useApiData<PurchaseReturnSummary[]>(business && storeId ? `${business}/purchase-returns?storeId=${storeId}&r=${refreshKey}` : null);
  return (
    <section className="sb-card" aria-labelledby="returns-heading">
      <header className="sb-card__header">
        <h2 id="returns-heading">Purchase returns (debit notes)</h2>
      </header>
      <ErrorText error={notes.error} />
      {notes.data && notes.data.length === 0 ? <p className="sb-muted">No goods returned to suppliers yet.</p> : null}
      {notes.data && notes.data.length > 0 ? (
        <table className="sb-table" data-testid="purchase-returns-table">
          <thead>
            <tr>
              <th>Debit note</th>
              <th>Date</th>
              <th>Supplier</th>
              <th>Against</th>
              <th>Reason</th>
              <th className="sb-num">Amount</th>
            </tr>
          </thead>
          <tbody>
            {notes.data.map((n) => (
              <tr key={n.id}>
                <td>
                  <a href={`${business}/purchase-returns/${n.id}/pdf`} download>
                    {n.number}
                  </a>
                </td>
                <td>{n.businessDate}</td>
                <td>{n.supplierName}</td>
                <td>{n.grnNumber}</td>
                <td>{n.reason}</td>
                <td className="sb-num">{moneyFormat.format(n.total)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
    </section>
  );
}
