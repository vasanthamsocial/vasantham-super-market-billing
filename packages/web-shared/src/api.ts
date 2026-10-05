// Same-origin API client. Requests go to /api/* on the web app's own origin and are forwarded to the
// ASP.NET Core API. The session cookie is HttpOnly (never visible here); the readable sb_csrf cookie is echoed
// in the X-CSRF-Token header on every state-changing request.

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

/** An API failure, carrying the RFC 7807 detail and the stable machine-readable code. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

function readCookie(name: string): string | null {
  if (typeof document === 'undefined') return null;
  const match = document.cookie.split('; ').find((part) => part.startsWith(`${name}=`));
  return match ? decodeURIComponent(match.slice(name.length + 1)) : null;
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE';

export async function apiRequest<T>(
  method: Method,
  path: string,
  body?: unknown,
  options: { signal?: AbortSignal; acceptStatuses?: number[] } = {},
): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET') {
    const csrf = readCookie('sb_csrf');
    if (csrf) headers['X-CSRF-Token'] = csrf;
  }

  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
    cache: 'no-store',
    signal: options.signal,
  });

  if (!response.ok && !(options.acceptStatuses ?? []).includes(response.status)) {
    let detail = `Request failed (HTTP ${response.status}).`;
    let code: string | null = null;
    try {
      const problem = (await response.json()) as { detail?: string; title?: string; code?: string };
      detail = problem.detail ?? problem.title ?? detail;
      code = problem.code ?? null;
    } catch {
      // Not JSON: keep the generic message.
    }
    if (response.status === 429) detail = 'Too many attempts. Wait a minute and try again.';
    throw new ApiError(detail, response.status, code);
  }

  if (response.status === 204) return undefined as T;
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** A POST that answers with a file (for example an archive package): its bytes and the name the server gave it. */
export async function postForFile(path: string, body: unknown = {}): Promise<{ blob: Blob; fileName: string }> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  const csrf = readCookie('sb_csrf');
  if (csrf) headers['X-CSRF-Token'] = csrf;
  const response = await fetch(path, { method: 'POST', headers, body: JSON.stringify(body), credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) {
    let detail = `Request failed (HTTP ${response.status}).`;
    let code: string | null = null;
    try {
      const problem = (await response.json()) as { detail?: string; title?: string; code?: string };
      detail = problem.detail ?? problem.title ?? detail;
      code = problem.code ?? null;
    } catch {
      // Not JSON: keep the generic message.
    }
    throw new ApiError(detail, response.status, code);
  }
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)?.[1];
  return { blob: await response.blob(), fileName: name ? decodeURIComponent(name) : 'download' };
}

/** Sends a file as multipart form data (field "file"), with the CSRF header. */
export async function uploadFile<T>(path: string, file: File): Promise<T> {
  const form = new FormData();
  form.append('file', file, file.name);
  const headers: Record<string, string> = { Accept: 'application/json' };
  const csrf = readCookie('sb_csrf');
  if (csrf) headers['X-CSRF-Token'] = csrf;
  const response = await fetch(path, { method: 'POST', headers, body: form, credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) {
    let detail = response.status === 413 ? 'The file is too large (at most 10 MB).' : `Upload failed (HTTP ${response.status}).`;
    let code: string | null = null;
    try {
      const problem = (await response.json()) as { detail?: string; code?: string };
      detail = problem.detail ?? detail;
      code = problem.code ?? null;
    } catch {
      // Not JSON: keep the generic message.
    }
    throw new ApiError(detail, response.status, code);
  }
  return (await response.json()) as T;
}

export const api = {
  get: <T>(path: string, signal?: AbortSignal) => apiRequest<T>('GET', path, undefined, { signal }),
  post: <T>(path: string, body: unknown = {}) => apiRequest<T>('POST', path, body),
  put: <T>(path: string, body: unknown) => apiRequest<T>('PUT', path, body),
  del: <T>(path: string) => apiRequest<T>('DELETE', path),
};

export function getSystemInfo(signal?: AbortSignal): Promise<SystemInfo> {
  return apiRequest<SystemInfo>('GET', '/api/v1/system/info', undefined, { signal });
}

/** Readiness returns 503 with a JSON body when unhealthy; that body is still useful to display. */
export function getReadiness(signal?: AbortSignal): Promise<HealthReport> {
  return apiRequest<HealthReport>('GET', '/health/ready', undefined, { signal, acceptStatuses: [503] });
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  if (error instanceof Error) return error.message;
  return 'Something went wrong.';
}
