// Talks to the counter agent on this counter PC (printer, cash drawer, scale, customer display; D-018).
// Its address and pairing token are this browser's settings, kept in localStorage: they belong to the PC, not to a user.

import type { Invoice } from '../types';

export interface AgentSettings {
  url: string;
  token: string;
  autoPrint: boolean;
}

export interface AgentStatus {
  version: string | null;
  printer: string;
  drawer: boolean;
  scale: string;
  display: string;
}

const storageKey = 'sb.counterAgent';
export const defaultAgentUrl = 'http://127.0.0.1:47800';

export function loadAgentSettings(): AgentSettings | null {
  try {
    const raw = window.localStorage.getItem(storageKey);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<AgentSettings>;
    return parsed.url && parsed.token ? { url: parsed.url, token: parsed.token, autoPrint: parsed.autoPrint ?? true } : null;
  } catch {
    return null;
  }
}

export function saveAgentSettings(settings: AgentSettings | null): void {
  try {
    if (settings) window.localStorage.setItem(storageKey, JSON.stringify(settings));
    else window.localStorage.removeItem(storageKey);
  } catch {
    // Storage blocked: the settings last until the page is closed.
  }
}

/** Calls the agent; failures are plain Errors with the agent's message (never mistaken for the store server being down). */
export async function counterAgentCall<T>(settings: AgentSettings, method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${settings.url.replace(/\/+$/, '')}${path}`, {
      method,
      headers: { 'X-Agent-Token': settings.token, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: 'no-store',
    });
  } catch {
    throw new Error('The counter agent is not running on this PC (or the browser blocked it).');
  }
  if (!response.ok) {
    let message = `Counter agent error (HTTP ${response.status}).`;
    try {
      message = ((await response.json()) as { error?: string }).error ?? message;
    } catch {
      // keep the generic message
    }
    throw new Error(message);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const counterAgent = {
  status: (s: AgentSettings) => counterAgentCall<AgentStatus>(s, 'GET', '/status'),
  printReceipt: (s: AgentSettings, invoice: Invoice, openDrawer: boolean) => counterAgentCall<void>(s, 'POST', '/receipt', { invoice, openDrawer }),
  openDrawer: (s: AgentSettings) => counterAgentCall<void>(s, 'POST', '/drawer/open'),
  readWeight: (s: AgentSettings) => counterAgentCall<{ kilograms: number; stable: boolean }>(s, 'GET', '/scale/weight'),
  display: (s: AgentSettings, line1: string, line2: string) => counterAgentCall<void>(s, 'POST', '/display', { line1, line2 }),
};
