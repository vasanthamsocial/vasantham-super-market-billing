'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { CollectionPermission, type DayList, type DayParty, type DebtorReceipt, type PromiseInfo } from '../types';
import { ActionForm, ErrorText, Field, Notice, optional, text } from '../ui';
import { moneyFormat } from '../stock/StockPanel';
import { DayListView } from './DayListView';
import { CollectForm, OutcomeForm, RoundBar } from './FieldCollection';

/** The collector's own day in the Collection App: the round, and today's parties in route order with collecting on each. */
export function CollectorHome() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const [overdue, setOverdue] = useState(false);
  const [version, setVersion] = useState(0);
  const day = useApiData<DayList>(business && hasPermission(CollectionPermission.Collect) ? `${business}/collections/day?includeOverdue=${overdue}&r=${version}` : null);
  const [open, setOpen] = useState<{ debtorId: string; form: 'collect' | 'outcome' } | null>(null);
  const [done, setDone] = useState<DebtorReceipt | null>(null);
  const refresh = () => setVersion((v) => v + 1);

  if (!hasPermission(CollectionPermission.Collect)) {
    return <p className="sb-muted">Your account does not collect. Ask a manager for the collection person role.</p>;
  }

  return (
    <section aria-labelledby="today-heading">
      <h1 id="today-heading">Today{day.data ? ` - ${day.data.date}` : ''}</h1>
      <RoundBar business={business} version={version} onChange={refresh} />
      {done ? (
        <Notice tone="success">
          Receipt {done.number}: Rs. {moneyFormat.format(done.amount)} from {done.debtorName}
          {done.appliedTo.length > 0 ? ` for ${done.appliedTo.map((a) => a.documentNumber ?? 'opening balance').join(', ')}` : ''}. Now owes Rs.{' '}
          {moneyFormat.format(done.balanceAfter)}.
        </Notice>
      ) : null}
      <label className="sb-check">
        <input type="checkbox" checked={overdue} onChange={(e) => setOverdue(e.target.checked)} /> Also my parties with something overdue
      </label>
      <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => void day.reload()}>Refresh</button>
      <ErrorText error={day.error} />
      {day.loading && !day.data ? <p className="sb-muted">Loading your visits...</p> : null}
      {day.data ? (
        <DayListView
          day={day.data}
          action={(party: DayParty) => (
            <div className="sb-actions">
              {open?.debtorId === party.debtorId && open.form === 'collect' ? (
                <CollectForm business={business} party={party} onDone={(receipt) => { setDone(receipt); setOpen(null); refresh(); }} />
              ) : open?.debtorId === party.debtorId && open.form === 'outcome' ? (
                <OutcomeForm business={business} party={party} onDone={() => { setOpen(null); refresh(); }} />
              ) : (
                <>
                  <button type="button" className="sb-button sb-button--small" onClick={() => { setDone(null); setOpen({ debtorId: party.debtorId, form: 'collect' }); }}>
                    Collect
                  </button>
                  <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setOpen({ debtorId: party.debtorId, form: 'outcome' })}>
                    No payment
                  </button>
                </>
              )}
            </div>
          )}
        />
      ) : null}
    </section>
  );
}

const promiseStatus: Record<string, string> = { PENDING: 'Pending', KEPT: 'Kept', BROKEN: 'Broken', CANCELLED: 'Cancelled' };

/** Promises to pay of a debtor: whether each was kept, and recording a new one. */
export function PromisesCard({ business, debtorId }: { business: string | null; debtorId: string }) {
  const { hasPermission } = useAuth();
  const [version, setVersion] = useState(0);
  const promises = useApiData<PromiseInfo[]>(business ? `${business}/debtors/${debtorId}/promises?r=${version}` : null);
  const canManage = hasPermission(CollectionPermission.Manage);
  return (
    <details data-testid="promises">
      <summary>Promises to pay{promises.data?.some((p) => p.status === 'PENDING') ? ' (pending)' : ''}</summary>
      <ErrorText error={promises.error} />
      <ul className="sb-plain-list">
        {(promises.data ?? []).map((p) => (
          <li key={p.id}>
            Rs. {moneyFormat.format(p.amount)} by {p.promisedDate}: {promiseStatus[p.status]} (paid Rs. {moneyFormat.format(p.paidSince)} since){p.note ? ` - ${p.note}` : ''}
            <span className="sb-muted"> recorded by {p.recordedBy}</span>
            {canManage && p.status === 'PENDING' ? (
              <button
                type="button"
                className="sb-button sb-button--secondary sb-button--small"
                onClick={async () => {
                  await api.post(`${business}/promises/${p.id}/cancel`);
                  setVersion((v) => v + 1);
                }}
              >
                Cancel
              </button>
            ) : null}
          </li>
        ))}
      </ul>
      {promises.data && promises.data.length === 0 ? <p className="sb-muted">No promises recorded.</p> : null}
      {canManage ? (
        <ActionForm
          submitLabel="Record promise"
          onSubmit={async (data) => {
            await api.post(`${business}/debtors/${debtorId}/promises`, {
              amount: Number(text(data, 'amount')),
              promisedDate: text(data, 'promisedDate'),
              note: optional(data, 'note'),
            });
            setVersion((v) => v + 1);
          }}
        >
          <div className="sb-form-row">
            <Field label="Promised amount (Rs.)" name="amount" inputMode="decimal" required />
            <Field label="By" name="promisedDate" type="date" required />
            <Field label="Note (optional)" name="note" />
          </div>
        </ActionForm>
      ) : null}
    </details>
  );
}
