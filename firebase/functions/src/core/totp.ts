import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto';

// Time-based one-time passwords (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits), as authenticator apps expect.

export const StepSeconds = 30;
export const Digits = 6;
const base32Alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

export function newSecret(): Buffer {
  return randomBytes(20);
}

export function stepAt(time: Date): number {
  return Math.floor(time.getTime() / 1000 / StepSeconds);
}

export function compute(secret: Buffer, step: number): string {
  const counter = Buffer.alloc(8);
  counter.writeBigInt64BE(BigInt(step));
  const hash = createHmac('sha1', secret).update(counter).digest();
  const offset = hash[hash.length - 1]! & 0x0f;
  const binary = (hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return binary.toString().padStart(Digits, '0');
}

/**
 * Accepts the code for the current step or one either side (clock drift), never a step at or before the last one
 * used, so an intercepted code cannot be replayed. Returns the matched step, or null.
 */
export function verify(secret: Buffer, code: string | null | undefined, now: Date, lastUsedStep: number | null): number | null {
  const digits = (code ?? '').replace(/[^0-9]/g, '');
  if (digits.length !== Digits) return null;
  const current = stepAt(now);
  for (let step = current - 1; step <= current + 1; step++) {
    if (lastUsedStep !== null && step <= lastUsedStep) continue;
    if (timingSafeEqual(Buffer.from(compute(secret, step)), Buffer.from(digits))) return step;
  }
  return null;
}

export function otpAuthUri(issuer: string, account: string, secret: Buffer): string {
  const i = encodeURIComponent(issuer);
  return `otpauth://totp/${i}:${encodeURIComponent(account)}?secret=${base32Encode(secret)}&issuer=${i}&algorithm=SHA1&digits=${Digits}&period=${StepSeconds}`;
}

export function base32Encode(data: Buffer): string {
  let output = '';
  let buffer = 0;
  let bits = 0;
  for (const byte of data) {
    buffer = (buffer << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      output += base32Alphabet[(buffer >> (bits - 5)) & 31];
      bits -= 5;
    }
  }
  if (bits > 0) output += base32Alphabet[(buffer << (5 - bits)) & 31];
  return output;
}

export function base32Decode(text: string): Buffer {
  const output: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const c of text.replace(/=+$/, '').toUpperCase()) {
    const value = base32Alphabet.indexOf(c);
    if (value < 0) throw new Error(`'${c}' is not a Base32 character.`);
    buffer = (buffer << 5) | value;
    bits += 5;
    if (bits >= 8) {
      output.push((buffer >> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(output);
}
