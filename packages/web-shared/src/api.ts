// Minimal same-origin API client. Requests go to /api/* on the web app's own origin and are
// forwarded to the ASP.NET Core API by Next.js rewrites.

export interface SystemInfo {
  application: string;
  version: string;
  environment: string;
  serverTimeUtc: string;
  archiveWebEnabled: boolean;
}

export interface HealthCheckEntry {
  name: string;
  status: 'Healthy' | 'Degraded' | 'Unhealthy';
  description: string | null;
  durationMs: number;
}

export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy';
  totalDurationMs: number;
  checkedAtUtc: string;
  checks: HealthCheckEntry[];
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function getJson<T>(path: string, signal?: AbortSignal, acceptStatuses: number[] = []): Promise<T> {
  const response = await fetch(path, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    credentials: 'same-origin',
    cache: 'no-store',
    signal,
  });
  if (!response.ok && !acceptStatuses.includes(response.status)) {
    throw new ApiError(`GET ${path} failed with HTTP ${response.status}`, response.status);
  }
  return (await response.json()) as T;
}

export function getSystemInfo(signal?: AbortSignal): Promise<SystemInfo> {
  return getJson<SystemInfo>('/api/v1/system/info', signal);
}

/** Readiness returns 503 with a JSON body when unhealthy; that body is still useful to display. */
export function getReadiness(signal?: AbortSignal): Promise<HealthReport> {
  return getJson<HealthReport>('/health/ready', signal, [503]);
}
