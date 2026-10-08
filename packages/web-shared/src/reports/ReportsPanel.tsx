'use client';

import { useMemo, useState, type FormEvent } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { useStoreChoice } from '../stock/StockPanel';
import { ErrorText, formatDateTime } from '../ui';

export interface ReportColumn {
  key: string;
  label: string;
  kind: 'text' | 'date' | 'datetime' | 'count' | 'quantity' | 'money' | 'percent';
}

export interface Report {
  key: string;
  title: string;
  from: string;
  to: string;
  by: string | null;
  columns: ReportColumn[];
  rows: Record<string, unknown>[];
  totals: Record<string, unknown> | null;
  notes: string[];
  generatedAtUtc: string;
}

export interface ReportDefinition {
  key: string;
  title: string;
  group: string;
  description: string;
  groupings: string[];
  showsProfit: boolean;
}

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const quantity = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });

export const groupingLabels: Record<string, string> = {
  month: 'Month',
  day: 'Day',
  store: 'Store',
  counter: 'Counter',
  cashier: 'Cashier',
  item: 'Item',
  category: 'Category',
  brand: 'Brand',
  classification: 'Purchase type',
  supplier: 'Supplier',
  document: 'Document',
  collector: 'Collector',
  method: 'Payment method',
  route: 'Route',
  transporter: 'Lorry service',
  event: 'Event',
  user: 'User',
  detail: 'Each event',
};

function today(): string {
  return new Date().toLocaleDateString('en-CA');
}

function firstOfMonth(): string {
  const d = new Date();
  return new Date(d.getFullYear(), d.getMonth(), 1).toLocaleDateString('en-CA');
}

export function formatCell(kind: ReportColumn['kind'], value: unknown): string {
  if (value === null || value === undefined || value === '') return '';
  switch (kind) {
    case 'money':
      return money.format(Number(value));
    case 'quantity':
    case 'count':
      return quantity.format(Number(value));
    case 'percent':
      return `${money.format(Number(value))}%`;
    case 'datetime':
      return formatDateTime(String(value));
    default:
      return String(value);
  }
}

/** Reports: pick one, a date range, a store (or all, with a business-wide role) and how to group it; view it or export it. */
export function ReportsPanel() {
  const { membership } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const definitions = useApiData<ReportDefinition[]>(business ? `${business}/reports` : null);
  const { stores } = useStoreChoice();
  const [key, setKey] = useState(() =>
    typeof window === 'undefined' ? 'sales-summary' : new URLSearchParams(window.location.search).get('report') ?? 'sales-summary',
  );
  const [from, setFrom] = useState(firstOfMonth);
  const [to, setTo] = useState(today);
  const [storeId, setStoreId] = useState('');
  const [by, setBy] = useState('');
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const definition = (definitions.data ?? []).find((d) => d.key === key);
  const groups = useMemo(() => [...new Set((definitions.data ?? []).map((d) => d.group))], [definitions.data]);

  const query = new URLSearchParams({ from, to, ...(storeId ? { storeId } : {}), ...(by && definition?.groupings.includes(by) ? { by } : {}) }).toString();

  async function run(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setReport(await api.get<Report>(`${business}/reports/${key}?${query}`));
    } catch (caught) {
      setReport(null);
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  if (!business) return null;

  return (
    <>
      <section className="sb-card" aria-labelledby="reports-heading">
        <header className="sb-card__header">
          <h2 id="reports-heading">Reports</h2>
        </header>
        <ErrorText error={definitions.error} />
        <form className="sb-form" onSubmit={(e) => void run(e)} data-testid="report-form">
          <div className="sb-form-row">
            <label className="sb-field">
              <span className="sb-field__label">Report</span>
              <select className="sb-input" value={key} onChange={(e) => { setKey(e.target.value); setBy(''); setReport(null); }}>
                {groups.map((g) => (
                  <optgroup key={g} label={g}>
                    {(definitions.data ?? []).filter((d) => d.group === g).map((d) => <option key={d.key} value={d.key}>{d.title}</option>)}
                  </optgroup>
                ))}
              </select>
            </label>
            <label className="sb-field">
              <span className="sb-field__label">From</span>
              <input className="sb-input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
            </label>
            <label className="sb-field">
              <span className="sb-field__label">To</span>
              <input className="sb-input" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
            </label>
            <label className="sb-field">
              <span className="sb-field__label">Store</span>
              <select className="sb-input" value={storeId} onChange={(e) => setStoreId(e.target.value)}>
                <option value="">All stores</option>
                {stores.map((s) => <option key={s.id} value={s.id}>{s.code} - {s.name}</option>)}
              </select>
            </label>
            {definition && definition.groupings.length > 0 ? (
              <label className="sb-field">
                <span className="sb-field__label">Group by</span>
                <select className="sb-input" value={by || definition.groupings[0]} onChange={(e) => setBy(e.target.value)}>
                  {definition.groupings.map((g) => <option key={g} value={g}>{groupingLabels[g] ?? g}</option>)}
                </select>
              </label>
            ) : null}
          </div>
          {definition ? <p className="sb-muted">{definition.description}</p> : null}
          <ErrorText error={error} />
          <div className="sb-actions">
            <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Please wait...' : 'Show report'}</button>
            <a className="sb-button sb-button--secondary" href={`${business}/reports/${key}?${query}&format=csv`} download>Export CSV</a>
          </div>
        </form>
      </section>
      {report ? <ReportView report={report} /> : null}
    </>
  );
}

export function ReportView({ report }: { report: Report }) {
  const numeric = (kind: string) => kind !== 'text' && kind !== 'date' && kind !== 'datetime';
  return (
    <section className="sb-card" aria-labelledby="report-heading" data-testid="report">
      <header className="sb-card__header">
        <h2 id="report-heading">{report.title}</h2>
        <span className="sb-muted">
          {report.from} to {report.to}
          {report.by ? `, by ${groupingLabels[report.by] ?? report.by}` : ''}; prepared {formatDateTime(report.generatedAtUtc)}
        </span>
      </header>
      <div className="sb-table-scroll">
        <table className="sb-table" data-testid="report-table">
          <thead>
            <tr>
              {report.columns.map((c) => <th key={c.key} className={numeric(c.kind) ? 'sb-num' : undefined}>{c.label}</th>)}
            </tr>
          </thead>
          <tbody>
            {report.rows.map((row, i) => (
              <tr key={i}>
                {report.columns.map((c) => <td key={c.key} className={numeric(c.kind) ? 'sb-num' : undefined}>{formatCell(c.kind, row[c.key])}</td>)}
              </tr>
            ))}
          </tbody>
          {report.totals ? (
            <tfoot>
              <tr data-testid="report-totals">
                {report.columns.map((c) => (
                  <th key={c.key} className={numeric(c.kind) ? 'sb-num' : undefined}>{formatCell(c.kind, report.totals![c.key])}</th>
                ))}
              </tr>
            </tfoot>
          ) : null}
        </table>
      </div>
      {report.rows.length === 0 ? <p className="sb-muted">Nothing in this period.</p> : null}
      {report.notes.map((n) => <p key={n} className="sb-muted">{n}</p>)}
    </section>
  );
}
