'use client';

import { useMemo, useState, type FormEvent } from 'react';
import { api } from '../api';
import { useApiData } from '../admin/useApiData';
import { groupingLabels, ReportView, type Report, type ReportDefinition } from '../reports/ReportsPanel';
import { ErrorText } from '../ui';

// Historical reports of the Owner Archive (spec section 22, D-043): read-only, from the archived months.

interface ArchiveBusiness {
  id: string;
  code: string;
  name: string;
  stores: { id: string; code: string; name: string }[];
  months: string[];
}

/** The last day of a month given as YYYY-MM. */
function endOf(month: string): string {
  const y = Number(month.slice(0, 4));
  const m = Number(month.slice(5, 7));
  return `${month}-${String(new Date(y, m, 0).getDate()).padStart(2, '0')}`;
}

/** The financial year (April to March) of a month, as its first year. */
function yearOf(month: string): number {
  const y = Number(month.slice(0, 4));
  const m = Number(month.slice(5, 7));
  return m >= 4 ? y : y - 1;
}

const yearLabel = (y: number) => `${y}-${String((y + 1) % 100).padStart(2, '0')}`;

export function ArchiveReportsPanel() {
  const businesses = useApiData<ArchiveBusiness[]>('/api/v1/archive/businesses');
  const [businessId, setBusinessId] = useState('');
  const business = (businesses.data ?? []).find((b) => b.id === businessId) ?? businesses.data?.[0];
  const base = business ? `/api/v1/archive/businesses/${business.id}/reports` : null;
  const definitions = useApiData<ReportDefinition[]>(base);
  const [key, setKey] = useState('sales-summary');
  const [period, setPeriod] = useState('');
  const [customFrom, setCustomFrom] = useState('');
  const [customTo, setCustomTo] = useState('');
  const [storeId, setStoreId] = useState('');
  const [by, setBy] = useState('');
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const months = useMemo(() => [...(business?.months ?? [])].reverse(), [business]);
  const years = useMemo(() => [...new Set(months.map(yearOf))], [months]);
  const chosen = period || (months[0] ? `m:${months[0]}` : '');
  const [from, to] = chosen.startsWith('m:')
    ? [`${chosen.slice(2)}-01`, endOf(chosen.slice(2))]
    : chosen.startsWith('y:')
      ? [`${chosen.slice(2)}-04-01`, `${Number(chosen.slice(2)) + 1}-03-31`]
      : [customFrom, customTo];
  const available = definitions.data ?? [];
  const definition = available.find((d) => d.key === key) ?? available[0];
  const groups = [...new Set(available.map((d) => d.group))];
  const query = new URLSearchParams({
    from,
    to,
    ...(storeId ? { storeId } : {}),
    ...(by && definition?.groupings.includes(by) ? { by } : {}),
  }).toString();

  async function run(event: FormEvent) {
    event.preventDefault();
    if (!base || !definition) return;
    setBusy(true);
    setError(null);
    try {
      setReport(await api.get<Report>(`${base}/${definition.key}?${query}`));
    } catch (caught) {
      setReport(null);
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <section className="sb-card" aria-labelledby="archive-reports-heading">
        <header className="sb-card__header">
          <h2 id="archive-reports-heading">Historical reports</h2>
        </header>
        <ErrorText error={businesses.error ?? definitions.error} />
        {businesses.data && businesses.data.length === 0 ? <p className="sb-muted">No month has been archived yet.</p> : null}
        {business && definition ? (
          <form className="sb-form" onSubmit={(e) => void run(e)} data-testid="archive-report-form">
            <div className="sb-form-row">
              <label className="sb-field">
                <span className="sb-field__label">Business</span>
                <select className="sb-input" value={business.id} onChange={(e) => { setBusinessId(e.target.value); setPeriod(''); setStoreId(''); setReport(null); }}>
                  {(businesses.data ?? []).map((b) => <option key={b.id} value={b.id}>{b.code} - {b.name}</option>)}
                </select>
              </label>
              <label className="sb-field">
                <span className="sb-field__label">Report</span>
                <select className="sb-input" value={definition.key} onChange={(e) => { setKey(e.target.value); setBy(''); setReport(null); }}>
                  {groups.map((g) => (
                    <optgroup key={g} label={g}>
                      {available.filter((d) => d.group === g).map((d) => <option key={d.key} value={d.key}>{d.title}</option>)}
                    </optgroup>
                  ))}
                </select>
              </label>
              <label className="sb-field">
                <span className="sb-field__label">Period</span>
                <select className="sb-input" value={chosen} onChange={(e) => setPeriod(e.target.value)}>
                  <optgroup label="Archived months">
                    {months.map((m) => <option key={m} value={`m:${m}`}>{m}</option>)}
                  </optgroup>
                  <optgroup label="Financial years">
                    {years.map((y) => <option key={y} value={`y:${y}`}>FY {yearLabel(y)}</option>)}
                  </optgroup>
                  <option value="custom">Other dates...</option>
                </select>
              </label>
              {chosen === 'custom' ? (
                <>
                  <label className="sb-field">
                    <span className="sb-field__label">From</span>
                    <input className="sb-input" type="date" value={customFrom} onChange={(e) => setCustomFrom(e.target.value)} required />
                  </label>
                  <label className="sb-field">
                    <span className="sb-field__label">To</span>
                    <input className="sb-input" type="date" value={customTo} onChange={(e) => setCustomTo(e.target.value)} required />
                  </label>
                </>
              ) : null}
              <label className="sb-field">
                <span className="sb-field__label">Store</span>
                <select className="sb-input" value={storeId} onChange={(e) => setStoreId(e.target.value)}>
                  <option value="">All stores</option>
                  {business.stores.map((s) => <option key={s.id} value={s.id}>{s.code} - {s.name}</option>)}
                </select>
              </label>
              {definition.groupings.length > 0 ? (
                <label className="sb-field">
                  <span className="sb-field__label">Group by</span>
                  <select className="sb-input" value={by || definition.groupings[0]} onChange={(e) => setBy(e.target.value)}>
                    {definition.groupings.map((g) => <option key={g} value={g}>{groupingLabels[g] ?? g}</option>)}
                  </select>
                </label>
              ) : null}
            </div>
            <p className="sb-muted">{definition.description} Read-only, as the store server recorded it.</p>
            <ErrorText error={error} />
            <div className="sb-actions">
              <button className="sb-button" type="submit" disabled={busy || !from || !to}>{busy ? 'Please wait...' : 'Show report'}</button>
              <a className="sb-button sb-button--secondary" href={`${base}/${definition.key}?${query}&format=csv`} download>Export CSV</a>
            </div>
          </form>
        ) : null}
      </section>
      {report ? <ReportView report={report} /> : null}
    </>
  );
}
