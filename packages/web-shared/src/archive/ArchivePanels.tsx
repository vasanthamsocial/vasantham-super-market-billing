'use client';

import { useState, type ReactNode } from 'react';
import { api, uploadFile } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { ActionForm, ErrorText, Field, formatDateTime, Notice, optional, text } from '../ui';

// The Owner Archive Web (spec section 22, D-042): desktop screens of the archive server.

interface ArchiveGrant {
  id: string;
  roleCode: string;
  roleName: string;
  businessId: string | null;
  businessName: string | null;
  storeId: string | null;
  storeName: string | null;
  financialYear: number | null;
  financialYearLabel: string | null;
  reports: string[] | null;
}

interface ArchiveMe {
  userId: string;
  grants: ArchiveGrant[];
  permissions: string[];
}

interface ArchiveImport {
  id: string;
  businessCode: string;
  businessName: string;
  month: string;
  source: string;
  fileSha256: string;
  records: number;
  importedBy: string;
  importedAtUtc: string;
  status: 'VERIFIED' | 'ACCOUNTANT_APPROVED' | 'APPROVED';
  accountantApprovedBy: string | null;
  ownerApprovedBy: string | null;
  rowVersion: number;
  alreadyImported: boolean;
  datasets: { name: string; records: number; totals: Record<string, number>; master: boolean }[];
}

interface ArchiveBusiness {
  id: string;
  code: string;
  name: string;
  stores: { id: string; code: string; name: string }[];
  months: string[];
}

const statusLabels: Record<ArchiveImport['status'], string> = {
  VERIFIED: 'Verified, waiting for the accountant',
  ACCOUNTANT_APPROVED: 'Accountant approved, waiting for the owner',
  APPROVED: 'Approved',
};

const number = new Intl.NumberFormat('en-IN');
const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

function useArchiveMe() {
  return useApiData<ArchiveMe>('/api/v1/archive/me');
}

function can(me: ArchiveMe | null, permission: string): boolean {
  return me?.permissions.includes(permission) ?? false;
}

/** First-time setup of the archive server: its company and owner administrator. */
export function ArchiveSetupForm() {
  const { refresh } = useAuth();
  return (
    <>
      <p className="sb-muted">
        Create the archive&apos;s owner administrator. The setup code is in the file named in the archive server&apos;s startup log (by default{' '}
        <code>App_Data\setup-code.txt</code> next to its API).
      </p>
      <ActionForm
        testId="archive-setup-form"
        submitLabel="Complete setup"
        onSubmit={async (data) => {
          if (data.get('ownerPassword') !== data.get('ownerPasswordConfirm')) throw new Error('The passwords do not match.');
          await api.post('/api/v1/archive/setup', {
            setupCode: text(data, 'setupCode'),
            companyCode: text(data, 'companyCode'),
            companyName: text(data, 'companyName'),
            ownerUsername: text(data, 'ownerUsername'),
            ownerDisplayName: text(data, 'ownerDisplayName'),
            ownerPassword: data.get('ownerPassword'),
          });
          await refresh();
        }}
      >
        <Field label="Setup code" name="setupCode" autoComplete="off" required />
        <Field label="Company code" name="companyCode" hint="3-20 letters or digits." required />
        <Field label="Company name" name="companyName" required />
        <Field label="Owner administrator's username" name="ownerUsername" autoComplete="username" required />
        <Field label="Your name" name="ownerDisplayName" required />
        <Field label="Password" name="ownerPassword" type="password" autoComplete="new-password" hint="At least 10 characters." required />
        <Field label="Repeat password" name="ownerPasswordConfirm" type="password" autoComplete="new-password" required />
      </ActionForm>
    </>
  );
}

/** Imported months: upload a package, see what was verified, and approve (accountant, then owner). */
export function ArchiveImportsPanel() {
  const me = useArchiveMe();
  const [version, setVersion] = useState(0);
  const imports = useApiData<ArchiveImport[]>(`/api/v1/archive/imports?r=${version}`);
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<ArchiveImport | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState<string | null>(null);

  async function upload() {
    if (!file) return;
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      setResult(await uploadFile<ArchiveImport>('/api/v1/archive/imports', file));
      setVersion((v) => v + 1);
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  async function approve(item: ArchiveImport, asOwner: boolean, note: string | null) {
    await api.post(`/api/v1/archive/imports/${item.id}/${asOwner ? 'approve-owner' : 'approve-accounts'}`, { note, rowVersion: item.rowVersion });
    setOpen(null);
    setVersion((v) => v + 1);
  }

  return (
    <>
      {can(me.data, 'archive.import') ? (
        <section className="sb-card" aria-labelledby="import-heading" data-testid="archive-upload">
          <header className="sb-card__header">
            <h2 id="import-heading">Import a month</h2>
          </header>
          <p className="sb-muted">
            Choose the package (.sbarc) the store server made at month close. It is opened only if it comes from a trusted store server and nothing in
            it was changed; every checksum, count and total is checked. The same month again adds nothing.
          </p>
          <div className="sb-inline-form">
            <input className="sb-input" type="file" accept=".sbarc" aria-label="Package" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
            <button type="button" className="sb-button" disabled={!file || busy} onClick={() => void upload()}>{busy ? 'Importing...' : 'Import'}</button>
          </div>
          <ErrorText error={error} />
          {result ? (
            <Notice tone="success">
              {result.alreadyImported
                ? `${result.businessCode} ${result.month} was already archived; nothing was added.`
                : `${result.businessCode} ${result.month} imported and verified: ${number.format(result.records)} records.`}
            </Notice>
          ) : null}
        </section>
      ) : null}

      <section className="sb-card" aria-labelledby="imports-heading">
        <header className="sb-card__header">
          <h2 id="imports-heading">Archived months</h2>
        </header>
        <ErrorText error={imports.error ?? me.error} />
        {imports.data && imports.data.length === 0 ? <p className="sb-muted">No month has been archived yet.</p> : null}
        <table className="sb-table" data-testid="archive-imports">
          <thead>
            <tr>
              <th>Business</th>
              <th>Month</th>
              <th className="sb-num">Records</th>
              <th>From</th>
              <th>Status</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(imports.data ?? []).map((i) => (
              <ImportRow
                key={i.id}
                item={i}
                open={open === i.id}
                toggle={() => setOpen(open === i.id ? null : i.id)}
                canAccountant={can(me.data, 'archive.approve.accounts') && i.status === 'VERIFIED'}
                canOwner={can(me.data, 'archive.approve.owner') && i.status === 'ACCOUNTANT_APPROVED'}
                approve={approve}
              />
            ))}
          </tbody>
        </table>
      </section>
    </>
  );
}

function ImportRow({ item, open, toggle, canAccountant, canOwner, approve }: {
  item: ArchiveImport;
  open: boolean;
  toggle: () => void;
  canAccountant: boolean;
  canOwner: boolean;
  approve: (item: ArchiveImport, asOwner: boolean, note: string | null) => Promise<void>;
}) {
  return (
    <>
      <tr>
        <td>{item.businessCode} - {item.businessName}</td>
        <td>{item.month}</td>
        <td className="sb-num">{number.format(item.records)}</td>
        <td>{item.source}, {formatDateTime(item.importedAtUtc)} by {item.importedBy}</td>
        <td>
          {statusLabels[item.status]}
          {item.accountantApprovedBy ? <span className="sb-muted"> (accountant: {item.accountantApprovedBy}{item.ownerApprovedBy ? `; owner: ${item.ownerApprovedBy}` : ''})</span> : null}
        </td>
        <td>
          <button type="button" className="sb-link" onClick={toggle}>{open ? 'Close' : 'Details'}</button>
        </td>
      </tr>
      {open ? (
        <tr>
          <td colSpan={6}>
            <p className="sb-muted">Package SHA-256: <code>{item.fileSha256}</code></p>
            <table className="sb-table" aria-label="What the month holds">
              <thead>
                <tr>
                  <th>Records</th>
                  <th className="sb-num">Count</th>
                  <th>Totals</th>
                </tr>
              </thead>
              <tbody>
                {item.datasets.filter((d) => !d.master && d.records > 0).map((d) => (
                  <tr key={d.name}>
                    <td>{d.name.replace(/_/g, ' ')}</td>
                    <td className="sb-num">{number.format(d.records)}</td>
                    <td>{Object.entries(d.totals).map(([k, v]) => `${k.replace(/_/g, ' ')} ${money.format(v)}`).join('; ')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {canAccountant || canOwner ? (
              <ActionForm submitLabel={canOwner ? 'Approve as owner' : 'Approve as accountant'} onSubmit={(data) => approve(item, canOwner, optional(data, 'note'))}>
                <Field label="Note (optional)" name="note" maxLength={300} />
              </ActionForm>
            ) : null}
          </td>
        </tr>
      ) : null}
    </>
  );
}

/** This archive's key (for the store servers) and the store servers it trusts. */
export function ArchiveSourcesPanel() {
  const me = useArchiveMe();
  const [version, setVersion] = useState(0);
  const keys = useApiData<{ keyId: string; publicKeyPem: string }>('/api/v1/archive/keys');
  const sources = useApiData<{ id: string; name: string; keyId: string; registeredBy: string; registeredAtUtc: string; isActive: boolean; imports: number }[]>(
    `/api/v1/archive/sources?r=${version}`);
  const manage = can(me.data, 'archive.sources.manage');
  return (
    <>
      <section className="sb-card" aria-labelledby="key-heading" data-testid="archive-key">
        <header className="sb-card__header">
          <h2 id="key-heading">This archive&apos;s key</h2>
        </header>
        <ErrorText error={keys.error} />
        <p>
          On each store server, open <strong>Month close</strong> and register this public key (key <code>{keys.data?.keyId}</code>): its packages
          are then encrypted for this archive only.
        </p>
        <textarea className="sb-input" readOnly rows={4} value={keys.data?.publicKeyPem ?? ''} aria-label="This archive's public key" />
      </section>
      <section className="sb-card" aria-labelledby="sources-heading" data-testid="archive-sources">
        <header className="sb-card__header">
          <h2 id="sources-heading">Trusted store servers</h2>
        </header>
        <ErrorText error={sources.error} />
        <ul className="sb-plain-list">
          {(sources.data ?? []).map((s) => (
            <li key={s.id}>
              <strong>{s.name}</strong> (key {s.keyId}), registered {formatDateTime(s.registeredAtUtc)} by {s.registeredBy}; {s.imports} month(s) imported.{' '}
              {s.isActive ? null : <span className="sb-muted">No longer trusted.</span>}
              {manage && s.isActive ? (
                <button
                  type="button"
                  className="sb-link"
                  onClick={async () => {
                    if (!window.confirm(`Stop accepting packages from ${s.name}? Months already imported stay.`)) return;
                    await api.post(`/api/v1/archive/sources/${s.id}/revoke`);
                    setVersion((v) => v + 1);
                  }}
                >
                  Stop trusting
                </button>
              ) : null}
            </li>
          ))}
        </ul>
        {sources.data && sources.data.length === 0 ? <p className="sb-muted">No store server is trusted yet.</p> : null}
        {manage ? (
          <ActionForm
            testId="register-source-form"
            submitLabel="Trust this store server"
            onSubmit={async (data) => {
              await api.post('/api/v1/archive/sources', { name: text(data, 'name'), publicKeyPem: text(data, 'pem') });
              setVersion((v) => v + 1);
            }}
          >
            <Field label="Name" name="name" placeholder="e.g. Main store server" maxLength={100} required />
            <label className="sb-field">
              <span className="sb-field__label">The store server&apos;s public key (from its Month close screen)</span>
              <textarea className="sb-input" name="pem" rows={4} required placeholder="-----BEGIN PUBLIC KEY-----" />
            </label>
          </ActionForm>
        ) : null}
      </section>
    </>
  );
}

const roles: [code: string, name: string][] = [
  ['archive_owner', 'Owner administrator'],
  ['archive_manager', 'Archive manager'],
  ['archive_accountant', 'Accountant'],
  ['archive_auditor', 'Auditor'],
  ['archive_report_user', 'Report user'],
  ['archive_support', 'Restricted support administrator'],
];

function scopeText(g: ArchiveGrant): string {
  const parts = [g.businessName ?? 'all businesses'];
  if (g.storeName) parts.push(g.storeName);
  parts.push(g.financialYearLabel ? `FY ${g.financialYearLabel}` : 'all years');
  if (g.reports) parts.push(`reports: ${g.reports.join(', ')}`);
  return parts.join(', ');
}

/** The scope fields of a grant: business, store, financial year, reports. */
function GrantFields({ businesses }: { businesses: ArchiveBusiness[] }) {
  return (
    <>
      <label className="sb-field">
        <span className="sb-field__label">Role</span>
        <select className="sb-input" name="role" defaultValue="archive_report_user">
          {roles.map(([code, name]) => <option key={code} value={code}>{name}</option>)}
        </select>
      </label>
      <label className="sb-field">
        <span className="sb-field__label">Business</span>
        <select className="sb-input" name="business" defaultValue="">
          <option value="">All businesses</option>
          {businesses.map((b) => <option key={b.id} value={b.id}>{b.code} - {b.name}</option>)}
        </select>
      </label>
      <label className="sb-field">
        <span className="sb-field__label">Store</span>
        <select className="sb-input" name="store" defaultValue="">
          <option value="">All stores</option>
          {businesses.flatMap((b) => b.stores.map((s) => <option key={s.id} value={`${b.id}|${s.id}`}>{b.code} {s.code} - {s.name}</option>))}
        </select>
      </label>
      <Field label="Financial year (first year, e.g. 2026 for 2026-27; empty: all)" name="year" inputMode="numeric" />
      <Field label="Reports (keys separated by commas; empty: all)" name="reports" />
    </>
  );
}

function grantBody(data: FormData) {
  const store = optional(data, 'store');
  const year = optional(data, 'year');
  const reports = optional(data, 'reports');
  return {
    roleCode: text(data, 'role'),
    businessId: store ? store.split('|')[0] : optional(data, 'business'),
    storeId: store ? store.split('|')[1] : null,
    financialYear: year ? Number(year) : null,
    reports: reports ? reports.split(',').map((r) => r.trim()).filter(Boolean) : null,
  };
}

/** Archive users: their roles with business, store, financial-year and report limits; adding, limiting and stopping them. */
export function ArchiveUsersPanel() {
  const me = useArchiveMe();
  const [version, setVersion] = useState(0);
  const users = useApiData<{ id: string; username: string; displayName: string; isActive: boolean; isLockedOut: boolean; mfaEnabled: boolean; grants: ArchiveGrant[] }[]>(
    `/api/v1/archive/users?r=${version}`);
  const businesses = useApiData<ArchiveBusiness[]>('/api/v1/archive/businesses');
  const [notice, setNotice] = useState<ReactNode>(null);
  const [error, setError] = useState<unknown>(null);
  const [granting, setGranting] = useState<string | null>(null);
  const manage = can(me.data, 'archive.users.manage');
  const changed = () => setVersion((v) => v + 1);

  async function act(work: () => Promise<unknown>) {
    setError(null);
    setNotice(null);
    try {
      await work();
      changed();
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <section className="sb-card" aria-labelledby="archive-users-heading" data-testid="archive-users">
      <header className="sb-card__header">
        <h2 id="archive-users-heading">Archive users</h2>
      </header>
      <ErrorText error={users.error ?? error} />
      {notice ? <Notice tone="success">{notice}</Notice> : null}
      <ul className="sb-plain-list">
        {(users.data ?? []).map((u) => (
          <li key={u.id}>
            <strong>{u.displayName}</strong> ({u.username}){u.isActive ? '' : ' - disabled'}{u.isLockedOut ? ' - locked' : ''}{u.mfaEnabled ? ' - two-step on' : ''}
            <ul>
              {u.grants.map((g) => (
                <li key={g.id}>
                  {g.roleName}: {scopeText(g)}
                  {manage && u.id !== me.data?.userId ? (
                    <button type="button" className="sb-link" onClick={() => void act(() => api.del(`/api/v1/archive/users/${u.id}/grants/${g.id}`))}>Remove</button>
                  ) : null}
                </li>
              ))}
            </ul>
            {manage && u.id !== me.data?.userId ? (
              <div className="sb-actions">
                <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setGranting(granting === u.id ? null : u.id)}>Add a role</button>
                <button type="button" className="sb-button sb-button--secondary sb-button--small"
                  onClick={() => void act(() => api.put(`/api/v1/archive/users/${u.id}/active`, { isActive: !u.isActive }))}>
                  {u.isActive ? 'Disable' : 'Enable'}
                </button>
                {u.isLockedOut ? (
                  <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => void act(() => api.post(`/api/v1/archive/users/${u.id}/unlock`))}>Unlock</button>
                ) : null}
                <button type="button" className="sb-button sb-button--secondary sb-button--small"
                  onClick={() => void act(async () => {
                    const reset = await api.post<{ resetCode: string; expiresAtUtc: string }>(`/api/v1/archive/users/${u.id}/password-reset`);
                    setNotice(<>Reset code for {u.username}: <strong>{reset.resetCode}</strong> (until {formatDateTime(reset.expiresAtUtc)}). Shown once.</>);
                  })}>
                  Password reset code
                </button>
              </div>
            ) : null}
            {granting === u.id ? (
              <ActionForm submitLabel="Add role" onSubmit={async (data) => {
                await api.post(`/api/v1/archive/users/${u.id}/grants`, grantBody(data));
                setGranting(null);
                changed();
              }}>
                <GrantFields businesses={businesses.data ?? []} />
              </ActionForm>
            ) : null}
          </li>
        ))}
      </ul>
      {manage ? (
        <details>
          <summary>Add a user</summary>
          <ActionForm
            testId="archive-user-form"
            submitLabel="Add user"
            onSubmit={async (data) => {
              await api.post('/api/v1/archive/users', {
                username: text(data, 'username'),
                displayName: text(data, 'displayName'),
                temporaryPassword: text(data, 'password'),
                grant: grantBody(data),
              });
              setNotice('User added. They choose their own password when they first sign in.');
              changed();
            }}
          >
            <Field label="Username" name="username" autoComplete="off" required />
            <Field label="Name" name="displayName" required />
            <Field label="Temporary password" name="password" type="password" autoComplete="new-password" hint="At least 10 characters; they change it at first sign-in." required />
            <GrantFields businesses={businesses.data ?? []} />
          </ActionForm>
        </details>
      ) : null}
    </section>
  );
}
