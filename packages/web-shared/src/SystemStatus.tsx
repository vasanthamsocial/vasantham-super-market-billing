'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { getReadiness, getSystemInfo, type HealthReport, type SystemInfo } from './api';
import { classifyFreshness, freshnessLabel, type Freshness } from './freshness';

const REFRESH_INTERVAL_MS = 15_000;

interface StatusState {
  info: SystemInfo | null;
  readiness: HealthReport | null;
  lastSuccessAt: number | null;
  lastAttemptFailed: boolean;
  error: string | null;
}

const initialState: StatusState = {
  info: null,
  readiness: null,
  lastSuccessAt: null,
  lastAttemptFailed: false,
  error: null,
};

export function FreshnessBadge({ freshness }: { freshness: Freshness }) {
  return (
    <span className={`sb-badge sb-badge--${freshness}`} data-testid="freshness-badge" data-freshness={freshness}>
      {freshnessLabel[freshness]}
    </span>
  );
}

/** Polls the API through the same-origin proxy and shows API, database and schema health. */
export function SystemStatus({ onInfo }: { onInfo?: (info: SystemInfo | null) => void }) {
  const [state, setState] = useState<StatusState>(initialState);
  const [now, setNow] = useState(() => Date.now());
  const onInfoRef = useRef(onInfo);
  useEffect(() => {
    onInfoRef.current = onInfo;
  }, [onInfo]);

  const refresh = useCallback(async (signal: AbortSignal) => {
    try {
      const [info, readiness] = await Promise.all([getSystemInfo(signal), getReadiness(signal)]);
      setState({ info, readiness, lastSuccessAt: Date.now(), lastAttemptFailed: false, error: null });
      onInfoRef.current?.(info);
    } catch (error) {
      if (signal.aborted) return;
      setState((previous) => ({
        ...previous,
        lastAttemptFailed: true,
        error: error instanceof Error ? error.message : 'Unknown error',
      }));
      onInfoRef.current?.(null);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    const poll = window.setInterval(() => void refresh(controller.signal), REFRESH_INTERVAL_MS);
    const tick = window.setInterval(() => setNow(Date.now()), 1_000);
    return () => {
      controller.abort();
      window.clearInterval(poll);
      window.clearInterval(tick);
    };
  }, [refresh]);

  const freshness = classifyFreshness({
    lastSuccessAt: state.lastSuccessAt,
    lastAttemptFailed: state.lastAttemptFailed,
    now,
    refreshIntervalMs: REFRESH_INTERVAL_MS,
  });

  const checks = state.readiness?.checks ?? [];

  return (
    <section className="sb-card" aria-labelledby="system-status-heading" data-testid="system-status">
      <header className="sb-card__header">
        <h2 id="system-status-heading">System status</h2>
        <FreshnessBadge freshness={freshness} />
      </header>
      <dl className="sb-status-grid">
        <dt>API</dt>
        <dd data-testid="api-status">{state.info ? 'Connected' : state.lastAttemptFailed ? 'Not reachable' : 'Checking...'}</dd>
        <dt>Version</dt>
        <dd>{state.info ? `${state.info.version} (${state.info.environment})` : '-'}</dd>
        <dt>Readiness</dt>
        <dd data-testid="readiness-status">{state.readiness?.status ?? '-'}</dd>
        {checks.map((check) => (
          <StatusRow key={check.name} name={check.name} status={check.status} description={check.description} />
        ))}
        <dt>Last updated</dt>
        <dd>{state.lastSuccessAt ? new Date(state.lastSuccessAt).toLocaleTimeString() : 'Never'}</dd>
      </dl>
      {state.error && freshness !== 'live' ? (
        <p className="sb-muted" role="status">
          Last attempt failed: {state.error}
        </p>
      ) : null}
    </section>
  );
}

function StatusRow({ name, status, description }: { name: string; status: string; description: string | null }) {
  const label = name.charAt(0).toUpperCase() + name.slice(1);
  return (
    <>
      <dt>{label}</dt>
      <dd data-testid={`check-${name}`} title={description ?? undefined}>
        {status}
      </dd>
    </>
  );
}
