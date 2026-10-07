import { createCipheriv, createDecipheriv, createHash, pbkdf2, randomBytes, randomInt, timingSafeEqual } from 'node:crypto';
import { promisify } from 'node:util';

const pbkdf2Async = promisify(pbkdf2);

// Passwords: PBKDF2-HMAC-SHA512, 210,000 iterations (OWASP), 16-byte salt, 32-byte key. Stored as
// "pbkdf2-sha512$<iterations>$<salt b64>$<key b64>".
export const PasswordIterations = Number(process.env.SB_PASSWORD_ITERATIONS ?? 210_000);

export async function hashPassword(password: string): Promise<string> {
  const salt = randomBytes(16);
  const key = await pbkdf2Async(password, salt, PasswordIterations, 32, 'sha512');
  return `pbkdf2-sha512$${PasswordIterations}$${salt.toString('base64')}$${key.toString('base64')}`;
}

/** Whether the password matches, and whether the stored hash should be upgraded (fewer iterations than now). */
export async function verifyPassword(stored: string, password: string): Promise<{ valid: boolean; needsRehash: boolean }> {
  const [scheme, iterations, salt, key] = stored.split('$');
  if (scheme !== 'pbkdf2-sha512' || !iterations || !salt || !key) return { valid: false, needsRehash: false };
  const expected = Buffer.from(key, 'base64');
  const actual = await pbkdf2Async(password, Buffer.from(salt, 'base64'), Number(iterations), expected.length, 'sha512');
  const valid = actual.length === expected.length && timingSafeEqual(actual, expected);
  return { valid, needsRehash: valid && Number(iterations) < PasswordIterations };
}

let dummyHash: Promise<string> | null = null;

/** Spends the same time as checking a real password, so response time does not reveal which usernames exist. */
export async function verifyDummy(password: string): Promise<void> {
  dummyHash ??= hashPassword(newToken());
  await verifyPassword(await dummyHash, password);
}

// Random bearer secrets (session, CSRF, reset, recovery codes) and their stored SHA-256 hashes.

const codeAlphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';

/** 256-bit URL-safe random token. */
export function newToken(): string {
  return randomBytes(32).toString('base64url');
}

/** Human-typeable code such as K7QD-3MXA-9PLT (no 0/O/1/I), for reset and recovery codes read from paper. */
export function newHumanCode(groups = 3, groupLength = 4): string {
  const parts: string[] = [];
  for (let g = 0; g < groups; g++) {
    let part = '';
    for (let i = 0; i < groupLength; i++) part += codeAlphabet[randomInt(codeAlphabet.length)];
    parts.push(part);
  }
  return parts.join('-');
}

export function hashToken(token: string): string {
  return createHash('sha256').update(token, 'utf8').digest('hex');
}

/** Hash of a human code, ignoring case, spaces and hyphens so "k7qd 3mxa 9plt" matches. */
export function hashHumanCode(code: string | null | undefined): string {
  return hashToken((code ?? '').replace(/[^0-9a-z]/gi, '').toUpperCase());
}

export function sameHash(a: string, b: string): boolean {
  const left = Buffer.from(a, 'hex');
  const right = Buffer.from(b, 'hex');
  return left.length === right.length && timingSafeEqual(left, right);
}

// Secrets at rest (MFA secrets): AES-256-GCM under the SB_DATA_KEY secret, the purpose bound in as associated data.

function dataKey(): Buffer {
  const key = Buffer.from(process.env.SB_DATA_KEY ?? '', 'base64');
  if (key.length !== 32) throw new Error('SB_DATA_KEY must be a base64 256-bit key (Firebase secret).');
  return key;
}

export function protect(plain: Buffer, purpose: string): string {
  const iv = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', dataKey(), iv);
  cipher.setAAD(Buffer.from(purpose, 'utf8'));
  const data = Buffer.concat([cipher.update(plain), cipher.final()]);
  return ['v1', iv.toString('base64'), cipher.getAuthTag().toString('base64'), data.toString('base64')].join('.');
}

export function unprotect(text: string, purpose: string): Buffer {
  const [version, iv, tag, data] = text.split('.');
  if (version !== 'v1' || !iv || !tag || !data) throw new Error('Unreadable protected secret.');
  const decipher = createDecipheriv('aes-256-gcm', dataKey(), Buffer.from(iv, 'base64'));
  decipher.setAAD(Buffer.from(purpose, 'utf8'));
  decipher.setAuthTag(Buffer.from(tag, 'base64'));
  return Buffer.concat([decipher.update(Buffer.from(data, 'base64')), decipher.final()]);
}
