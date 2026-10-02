'use client';

import { useState } from 'react';
import { api, ApiError, errorMessage } from '../api';
import { SupervisorApprovalForm } from '../sales/SupervisorApprovalForm';
import { CashMovementLabels, type ShiftSummary } from '../types';
import { DenominationGrid, ShiftReport, useDenominationCounts } from './ShiftViews';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** No shift is open on this counter: the cashier counts the opening float to start one. */
export function OpenShiftPanel({ counterCode, onOpened }: { counterCode: string; onOpened: (shift: ShiftSummary) => void }) {
  const state = useDenominationCounts();
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  return (
    <section className="sb-card sb-pos" aria-labelledby="open-shift-heading" data-testid="open-shift">
      <header className="sb-card__header">
        <h2 id="open-shift-heading">Open a shift on counter {counterCode}</h2>
      </header>
      <p className="sb-muted">Count the cash in the drawer before the first bill. This is your opening float.</p>
      <DenominationGrid state={state} label="Opening float" />
      {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
      <button
        type="button"
        className="sb-button"
        disabled={busy || !state.valid}
        onClick={() => {
          setBusy(true);
          setError(null);
          api
            .post<ShiftSummary>('/api/v1/pos/shift/open', { counts: state.list })
            .then(onOpened)
            .catch(setError)
            .finally(() => setBusy(false));
        }}
      >
        Open shift with Rs. {money.format(state.total)}
      </button>
    </section>
  );
}

/** Cash into or out of the drawer. A cashier's pay-out needs a supervisor. */
export function CashMovementDialog({ onDone, onClose }: { onDone: (shift: ShiftSummary) => void; onClose: () => void }) {
  const [kind, setKind] = useState('PAY_IN');
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [needsApproval, setNeedsApproval] = useState(false);
  const [error, setError] = useState<unknown>(null);

  function save(approvalToken: string | null) {
    setError(null);
    api
      .post<ShiftSummary>('/api/v1/pos/shift/cash', { kind, amount: Number(amount), reason: reason.trim(), approvalToken })
      .then(onDone)
      .catch((e: unknown) => {
        if (e instanceof ApiError && e.status === 403 && kind === 'PAY_OUT' && !approvalToken) setNeedsApproval(true);
        else setError(e);
      });
  }

  return (
    <div className="sb-modal" role="dialog" aria-modal="true" aria-label="Cash in or out" data-testid="pos-cash">
      <div className="sb-modal__box">
        <header className="sb-card__header">
          <h2>Cash in or out</h2>
          <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={onClose}>Close (Esc)</button>
        </header>
        {needsApproval ? (
          <SupervisorApprovalForm
            what={`Paying out Rs. ${money.format(Number(amount))}`}
            request={{ kind: 'PAY_OUT', maxAmount: Number(amount) }}
            onApproved={(a) => save(a.token)}
          />
        ) : (
          <form className="sb-form" onSubmit={(e) => { e.preventDefault(); save(null); }}>
            <label className="sb-field">
              <span className="sb-field__label">What</span>
              <select className="sb-input" value={kind} onChange={(e) => setKind(e.target.value)}>
                {Object.entries(CashMovementLabels).map(([value, label]) => (
                  <option key={value} value={value}>{label}</option>
                ))}
              </select>
            </label>
            <div className="sb-field">
              <label className="sb-field__label" htmlFor="cash-amount">Amount (Rs.)</label>
              <input id="cash-amount" className="sb-input" inputMode="decimal" autoFocus value={amount} onChange={(e) => setAmount(e.target.value)} />
            </div>
            <div className="sb-field">
              <label className="sb-field__label" htmlFor="cash-reason">Reason</label>
              <input id="cash-reason" className="sb-input" value={reason} onChange={(e) => setReason(e.target.value)} />
            </div>
            {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
            <button className="sb-button" type="submit">Record</button>
          </form>
        )}
      </div>
    </div>
  );
}

/**
 * Closing the shift. The count is blind: the cashier counts the drawer before seeing what was expected. A
 * difference needs a note; the reconciliation is shown afterwards.
 */
export function CloseShiftDialog({ onClosed, onClose, closeUrl = '/api/v1/pos/shift/close' }: { onClosed: () => void; onClose: () => void; closeUrl?: string }) {
  const state = useDenominationCounts();
  const [note, setNote] = useState('');
  const [askNote, setAskNote] = useState(false);
  const [result, setResult] = useState<ShiftSummary | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  function submit() {
    setBusy(true);
    setError(null);
    api
      .post<ShiftSummary>(closeUrl, { counts: state.list, note: note.trim() || null })
      .then(setResult)
      .catch((e: unknown) => {
        if (e instanceof ApiError && e.code === 'shift.note_required') setAskNote(true);
        setError(e);
      })
      .finally(() => setBusy(false));
  }

  return (
    <div className="sb-modal" role="dialog" aria-modal="true" aria-label="Close shift" data-testid="pos-close-shift">
      <div className="sb-modal__box sb-modal__box--wide">
        <header className="sb-card__header">
          <h2>{result ? 'Shift closed' : 'Close the shift'}</h2>
          {result ? null : <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={onClose}>Close (Esc)</button>}
        </header>
        {result ? (
          <>
            <ShiftReport shift={result} />
            <button type="button" className="sb-button" autoFocus onClick={onClosed}>Done</button>
          </>
        ) : (
          <>
            <p className="sb-muted">Count all the cash in the drawer. The expected amount is shown after you submit the count.</p>
            <DenominationGrid state={state} label="Cash in the drawer" />
            {askNote || note ? (
              <div className="sb-field">
                <label className="sb-field__label" htmlFor="close-note">Explain the difference</label>
                <input id="close-note" className="sb-input" value={note} autoFocus onChange={(e) => setNote(e.target.value)} />
              </div>
            ) : null}
            {error ? <p className="sb-error" role="alert" data-testid="close-error">{errorMessage(error)}</p> : null}
            <button type="button" className="sb-button" disabled={busy || !state.valid} onClick={submit}>
              Close shift with Rs. {money.format(state.total)}
            </button>
          </>
        )}
      </div>
    </div>
  );
}
