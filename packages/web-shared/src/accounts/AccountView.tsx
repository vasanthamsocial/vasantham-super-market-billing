'use client';

import { useState, type ReactNode } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { AccountPermission, LedgerEntryTypeLabels, type OpenItems, type Statement } from '../types';
import { ActionForm, ErrorText, Field, Notice, optional, text } from '../ui';
import { moneyFormat } from '../stock/StockPanel';

export type PartyType = 'SUPPLIER' | 'DEBTOR';

const words = {
  SUPPLIER: { up: 'Billed', down: 'Paid', owed: 'Owed to supplier', advance: 'Advance paid', unapplied: 'Payments not yet applied to bills' },
  DEBTOR: { up: 'Charged', down: 'Received', owed: 'Owed by debtor', advance: 'Advance received', unapplied: 'Receipts not yet applied to invoices' },
} as const;

export function balanceText(partyType: PartyType, balance: number): string {
  const w = words[partyType];
  if (balance === 0) return 'Nothing owed';
  return balance > 0 ? `${w.owed}: Rs. ${moneyFormat.format(balance)}` : `${w.advance}: Rs. ${moneyFormat.format(-balance)}`;
}

/**
 * One supplier's or debtor's account: what is owed with ageing, the open bills, the statement with running balance,
 * the opening balance (only before anything else is posted) and corrections, which wait for another person's approval.
 */
export function AccountView({
  business,
  partyType,
  partyId,
  title,
  actions,
  refreshKey = '',
  onChanged,
}: {
  business: string | null;
  partyType: PartyType;
  partyId: string;
  title: string;
  actions?: (open: OpenItems | null, reload: () => Promise<void>) => ReactNode;
  refreshKey?: string;
  onChanged?: () => void;
}) {
  const { hasPermission } = useAuth();
  const collection = partyType === 'SUPPLIER' ? 'suppliers' : 'debtors';
  const base = business ? `${business}/${collection}/${partyId}` : null;
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [version, setVersion] = useState(0);
  const key = `${refreshKey}-${version}`;
  const open = useApiData<OpenItems>(base ? `${base}/open-items?r=${key}` : null);
  const statement = useApiData<Statement>(base ? `${base}/statement?r=${key}${from ? `&from=${from}` : ''}${to ? `&to=${to}` : ''}` : null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const w = words[partyType];
  const canAdjust = hasPermission(AccountPermission.Adjust);
  const canSettle = hasPermission(partyType === 'SUPPLIER' ? AccountPermission.Payables : AccountPermission.Receivables);

  async function reload() {
    setVersion((v) => v + 1);
    onChanged?.();
  }

  const o = open.data;
  const noEntries = statement.data !== null && statement.data.entries.length === 0 && !from && !to;

  return (
    <section className="sb-card" aria-labelledby="account-heading" data-testid="account-view">
      <header className="sb-card__header">
        <h2 id="account-heading">{title}</h2>
        {o ? (
          <p data-testid="account-balance">
            {balanceText(partyType, o.balance)}
            {o.overdue > 0 ? <span className="sb-error"> (overdue Rs. {moneyFormat.format(o.overdue)})</span> : null}
          </p>
        ) : null}
      </header>
      <ErrorText error={open.error ?? statement.error ?? error} />
      {notice ? <Notice tone="success">{notice}</Notice> : null}

      {o ? (
        <>
          <table className="sb-table sb-table--compact" aria-label="Ageing">
            <thead>
              <tr>
                <th className="sb-num">Not due</th>
                <th className="sb-num">1-30 days</th>
                <th className="sb-num">31-60 days</th>
                <th className="sb-num">61-90 days</th>
                <th className="sb-num">Over 90 days</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                {[o.ageing.notDue, o.ageing.days1To30, o.ageing.days31To60, o.ageing.days61To90, o.ageing.over90].map((v, i) => (
                  <td key={i} className="sb-num">{moneyFormat.format(v)}</td>
                ))}
              </tr>
            </tbody>
          </table>

          <h3>Open bills</h3>
          {o.charges.length === 0 ? <p className="sb-muted">Nothing unpaid.</p> : null}
          {o.charges.length > 0 ? (
            <table className="sb-table" data-testid="open-charges">
              <thead>
                <tr>
                  <th>Document</th>
                  <th>Date</th>
                  <th>Due</th>
                  <th className="sb-num">Amount</th>
                  <th className="sb-num">Unpaid</th>
                  <th className="sb-num">Days overdue</th>
                </tr>
              </thead>
              <tbody>
                {o.charges.map((c) => (
                  <tr key={c.entryId}>
                    <td>{c.documentNumber ?? LedgerEntryTypeLabels[c.entryType] ?? c.entryType}</td>
                    <td>{c.entryDate}</td>
                    <td>{c.dueDate ?? '-'}</td>
                    <td className="sb-num">{moneyFormat.format(c.amount)}</td>
                    <td className="sb-num">{moneyFormat.format(c.remaining)}</td>
                    <td className={`sb-num${c.daysOverdue > 0 ? ' sb-error' : ''}`}>{c.daysOverdue || '-'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : null}
          {o.unappliedPayments.length > 0 ? (
            <>
              <p className="sb-muted">
                {w.unapplied}: Rs. {moneyFormat.format(o.unappliedPayments.reduce((s, p) => s + p.remaining, 0))} (
                {o.unappliedPayments.map((p) => p.documentNumber ?? LedgerEntryTypeLabels[p.entryType]).join(', ')}).
              </p>
              {canSettle && o.charges.length > 0 ? (
                <button
                  type="button"
                  className="sb-button sb-button--secondary"
                  onClick={async () => {
                    setError(null);
                    try {
                      await api.post(`${base}/apply-payments`);
                      setNotice('Payments applied to the oldest bills.');
                      await reload();
                    } catch (caught) {
                      setError(caught);
                    }
                  }}
                >
                  Apply to bills
                </button>
              ) : null}
            </>
          ) : null}
        </>
      ) : null}

      {actions ? actions(o, reload) : null}

      <h3>Statement</h3>
      <div className="sb-inline-form">
        <label className="sb-field">
          <span className="sb-field__label">From</span>
          <input className="sb-input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className="sb-field">
          <span className="sb-field__label">To</span>
          <input className="sb-input" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>
      {statement.data ? (
        <table className="sb-table" data-testid="statement">
          <thead>
            <tr>
              <th>Date</th>
              <th>Entry</th>
              <th>Document</th>
              <th>Details</th>
              <th className="sb-num">{w.up}</th>
              <th className="sb-num">{w.down}</th>
              <th className="sb-num">Balance</th>
              <th className="sb-num">Open</th>
            </tr>
          </thead>
          <tbody>
            {from ? (
              <tr>
                <td colSpan={6}>Brought forward</td>
                <td className="sb-num">{moneyFormat.format(statement.data.openingBalance)}</td>
                <td />
              </tr>
            ) : null}
            {statement.data.entries.map((e) => (
              <tr key={e.id}>
                <td>{e.entryDate}</td>
                <td>{LedgerEntryTypeLabels[e.entryType] ?? e.entryType}</td>
                <td>{e.documentNumber ?? '-'}</td>
                <td>
                  {e.narration} <span className="sb-muted">({e.createdBy})</span>
                </td>
                <td className="sb-num">{e.amount > 0 ? moneyFormat.format(e.amount) : ''}</td>
                <td className="sb-num">{e.amount < 0 ? moneyFormat.format(-e.amount) : ''}</td>
                <td className="sb-num">{moneyFormat.format(e.balanceAfter)}</td>
                <td className="sb-num">{e.outstanding > 0 ? moneyFormat.format(e.outstanding) : '-'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}

      {canAdjust && noEntries ? (
        <ActionForm
          submitLabel="Enter opening balance"
          testId="opening-balance-form"
          onSubmit={async (data) => {
            setNotice(null);
            await api.post(`${base}/opening-balance`, {
              amount: Number(text(data, 'amount')),
              asOf: text(data, 'asOf'),
              dueDate: optional(data, 'dueDate'),
              note: optional(data, 'note'),
            });
            setNotice('Opening balance entered.');
            await reload();
          }}
        >
          <h3>Opening balance</h3>
          <div className="sb-form-row">
            <Field label="Amount (Rs.)" name="amount" inputMode="decimal" required hint={`Positive: ${w.owed.toLowerCase()}; negative: an advance`} />
            <Field label="As of" name="asOf" type="date" required />
            <Field label="Due on (optional)" name="dueDate" type="date" />
            <Field label="Note (optional)" name="note" />
          </div>
        </ActionForm>
      ) : null}

      {canAdjust && !noEntries ? (
        <details>
          <summary>Ask for a correction</summary>
          <ActionForm
            submitLabel="Ask for approval"
            testId="adjustment-form"
            onSubmit={async (data) => {
              setNotice(null);
              const result = await api.post<{ message: string }>(`${base}/adjustments`, { amount: Number(text(data, 'amount')), reason: text(data, 'reason') });
              setNotice(result.message);
            }}
          >
            <div className="sb-form-row">
              <Field label="Correction (Rs.)" name="amount" inputMode="decimal" required hint="Positive adds to what is owed; negative reduces it" />
              <Field label="Reason" name="reason" required />
            </div>
          </ActionForm>
        </details>
      ) : null}
    </section>
  );
}
