'use client';

import { useState } from 'react';
import { api, postForFile } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { ActionForm, ErrorText, Field, formatDateTime, Notice, optional, text } from '../ui';

interface MonthStatus {
  month: string;
  state: 'IN_PROGRESS' | 'OPEN' | 'LOCKED';
  lockedAtUtc: string | null;
  lockedBy: string | null;
  packages: number;
  lastPackageAtUtc: string | null;
  lastPackageSha256: string | null;
}

interface MonthCheck {
  key: string;
  title: string;
  passed: boolean;
  problems: number;
  detail: string;
}

interface ArchiveKeys {
  signerKeyId: string;
  signerPublicKeyPem: string;
  recipientKeyId: string | null;
  recipientSetAtUtc: string | null;
  recipientRowVersion: number | null;
}

const stateLabels: Record<MonthStatus['state'], string> = { IN_PROGRESS: 'In progress', OPEN: 'Over, not locked', LOCKED: 'Locked' };

/**
 * Monthly close (spec section 22): check that a month is complete and reconciles, lock it, and make its encrypted,
 * signed package for the owner's archive server.
 */
export function MonthClosePanel() {
  const { membership, hasPermission } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const canClose = hasPermission('months.close');
  const [version, setVersion] = useState(0);
  const months = useApiData<MonthStatus[]>(base ? `${base}/months?r=${version}` : null);
  const keys = useApiData<ArchiveKeys>(base ? `${base}/archive/keys?r=${version}` : null);
  const [chosen, setChosen] = useState<string | null>(null);

  return (
    <>
      <section className="sb-card" aria-labelledby="months-heading">
        <header className="sb-card__header">
          <h2 id="months-heading">Month close</h2>
        </header>
        <p className="sb-muted">
          Close each month in order once its shifts, collection rounds and offline bills are settled: nothing dated in a locked month can be
          recorded or changed afterwards. Then make its package for the archive server.
        </p>
        <ErrorText error={months.error} />
        <table className="sb-table" data-testid="months-table">
          <thead>
            <tr>
              <th>Month</th>
              <th>State</th>
              <th>Locked</th>
              <th>Packages</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(months.data ?? []).map((m) => (
              <tr key={m.month} aria-selected={chosen === m.month}>
                <td>{m.month}</td>
                <td>{stateLabels[m.state]}</td>
                <td>{m.lockedAtUtc ? `${formatDateTime(m.lockedAtUtc)} by ${m.lockedBy}` : ''}</td>
                <td>{m.packages > 0 ? `${m.packages} (last ${formatDateTime(m.lastPackageAtUtc)})` : ''}</td>
                <td>
                  {m.state !== 'IN_PROGRESS' ? (
                    <button type="button" className="sb-link" onClick={() => setChosen(m.month)}>Open</button>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      {chosen && base ? (
        <MonthCard
          key={`${chosen}-${version}`}
          base={base}
          month={(months.data ?? []).find((m) => m.month === chosen)!}
          canClose={canClose}
          hasRecipient={!!keys.data?.recipientKeyId}
          changed={() => setVersion((v) => v + 1)}
        />
      ) : null}
      {keys.data && base ? <ArchiveKeysCard base={base} keys={keys.data} canClose={canClose} changed={() => setVersion((v) => v + 1)} /> : null}
    </>
  );
}

function MonthCard({ base, month, canClose, hasRecipient, changed }: {
  base: string;
  month: MonthStatus;
  canClose: boolean;
  hasRecipient: boolean;
  changed: () => void;
}) {
  const checks = useApiData<{ ready: boolean; checks: MonthCheck[] }>(month.state === 'OPEN' ? `${base}/months/${month.month}/checks` : null);
  const [error, setError] = useState<unknown>(null);
  const [made, setMade] = useState<string | null>(null);

  async function download() {
    setError(null);
    try {
      const { blob, fileName } = await postForFile(`${base}/months/${month.month}/package`);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName;
      link.click();
      URL.revokeObjectURL(url);
      setMade(fileName);
      changed();
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <section className="sb-card" aria-labelledby="month-heading" data-testid="month-card">
      <header className="sb-card__header">
        <h2 id="month-heading">{month.month}: {stateLabels[month.state]}</h2>
      </header>
      <ErrorText error={checks.error ?? error} />
      {checks.data ? (
        <>
          <ul className="sb-plain-list" data-testid="month-checks">
            {checks.data.checks.map((c) => (
              <li key={c.key}>
                <span className={c.passed ? 'sb-chip' : 'sb-chip sb-chip--pending'}>{c.passed ? 'OK' : `${c.problems} to settle`}</span> {c.title}
                {c.passed ? null : <span className="sb-muted"> - {c.detail}</span>}
              </li>
            ))}
          </ul>
          {canClose && checks.data.ready ? (
            <ActionForm
              testId="lock-month-form"
              submitLabel={`Lock ${month.month}`}
              onSubmit={async (data) => {
                if (!window.confirm(`Lock ${month.month}? Nothing dated in it can be recorded or changed afterwards. This cannot be undone.`)) return;
                await api.post(`${base}/months/${month.month}/lock`, { note: optional(data, 'note') });
                changed();
              }}
            >
              <Field label="Note (optional)" name="note" maxLength={300} />
            </ActionForm>
          ) : null}
        </>
      ) : null}
      {month.state === 'LOCKED' ? (
        <>
          {made ? <Notice tone="success">Package {made} saved. Copy it to the archive server and import it there.</Notice> : null}
          {month.lastPackageSha256 ? <p className="sb-muted">Last package SHA-256: <code>{month.lastPackageSha256}</code></p> : null}
          {canClose ? (
            hasRecipient ? (
              <button type="button" className="sb-button" onClick={() => void download()}>Make archive package</button>
            ) : (
              <Notice tone="warning">Register the archive server&apos;s key below before making a package.</Notice>
            )
          ) : null}
        </>
      ) : null}
    </section>
  );
}

function ArchiveKeysCard({ base, keys, canClose, changed }: { base: string; keys: ArchiveKeys; canClose: boolean; changed: () => void }) {
  return (
    <section className="sb-card" aria-labelledby="archive-keys-heading" data-testid="archive-keys">
      <header className="sb-card__header">
        <h2 id="archive-keys-heading">Archive keys</h2>
      </header>
      <p>
        This server signs its packages with key <code>{keys.signerKeyId}</code>. Register this public key on the archive server, so it accepts
        packages from here:
      </p>
      <textarea className="sb-input" readOnly rows={4} value={keys.signerPublicKeyPem} aria-label="This server's public key" />
      <p>
        {keys.recipientKeyId
          ? `Packages are encrypted for the archive server with key ${keys.recipientKeyId} (registered ${formatDateTime(keys.recipientSetAtUtc)}).`
          : 'No archive server is registered yet.'}
      </p>
      {canClose ? (
        <ActionForm
          testId="archive-recipient-form"
          submitLabel={keys.recipientKeyId ? 'Replace the archive key' : 'Register the archive key'}
          onSubmit={async (data) => {
            await api.put(`${base}/archive/recipient`, { publicKeyPem: text(data, 'pem'), rowVersion: keys.recipientRowVersion });
            changed();
          }}
        >
          <label className="sb-field">
            <span className="sb-field__label">The archive server&apos;s public key (from its screen)</span>
            <textarea className="sb-input" name="pem" rows={4} required placeholder="-----BEGIN PUBLIC KEY-----" />
          </label>
        </ActionForm>
      ) : null}
    </section>
  );
}
