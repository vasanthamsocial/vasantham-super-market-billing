'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { StoreSelect, useStoreChoice } from '../stock/StockPanel';
import type { ShiftSummary } from '../types';
import { ActionForm, ErrorText, Field, text } from '../ui';
import { CloseShiftDialog } from './ShiftDialogs';
import { ShiftReport } from './ShiftViews';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** Shifts of a store: open ones and differences waiting for review, or every shift of a day. */
export function ShiftsPanel() {
  const { membership, hasPermission } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/shifts` : null;
  const { stores, storeId, setStoreId } = useStoreChoice();
  const [date, setDate] = useState('');
  const shifts = useApiData<ShiftSummary[]>(base && storeId ? `${base}?storeId=${storeId}${date ? `&date=${date}` : ''}` : null);
  const [openedId, setOpenedId] = useState<string | null>(null);
  const [closing, setClosing] = useState(false);
  const opened = shifts.data?.find((s) => s.id === openedId) ?? null;
  const canManage = hasPermission('shifts.manage');

  return (
    <>
      <section className="sb-card" aria-labelledby="shifts-heading">
        <header className="sb-card__header">
          <h2 id="shifts-heading">Shifts</h2>
        </header>
        <div className="sb-inline-form">
          <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
          <label className="sb-field">
            <span className="sb-field__label">Date (empty: open shifts and differences to review)</span>
            <input className="sb-input" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </label>
        </div>
        <ErrorText error={shifts.error} />
        <table className="sb-table" data-testid="shifts-table">
          <thead>
            <tr>
              <th>Counter</th>
              <th>Cashier</th>
              <th>Opened</th>
              <th>Status</th>
              <th className="sb-num">Bills</th>
              <th className="sb-num">Sales</th>
              <th className="sb-num">Difference</th>
            </tr>
          </thead>
          <tbody>
            {(shifts.data ?? []).map((s) => (
              <tr key={s.id}>
                <td>
                  <button type="button" className="sb-link" onClick={() => setOpenedId(s.id)}>{s.counterCode}</button>
                </td>
                <td>{s.cashier}</td>
                <td>{new Date(s.openedAtUtc).toLocaleString('en-IN', { dateStyle: 'short', timeStyle: 'short' })}</td>
                <td>{s.status === 'OPEN' ? 'Open' : s.needsReview ? 'Closed - review needed' : 'Closed'}</td>
                <td className="sb-num">{s.invoices}</td>
                <td className="sb-num">{money.format(s.salesTotal)}</td>
                <td className="sb-num">{s.difference === null ? '-' : money.format(s.difference)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {shifts.data && shifts.data.length === 0 ? <p className="sb-muted">{date ? 'No shifts on this day.' : 'No open shifts and nothing to review.'}</p> : null}
      </section>

      {opened && base ? (
        <section className="sb-card" aria-labelledby="shift-heading" data-testid="shift-detail">
          <header className="sb-card__header">
            <h2 id="shift-heading">Shift on counter {opened.counterCode}</h2>
          </header>
          <ShiftReport shift={opened} />
          {canManage && opened.needsReview ? (
            <ActionForm
              testId="review-shift-form"
              submitLabel="Accept the explanation"
              onSubmit={async (data) => {
                await api.post(`${base}/${opened.id}/review`, { note: text(data, 'note') });
                await shifts.reload();
              }}
            >
              <Field label="What was found" name="note" required />
            </ActionForm>
          ) : null}
          {canManage && opened.status === 'OPEN' ? (
            <button type="button" className="sb-button sb-button--secondary" onClick={() => setClosing(true)}>Close this shift for the cashier</button>
          ) : null}
        </section>
      ) : null}
      {closing && opened && base ? (
        <CloseShiftDialog
          closeUrl={`${base}/${opened.id}/close`}
          onClose={() => setClosing(false)}
          onClosed={() => {
            setClosing(false);
            void shifts.reload();
          }}
        />
      ) : null}
    </>
  );
}
