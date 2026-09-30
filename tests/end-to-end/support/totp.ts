import { createHmac } from 'node:crypto';

// RFC 6238 TOTP (HMAC-SHA1, 30 s, 6 digits) - plays the part of the user's authenticator app.

function base32Decode(text: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = 0;
  let value = 0;
  const bytes: number[] = [];
  for (const char of text.replace(/=+$/, '').toUpperCase()) {
    const index = alphabet.indexOf(char);
    if (index < 0) throw new Error(`Invalid Base32 character: ${char}`);
    value = (value << 5) | index;
    bits += 5;
    if (bits >= 8) {
      bytes.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(bytes);
}

export function totp(secretBase32: string, time = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(time / 1000 / 30)));
  const hash = createHmac('sha1', base32Decode(secretBase32)).update(counter).digest();
  const offset = hash[hash.length - 1]! & 0x0f;
  const code = (hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return code.toString().padStart(6, '0');
}

/** Waits until a fresh 30-second step starts, so a code can be used without clashing with the previous one. */
export async function nextTotpWindow(): Promise<void> {
  const remaining = 30_000 - (Date.now() % 30_000);
  await new Promise((resolve) => setTimeout(resolve, remaining + 500));
}
