'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import type { OfflineBillRecord } from '../types';
import { ActionForm, ErrorText, Field, formatDateTime, text } from '../ui';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/**
 * Bills counters issued without the server (D-039) that need someone: quarantined ones (their number is used, but they
 * could not be posted) to post or record as not posted, and posted ones with something to check.
 */
export function OfflineBillsCard() {
  const { membership, hasPermission } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/offline-bills` : null;
  const [version, setVersion] = useState(0);
  const waiting = useApiData<OfflineBillRecord[]>(base ? `${base}?status=QUARANTINED&r=${version}` : null);
  const review = useApiData<OfflineBillRecord[]>(base ? `${base}?status=REVIEW&r=${version}` : null);
  const [open, setOpen] = useState<string | null>(null);
  const canDecide = hasPermission('shifts.manage');
  const all = [...(waiting.data ?? []), ...(review.data ?? [])];
  if (!waiting.error && !review.error && all.length === 0) return null;

  return (
    <section className="sb-card" aria-labelledby="offline-bills-heading" data-testid="offline-bills">
      <header className="sb-card__header">
        <h2 id="offline-bills-heading">Bills issued without the server</h2>
      </header>
      <ErrorText error={waiting.error ?? review.error} />
      <ul className="sb-plain-list">
        {all.map((b) => (
          <li key={b.id}>
            <strong>{b.number}</strong> ({b.counterCode}, {b.cashier}, {formatDateTime(b.issuedAtUtc)}): Rs. {money.format(b.grandTotal)}.{' '}
            {b.status === 'QUARANTINED' ? <span className="sb-chip sb-chip--pending">Not posted: {b.reason}</span> : <span>To check: {b.review}</span>}
            {canDecide ? (
              <button type="button" className="sb-link" onClick={() => setOpen(open === b.id ? null : b.id)}>Decide</button>
            ) : null}
            {open === b.id ? (
              <ActionForm
                submitLabel="Save"
                onSubmit={async (data) => {
                  const note = text(data, 'note');
                  if (b.status === 'QUARANTINED') {
                    await api.post(`${base}/${b.id}/resolve`, { post: data.get('decision') === 'post', note, rowVersion: b.rowVersion });
                  } else {
                    await api.post(`${base}/${b.id}/review`, { note, rowVersion: b.rowVersion });
                  }
                  setOpen(null);
                  setVersion((v) => v + 1);
                }}
              >
                {b.status === 'QUARANTINED' ? (
                  <label className="sb-field">
                    <span className="sb-field__label">Decision</span>
                    <select className="sb-input" name="decision" defaultValue="post">
                      <option value="post">Post it now (in its cashier&apos;s open shift)</option>
                      <option value="void">Record it as not posted</option>
                    </select>
                  </label>
                ) : null}
                <Field label={b.status === 'QUARANTINED' ? 'Why' : 'What was checked'} name="note" maxLength={300} required />
              </ActionForm>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  );
}
