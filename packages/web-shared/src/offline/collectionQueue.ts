import { api, ApiError } from '../api';
import type { Me } from '../types';
import { get, list, put, remove, supported } from './secureStore';

/** The phone's enrolment, kept (encrypted) for working offline. */
export interface OfflineDevice {
  deviceId: string;
  businessId: string;
  collectorUserId: string;
  name: string;
  offlineLimit: number;
  maxOfflineHours: number;
  lastSequence: number;
}

/** A collection recorded without the server: provisional until it is synchronised. */
export interface QueuedCollection {
  id: string;
  sequence: number;
  debtorId: string;
  debtorName: string;
  method: string;
  amount: number;
  reference: string | null;
  bankName: string | null;
  chequeDate: string | null;
  recordedAtUtc: string;
}

export interface SyncResult {
  id: string;
  sequence: number;
  status: 'ACCEPTED' | 'QUARANTINED' | 'DUPLICATE' | 'REJECTED' | 'NOT_PROCESSED';
  reason: string | null;
  receiptNumber: string | null;
  balanceAfter: number | null;
}

/** What became of a collection after synchronisation, kept on the phone for the collector to see. */
export interface SyncedCollection extends QueuedCollection {
  status: SyncResult['status'];
  reason: string | null;
  receiptNumber: string | null;
  balanceAfter: number | null;
  syncedAtUtc: string;
}

const DeviceKey = 'device';
const MeKey = 'me';
const NextKey = 'next-sequence';
const queueKey = (sequence: number) => `queue:${String(sequence).padStart(12, '0')}`;
const historyKey = (sequence: number) => `history:${String(sequence).padStart(12, '0')}`;

/** The server could not be reached (no signal, or the store server is down): work offline. */
export function unreachable(error: unknown): boolean {
  return !(error instanceof ApiError) || error.status === 0 || error.status === 502 || error.status === 503 || error.status === 504;
}

export const offlineSupported = supported;

/** A collection's id, made on the phone: a UUIDv7 (time-ordered) like the server's ids. */
export function newCollectionId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  const ms = BigInt(Date.now());
  for (let i = 0; i < 6; i++) bytes[i] = Number((ms >> BigInt(8 * (5 - i))) & 0xffn);
  bytes[6] = (bytes[6]! & 0x0f) | 0x70;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * The receipt's idempotency key for a collection id: the same whether it is sent at once or synchronised later, so a
 * collection whose answer was lost (and was then queued) is not recorded twice.
 */
export function collectionKey(id: string): string {
  return `offline-${id.replaceAll('-', '')}`;
}

/** The enrolment from the server when online (kept for later); the kept one when not. Null when this phone is not enrolled. */
export async function loadDevice(): Promise<{ device: OfflineDevice | null; online: boolean }> {
  try {
    const device = (await api.get<OfflineDevice | undefined>('/api/v1/collections/device')) ?? null;
    if (device) {
      await put(DeviceKey, device);
      const next = (await get<number>(NextKey)) ?? 1;
      // Never go back below what the server has already received.
      if (next <= device.lastSequence) await put(NextKey, device.lastSequence + 1);
    } else {
      await remove(DeviceKey);
    }
    return { device, online: true };
  } catch (error) {
    if (!unreachable(error)) throw error;
    return { device: await get<OfflineDevice>(DeviceKey), online: false };
  }
}

/** Keeps who is signed in on an enrolled phone, so the app opens offline for them (and only them). */
export async function rememberCollector(me: Me, device: OfflineDevice | null): Promise<void> {
  if (device && device.collectorUserId === me.userId) {
    await put(MeKey, { me, savedAtUtc: new Date().toISOString() });
  }
}

/** The collector who last signed in on this phone, while the offline time allows; null otherwise. */
export async function offlineCollector(): Promise<Me | null> {
  if (!supported()) return null;
  const [saved, device] = await Promise.all([get<{ me: Me; savedAtUtc: string }>(MeKey), get<OfflineDevice>(DeviceKey)]);
  if (!saved || !device || saved.me.userId !== device.collectorUserId) return null;
  const age = Date.now() - Date.parse(saved.savedAtUtc);
  return age <= device.maxOfflineHours * 3_600_000 ? saved.me : null;
}

/** Signing out forgets who was signed in (the queued collections stay until they are synchronised). */
export async function forgetCollector(): Promise<void> {
  if (supported()) await remove(MeKey);
}

export async function cacheDay<T>(day: T): Promise<void> {
  await put('day', { day, savedAtUtc: new Date().toISOString() });
}

export async function cachedDay<T>(): Promise<{ day: T; savedAtUtc: string } | null> {
  return get<{ day: T; savedAtUtc: string }>('day');
}

export async function pending(): Promise<QueuedCollection[]> {
  return (await list<QueuedCollection>('queue:')).map((r) => r.value);
}

export async function history(): Promise<SyncedCollection[]> {
  return (await list<SyncedCollection>('history:')).map((r) => r.value).reverse();
}

/** Whether another offline collection of this amount is allowed now, and why not. */
export function check(device: OfflineDevice, queued: QueuedCollection[], amount: number, now = Date.now()): string | null {
  const held = queued.reduce((sum, q) => sum + q.amount, 0);
  if (held + amount > device.offlineLimit) {
    return `This phone may hold at most Rs. ${device.offlineLimit.toFixed(2)} without signal (Rs. ${held.toFixed(2)} is waiting). Synchronise first.`;
  }
  const oldest = queued[0];
  if (oldest && now - Date.parse(oldest.recordedAtUtc) > device.maxOfflineHours * 3_600_000) {
    return `Collections have waited more than ${device.maxOfflineHours} hours. Synchronise before collecting more.`;
  }
  return null;
}

/** Records a collection on the phone (encrypted), with the next sequence number. */
export async function enqueue(device: OfflineDevice, collection: Omit<QueuedCollection, 'sequence' | 'recordedAtUtc'>): Promise<QueuedCollection> {
  const queued = await pending();
  const refusal = check(device, queued, collection.amount);
  if (refusal) throw new Error(refusal);
  const sequence = Math.max((await get<number>(NextKey)) ?? 1, device.lastSequence + 1, (queued.at(-1)?.sequence ?? 0) + 1);
  const item: QueuedCollection = { ...collection, sequence, recordedAtUtc: new Date().toISOString() };
  await put(queueKey(sequence), item);
  await put(NextKey, sequence + 1);
  return item;
}

/**
 * Sends everything waiting, in order. What the server posted or quarantined (or had already) leaves the queue and is
 * kept in the history; what it did not process stays for the next attempt.
 */
export async function synchronise(): Promise<SyncResult[]> {
  const queued = await pending();
  if (queued.length === 0) return [];
  const response = await api.post<{ results: SyncResult[]; lastSequence: number }>('/api/v1/collections/device/sync', {
    items: queued.map((q) => ({
      id: q.id,
      sequence: q.sequence,
      debtorId: q.debtorId,
      method: q.method,
      amount: q.amount,
      recordedAtUtc: q.recordedAtUtc,
      reference: q.reference,
      bankName: q.bankName,
      chequeDate: q.chequeDate,
    })),
  });
  const now = new Date().toISOString();
  for (const result of response.results) {
    const item = queued.find((q) => q.id === result.id);
    if (!item || result.status === 'NOT_PROCESSED') continue;
    await put(historyKey(item.sequence), { ...item, ...result, syncedAtUtc: now } satisfies SyncedCollection);
    await remove(queueKey(item.sequence));
  }
  const device = await get<OfflineDevice>(DeviceKey);
  if (device) await put(DeviceKey, { ...device, lastSequence: response.lastSequence });
  return response.results;
}
