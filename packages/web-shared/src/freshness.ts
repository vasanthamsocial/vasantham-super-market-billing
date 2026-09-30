/**
 * Every screen that shows server data must say how fresh it is.
 *  - live:        fetched successfully within the expected refresh interval
 *  - delayed:     the last successful fetch is older than expected; shown data may be stale
 *  - cached:      shown from a local cache while the server is unreachable (offline-capable screens)
 *  - unavailable: no data could be obtained
 */
export type Freshness = 'live' | 'delayed' | 'cached' | 'unavailable';

export function classifyFreshness(options: {
  lastSuccessAt: number | null;
  lastAttemptFailed: boolean;
  now: number;
  refreshIntervalMs: number;
  fromCache?: boolean;
}): Freshness {
  const { lastSuccessAt, lastAttemptFailed, now, refreshIntervalMs, fromCache = false } = options;
  if (lastSuccessAt === null) {
    return fromCache ? 'cached' : 'unavailable';
  }
  if (fromCache) {
    return 'cached';
  }
  const age = now - lastSuccessAt;
  if (!lastAttemptFailed && age <= refreshIntervalMs * 2) {
    return 'live';
  }
  return 'delayed';
}

export const freshnessLabel: Record<Freshness, string> = {
  live: 'Live',
  delayed: 'Delayed',
  cached: 'Cached',
  unavailable: 'Unavailable',
};
