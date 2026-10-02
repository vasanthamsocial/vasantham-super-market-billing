'use client';

import { Fragment, useState } from 'react';
import { CashMovementLabels, Denominations, PaymentMethodLabels, RefundMethodLabels, type DenominationCount, type ShiftSummary } from '../types';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export function useDenominationCounts() {
  const [counts, setCounts] = useState<Record<number, string>>({});
  const list: DenominationCount[] = Denominations.map((d) => ({ denomination: d, count: Number(counts[d] || 0) })).filter((c) => c.count > 0);
  const total = list.reduce((sum, c) => sum + c.denomination * c.count, 0);
  return { counts, setCounts, list, total, valid: Object.values(counts).every((v) => v === '' || /^\d+$/.test(v)) };
}

/** Notes and coins in the drawer, counted one denomination at a time. */
export function DenominationGrid({ state, label }: { state: ReturnType<typeof useDenominationCounts>; label: string }) {
  return (
    <fieldset className="sb-fieldset" data-testid="denomination-grid">
      <legend>{label}</legend>
      <table className="sb-table">
        <thead>
          <tr>
            <th>Note / coin</th>
            <th>Count</th>
            <th className="sb-num">Amount</th>
          </tr>
        </thead>
        <tbody>
          {Denominations.map((d, i) => (
            <tr key={d}>
              <td>Rs. {d}</td>
              <td>
                <input
                  className="sb-input"
                  inputMode="numeric"
                  aria-label={`Number of Rs. ${d}`}
                  autoFocus={i === 0}
                  value={state.counts[d] ?? ''}
                  onChange={(e) => state.setCounts((c) => ({ ...c, [d]: e.target.value }))}
                />
              </td>
              <td className="sb-num">{money.format(d * Number(state.counts[d] || 0))}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <th colSpan={2}>Total</th>
            <th className="sb-num" data-testid="count-total">Rs. {money.format(state.total)}</th>
          </tr>
        </tfoot>
      </table>
    </fieldset>
  );
}

/** A shift's report: sales and refunds by method, cash movements and, once known, expected against counted cash. */
export function ShiftReport({ shift }: { shift: ShiftSummary }) {
  return (
    <div className="sb-stack" data-testid="shift-report">
      <dl className="sb-pos__summary">
        <dt>Counter / cashier</dt>
        <dd>{shift.counterCode} / {shift.cashier}</dd>
        <dt>Opened</dt>
        <dd>{new Date(shift.openedAtUtc).toLocaleString('en-IN', { dateStyle: 'short', timeStyle: 'short' })}</dd>
        {shift.closedAtUtc ? (<><dt>Closed</dt><dd>{new Date(shift.closedAtUtc).toLocaleString('en-IN', { dateStyle: 'short', timeStyle: 'short' })}</dd></>) : null}
        <dt>Opening float</dt>
        <dd>{money.format(shift.openingFloat)}</dd>
        <dt>Bills</dt>
        <dd>{shift.invoices} (Rs. {money.format(shift.salesTotal)})</dd>
        <dt>Returns</dt>
        <dd>{shift.returns} (Rs. {money.format(shift.returnsTotal)})</dd>
        {shift.payments.map((p) => (
          <Fragment key={`p${p.method}`}>
            <dt>Received: {PaymentMethodLabels[p.method] ?? p.method}</dt>
            <dd>{money.format(p.amount)}</dd>
          </Fragment>
        ))}
        {shift.refunds.map((r) => (
          <Fragment key={`r${r.method}`}>
            <dt>Refunded: {RefundMethodLabels[r.method] ?? r.method}</dt>
            <dd>{money.format(r.amount)}</dd>
          </Fragment>
        ))}
        {shift.movements.map((m, i) => (
          <Fragment key={`m${i}`}>
            <dt>{CashMovementLabels[m.kind] ?? m.kind}: {m.reason}</dt>
            <dd>{money.format(m.amount)}</dd>
          </Fragment>
        ))}
        {shift.expectedCash !== null ? (<><dt>Expected cash</dt><dd data-testid="shift-expected">{money.format(shift.expectedCash)}</dd></>) : null}
        {shift.countedCash !== null ? (<><dt>Counted cash</dt><dd data-testid="shift-counted">{money.format(shift.countedCash)}</dd></>) : null}
        {shift.difference !== null ? (
          <>
            <dt>Difference</dt>
            <dd data-testid="shift-difference">{shift.difference === 0 ? 'None' : `${money.format(Math.abs(shift.difference))} ${shift.difference > 0 ? 'over' : 'short'}`}</dd>
          </>
        ) : null}
      </dl>
      {shift.closeNote ? <p className="sb-muted">Note: {shift.closeNote}</p> : null}
      {shift.needsReview ? <p className="sb-notice sb-notice--warning" role="status">The difference is waiting for a manager's review.</p> : null}
      {shift.reviewedBy ? <p className="sb-muted">Reviewed by {shift.reviewedBy}: {shift.reviewNote}</p> : null}
      {shift.parkedBillsCleared > 0 ? <p className="sb-muted">{shift.parkedBillsCleared} parked bill(s) were cleared.</p> : null}
    </div>
  );
}
