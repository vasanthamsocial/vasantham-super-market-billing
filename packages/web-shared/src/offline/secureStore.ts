/**
 * Encrypted storage on this device (spec section 16: "encrypted local queue"). Records live in IndexedDB, each
 * encrypted with AES-GCM (256-bit, a fresh 96-bit IV per record) under a key generated on this device as
 * non-extractable: scripts can use it but never read it out, and it never leaves the browser profile. Clearing the
 * browser's site data destroys the key, and with it everything stored here.
 */

const DbName = 'sb-offline';
const Version = 1;
const KeyId = 'device-key';

export interface Sealed {
  iv: Uint8Array<ArrayBuffer>;
  data: ArrayBuffer;
}

function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DbName, Version);
    request.onupgradeneeded = () => {
      const db = request.result;
      for (const store of ['keys', 'records']) {
        if (!db.objectStoreNames.contains(store)) db.createObjectStore(store);
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function run<T>(store: string, mode: IDBTransactionMode, work: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await open();
  try {
    return await new Promise<T>((resolve, reject) => {
      const transaction = db.transaction(store, mode);
      const request = work(transaction.objectStore(store));
      transaction.oncomplete = () => resolve(request.result);
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error);
    });
  } finally {
    db.close();
  }
}

let keyPromise: Promise<CryptoKey> | null = null;

/** This device's key, made on first use; non-extractable. */
function deviceKey(): Promise<CryptoKey> {
  keyPromise ??= (async () => {
    const existing = await run<CryptoKey | undefined>('keys', 'readonly', (s) => s.get(KeyId) as IDBRequest<CryptoKey | undefined>);
    if (existing) return existing;
    const key = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    await run('keys', 'readwrite', (s) => s.put(key, KeyId));
    return key;
  })();
  return keyPromise;
}

export async function seal(value: unknown): Promise<Sealed> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, await deviceKey(), new TextEncoder().encode(JSON.stringify(value)));
  return { iv, data };
}

export async function unseal<T>(sealed: Sealed): Promise<T> {
  const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: sealed.iv }, await deviceKey(), sealed.data);
  return JSON.parse(new TextDecoder().decode(plain)) as T;
}

export async function put(key: string, value: unknown): Promise<void> {
  const sealed = await seal(value);
  await run('records', 'readwrite', (s) => s.put(sealed, key));
}

export async function get<T>(key: string): Promise<T | null> {
  const sealed = await run<Sealed | undefined>('records', 'readonly', (s) => s.get(key) as IDBRequest<Sealed | undefined>);
  return sealed ? unseal<T>(sealed) : null;
}

export async function remove(key: string): Promise<void> {
  await run('records', 'readwrite', (s) => s.delete(key));
}

/** All records whose key starts with the prefix, decrypted, in key order. */
export async function list<T>(prefix: string): Promise<{ key: string; value: T }[]> {
  const range = IDBKeyRange.bound(prefix, `${prefix}￿`);
  const keys = (await run<IDBValidKey[]>('records', 'readonly', (s) => s.getAllKeys(range))) as string[];
  const values = await run<Sealed[]>('records', 'readonly', (s) => s.getAll(range) as IDBRequest<Sealed[]>);
  return Promise.all(keys.map(async (key, i) => ({ key, value: await unseal<T>(values[i]!) })));
}

/** Whether this browser can keep an encrypted queue at all (IndexedDB and WebCrypto in a secure context). */
export function supported(): boolean {
  return typeof indexedDB !== 'undefined' && typeof crypto !== 'undefined' && !!crypto.subtle;
}
