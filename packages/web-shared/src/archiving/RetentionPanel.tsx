'use client';

import { useState } from 'react';
import { api, uploadFile } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { ActionForm, ErrorText, formatDateTime, Notice, text } from '../ui';

// Retention (spec section 22, D-044): removing archived months from this server, behind the archive's receipt and
// every gate, by the owner only.

interface RetentionMonth {
  month: string;
  locked: boolean;
  packaged: boolean;
  receiptRegisteredAtUtc: string | null;
  archiveApprovedAtUtc: string | null;
  removedAtUtc: string | null;
}

interface Retention {
  enabled: boolean;
  keepFrom: string;
  archiveRegistered: boolean;
  receiptKeyId: string | null;
  receiptKeySetAtUtc: string | null;
  months: RetentionMonth[];
}

interface RetentionPlan {
  month: string;
  ready: boolean;
  checks: { code: string; description: string; passed: boolean; detail: string | null }[];
  blockers: { code: string; description: string; count: number; examples: string[] }[];
  removed: { dataset: string; records: number }[];
  kept: { dataset: string; records: number }[];
  note: string;
}

const number = new Intl.NumberFormat('en-IN');
const label = (dataset: string) => dataset.replace(/_/g, ' ');

export function RetentionPanel() {
  const { membership, hasPermission } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/retention` : null;
  const owner = hasPermission('months.remove');
  const [version, setVersion] = useState(0);
  const status = useApiData<Retention>(base ? `${base}?r=${version}` : null);
  const [plan, setPlan] = useState<RetentionPlan | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const data = status.data;

  async function open(month: string) {
    setError(null);
    try {
      setPlan(await api.get<RetentionPlan>(`${base}/months/${month}`));
    } catch (caught) {
      setPlan(null);
      setError(caught);
    }
  }

  async function register() {
    if (!file || !base) return;
    setBusy(true);
    setError(null);
    try {
      setPlan(await uploadFile<RetentionPlan>(`${base}/receipts`, file));
      setVersion((v) => v + 1);
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  if (!base) return null;

  return (
    <>
      <section className="sb-card" aria-labelledby="retention-heading" data-testid="retention">
        <header className="sb-card__header">
          <h2 id="retention-heading">Retention</h2>
        </header>
        <ErrorText error={status.error ?? error} />
        {notice ? <Notice tone="success">{notice}</Notice> : null}
        {data ? (
          <>
            <p>
              This server always keeps the current and the previous financial year (from <strong>{data.keepFrom}</strong>). An older month may be
              removed from here only once the archive server confirms, with a signed receipt, that it holds that month approved by the accountant
              and the owner.
            </p>
            {!data.enabled ? (
              <Notice tone="info">Retention is switched off on this server. Archived months stay here.</Notice>
            ) : (
              <>
                <p className="sb-muted">
                  {data.receiptKeyId
                    ? `Receipts are accepted from the archive key ${data.receiptKeyId} (registered ${formatDateTime(data.receiptKeySetAtUtc)}).`
                    : 'No archive receipt key is registered yet.'}
                </p>
                {owner && data.archiveRegistered ? (
                  <ActionForm
                    testId="receipt-key-form"
                    submitLabel={data.receiptKeyId ? 'Replace the receipt key' : 'Register the receipt key'}
                    onSubmit={async (form) => {
                      await api.put(`${base}/receipt-key`, { publicKeyPem: text(form, 'pem') });
                      setVersion((v) => v + 1);
                    }}
                  >
                    <label className="sb-field">
                      <span className="sb-field__label">The archive&apos;s receipt key (from its Store servers screen)</span>
                      <textarea className="sb-input" name="pem" rows={4} required placeholder="-----BEGIN PUBLIC KEY-----" />
                    </label>
                  </ActionForm>
                ) : null}
                {owner && data.receiptKeyId ? (
                  <div className="sb-inline-form" data-testid="receipt-upload">
                    <input className="sb-input" type="file" accept=".sbreceipt,application/json" aria-label="Archive receipt" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
                    <button type="button" className="sb-button" disabled={!file || busy} onClick={() => void register()}>{busy ? 'Checking...' : 'Register the receipt'}</button>
                  </div>
                ) : null}
                {data.months.length === 0 ? <p className="sb-muted">No month is old enough to be removed.</p> : (
                  <table className="sb-table" data-testid="retention-months">
                    <thead>
                      <tr>
                        <th>Month</th>
                        <th>Locked</th>
                        <th>Archive receipt</th>
                        <th />
                      </tr>
                    </thead>
                    <tbody>
                      {data.months.map((m) => (
                        <tr key={m.month}>
                          <td>{m.month}</td>
                          <td>{m.locked ? (m.packaged ? 'Locked, packaged' : 'Locked') : 'Not locked'}</td>
                          <td>
                            {m.removedAtUtc ? `Removed ${formatDateTime(m.removedAtUtc)}; in the archive`
                              : m.receiptRegisteredAtUtc ? `Approved in the archive ${formatDateTime(m.archiveApprovedAtUtc)}` : ''}
                          </td>
                          <td><button type="button" className="sb-link" onClick={() => void open(m.month)}>Check</button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </>
            )}
          </>
        ) : null}
      </section>
      {plan ? (
        <RetentionPlanView
          plan={plan}
          canRemove={owner}
          onRemove={async (confirmation) => {
            const removed = await api.post<{ month: string; counts: Record<string, number> }>(`${base}/months/${plan.month}/remove`, { confirmation });
            setPlan(null);
            setNotice(`${removed.month} was removed from this server: ${Object.entries(removed.counts).filter(([, n]) => n > 0)
              .map(([k, n]) => `${label(k)} ${number.format(n)}`).join('; ')}.`);
            setVersion((v) => v + 1);
          }}
        />
      ) : null}
    </>
  );
}

function RetentionPlanView({ plan, canRemove, onRemove }: { plan: RetentionPlan; canRemove: boolean; onRemove: (confirmation: string) => Promise<void> }) {
  return (
    <section className="sb-card" aria-labelledby="retention-plan-heading" data-testid="retention-plan">
      <header className="sb-card__header">
        <h2 id="retention-plan-heading">Removing {plan.month}</h2>
        <span className="sb-muted">{plan.ready ? 'Every gate passes' : 'Not possible yet'}</span>
      </header>
      <Notice tone="warning">{plan.note}</Notice>
      <ul className="sb-plain-list">
        {plan.checks.map((c) => (
          <li key={c.code}>
            {c.passed ? 'Yes' : 'No'}: {c.description}
            {c.detail ? <span className="sb-muted"> ({c.detail})</span> : null}
          </li>
        ))}
      </ul>
      {plan.blockers.length > 0 ? (
        <>
          <h3>What stops it</h3>
          <ul className="sb-plain-list">
            {plan.blockers.map((b) => (
              <li key={b.code}>{b.description}: {number.format(b.count)}{b.examples.length > 0 ? <span className="sb-muted"> (for example {b.examples.join('; ')})</span> : null}</li>
            ))}
          </ul>
        </>
      ) : null}
      <h3>Would be removed (the archive holds them)</h3>
      {plan.removed.length === 0 ? <p className="sb-muted">Nothing.</p> : (
        <p>{plan.removed.map((r) => `${label(r.dataset)} ${number.format(r.records)}`).join('; ')}</p>
      )}
      <h3>Would stay</h3>
      {plan.kept.length === 0 ? <p className="sb-muted">Nothing.</p> : (
        <p>{plan.kept.map((r) => `${label(r.dataset)} ${number.format(r.records)}`).join('; ')}</p>
      )}
      {plan.ready && canRemove ? (
        <ActionForm testId="remove-month-form" submitLabel={`Remove ${plan.month} from this server`} onSubmit={(form) => onRemove(text(form, 'confirmation'))}>
          <label className="sb-field">
            <span className="sb-field__label">To confirm, type REMOVE {plan.month}</span>
            <input className="sb-input" name="confirmation" autoComplete="off" required />
          </label>
        </ActionForm>
      ) : null}
    </section>
  );
}
