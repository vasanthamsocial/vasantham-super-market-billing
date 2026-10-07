import { getApps, initializeApp } from 'firebase-admin/app';
import { getFirestore, type DocumentData, type Firestore, type Transaction } from 'firebase-admin/firestore';

if (getApps().length === 0) initializeApp();

/** Firestore, reached only from the backend (rules deny all direct access). */
export const db: Firestore = getFirestore();

/** Collections. Records reference each other by id; every business record carries businessId. */
export const C = {
  installation: 'installation',
  tenants: 'tenants',
  businesses: 'businesses',
  stores: 'stores',
  users: 'users',
  sessions: 'sessions',
  roleAssignments: 'roleAssignments',
  passwordResetTokens: 'passwordResetTokens',
  mfaRecoveryCodes: 'mfaRecoveryCodes',
  approvalRequests: 'approvalRequests',
  auditEvents: 'auditEvents',
  taxRegistrations: 'taxRegistrations',
  units: 'units',
  inventorySettings: 'inventorySettings',
  purchaseSettings: 'purchaseSettings',
  /** Unique keys (usernames, business codes, store codes...): one document per taken value. */
  uniques: 'uniques',
} as const;

export const InstallationId = 'singleton';

export type Doc = DocumentData;

export function doc(collection: string, id: string) {
  return db.collection(collection).doc(id);
}

/** Runs work in a Firestore transaction (all reads first, then writes; retried on contention). */
export function inTransaction<T>(work: (tx: Transaction) => Promise<T>): Promise<T> {
  return db.runTransaction(work);
}

export async function getAll<T = Doc>(collection: string, ids: readonly string[]): Promise<Map<string, T>> {
  const unique = [...new Set(ids)].filter(Boolean);
  const result = new Map<string, T>();
  if (unique.length === 0) return result;
  const snaps = await db.getAll(...unique.map((id) => doc(collection, id)));
  for (const snap of snaps) if (snap.exists) result.set(snap.id, snap.data() as T);
  return result;
}

/** A unique-key document id, e.g. unique('username', 'priya'). */
export function uniqueKey(kind: string, ...parts: string[]): string {
  return [kind, ...parts].join(':').replace(/\//g, '_');
}
