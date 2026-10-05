'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { classifyFreshness, type Freshness } from '../freshness';
import { FreshnessBadge } from '../SystemStatus';
import { useStoreChoice } from '../stock/StockPanel';
import { formatDateTime } from '../ui';
import { formatCell, type Report } from './ReportsPanel';

interface DashboardMetric {
  label: string;
  kind: 'money' | 'count' | 'quantity' | 'percent' | 'text' | 'datetime';
  value: number | null;
  text: string | null;
  tone: 'good' | 'warning' | 'bad' | null;
}

interface DashboardSection {
  key: string;
  title: string;
  status: 'ok' | 'unavailable';
  message: string | null;
  metrics: DashboardMetric[];
  table: Report | null;
  reportKey: string | null;
}

interface Dashboard {
  businessId: string;
  storeId: string | null;
  today: string;
  generatedAtUtc: string;
  sections: DashboardSection[];
}

const RefreshMs = 60_000;

function cacheKey(businessId: string, storeId: string) {
  return `sb.dashboard.${businessId}.${storeId || 'all'}`;
}

function readCache(key: string): { data: Dashboard; at: number } | null {
  try {
    const raw = window.localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as { data: Dashboard; at: number }) : null;
  } catch {
    return null;
  }
}

function writeCache(key: string, data: Dashboard) {
  try {
    window.localStorage.setItem(key, JSON.stringify({ data, at: Date.now() }));
  } catch {
    // Storage may be full or blocked; the dashboard still works without it.
  }
}

function metricText(m: DashboardMetric): string {
  if (m.kind === 'text') return m.text ?? '';
  if (m.kind === 'datetime') return m.text ? formatDateTime(m.text) : '-';
  if (m.value === null) return '-';
  return formatCell(m.kind === 'count' ? 'count' : m.kind, m.value);
}

/**
 * The Owner Dashboard: today and the month so far, refreshed every minute. Every figure is the same as its report.
 * It says how fresh it is: live, delayed (the last refresh failed or is late), cached (the server cannot be reached;
 * the last copy saved on this computer is shown) or unavailable.
 */
export function DashboardPanel({ reportsHref = '/reports' }: { reportsHref?: string }) {
  const { membership } = useAuth();
  const { stores } = useStoreChoice();
  const [storeId, setStoreId] = useState('');
  const [data, setData] = useState<Dashboard | null>(null);
  const [lastSuccessAt, setLastSuccessAt] = useState<number | null>(null);
  const [failed, setFailed] = useState(false);
  const [fromCache, setFromCache] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const businessId = membership?.businessId;
  const running = useRef(false);
  const shown = useRef<Dashboard | null>(null);
  shown.current = data;

  const load = useCallback(async () => {
    if (!businessId || running.current) return;
    running.current = true;
    const key = cacheKey(businessId, storeId);
    try {
      const fresh = await api.get<Dashboard>(`/api/v1/businesses/${businessId}/dashboard${storeId ? `?storeId=${storeId}` : ''}`);
      setData(fresh);
      setLastSuccessAt(Date.now());
      setFailed(false);
      setFromCache(false);
      writeCache(key, fresh);
    } catch {
      setFailed(true);
      // Keep what is on screen for this store; otherwise fall back to the copy saved on this computer.
      if (!shown.current || shown.current.storeId !== (storeId || null)) {
        const cached = readCache(key);
        setFromCache(!!cached);
        setData(cached?.data ?? null);
      }
    } finally {
      running.current = false;
    }
  }, [businessId, storeId]);

  useEffect(() => {
    if (!businessId) return;
    // Show the last copy at once while the first refresh runs.
    const cached = readCache(cacheKey(businessId, storeId));
    setData(cached?.data ?? null);
    setFromCache(!!cached);
    setLastSuccessAt(null);
    void load();
    const timer = window.setInterval(() => void load(), RefreshMs);
    const clock = window.setInterval(() => setNow(Date.now()), 15_000);
    return () => {
      window.clearInterval(timer);
      window.clearInterval(clock);
    };
  }, [businessId, storeId, load]);

  const freshness: Freshness = classifyFreshness({ lastSuccessAt, lastAttemptFailed: failed, now, refreshIntervalMs: RefreshMs, fromCache: fromCache && lastSuccessAt === null });

  return (
    <section aria-labelledby="dashboard-heading" data-testid="dashboard">
      <header className="sb-card__header">
        <h2 id="dashboard-heading">Today{data ? `, ${data.today}` : ''}</h2>
        <div className="sb-actions">
          <select className="sb-input" aria-label="Dashboard store" value={storeId} onChange={(e) => setStoreId(e.target.value)}>
            <option value="">All stores</option>
            {stores.map((s) => <option key={s.id} value={s.id}>{s.code} - {s.name}</option>)}
          </select>
          <FreshnessBadge freshness={freshness} />
          {data ? <span className="sb-muted" data-testid="dashboard-as-of">As of {formatDateTime(data.generatedAtUtc)}</span> : null}
          <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => void load()}>Refresh</button>
        </div>
      </header>
      {freshness === 'cached' ? (
        <p className="sb-notice sb-notice--warning" role="status">The server cannot be reached. These are the figures saved on this computer at the time shown.</p>
      ) : null}
      {!data && freshness === 'unavailable' ? <p className="sb-error" role="alert">The dashboard is unavailable: the server cannot be reached.</p> : null}
      <div className="sb-dashboard">
        {(data?.sections ?? []).map((s) => (
          <article key={s.key} className="sb-card sb-dashboard__tile" data-testid={`tile-${s.key}`} aria-labelledby={`tile-${s.key}-title`}>
            <header className="sb-card__header">
              <h3 id={`tile-${s.key}-title`}>{s.title}</h3>
              {s.status === 'unavailable' ? <span className="sb-badge sb-badge--unavailable">Unavailable</span> : null}
            </header>
            {s.message ? <p className="sb-muted">{s.message}</p> : null}
            {s.metrics.length > 0 ? (
              <dl className="sb-dashboard__metrics">
                {s.metrics.map((m) => (
                  <div key={m.label} className={m.tone ? `sb-tone--${m.tone}` : undefined}>
                    <dt>{m.label}</dt>
                    <dd>{metricText(m)}</dd>
                  </div>
                ))}
              </dl>
            ) : null}
            {s.table && s.table.rows.length > 0 ? (
              <div className="sb-table-scroll">
                <table className="sb-table sb-table--compact">
                  <thead>
                    <tr>{s.table.columns.map((c) => <th key={c.key}>{c.label}</th>)}</tr>
                  </thead>
                  <tbody>
                    {s.table.rows.map((row, i) => (
                      <tr key={i}>{s.table!.columns.map((c) => <td key={c.key}>{formatCell(c.kind, row[c.key])}</td>)}</tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : s.table ? <p className="sb-muted">Nothing yet.</p> : null}
            {s.reportKey ? <a className="sb-link" href={`${reportsHref}?report=${s.reportKey}`}>Full report</a> : null}
          </article>
        ))}
      </div>
    </section>
  );
}
