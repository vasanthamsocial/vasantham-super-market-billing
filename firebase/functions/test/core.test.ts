import { describe, expect, it } from 'vitest';
import * as secrets from '../src/core/secrets.js';
import * as totp from '../src/core/totp.js';
import { canGrant, canManage, covers, P, R, Roles, type ActiveGrant } from '../src/domain/permissions.js';
import * as rules from '../src/domain/rules.js';

describe('two-step codes (RFC 6238)', () => {
  // The RFC's SHA-1 test secret is the ASCII "12345678901234567890"; its expected codes are 8 digits, we use the last 6.
  const secret = Buffer.from('12345678901234567890', 'ascii');
  it.each([
    [59, '287082'],
    [1111111109, '081804'],
    [1111111111, '050471'],
    [1234567890, '005924'],
    [2000000000, '279037'],
  ])('at %i seconds the code is %s', (seconds, code) => {
    expect(totp.compute(secret, Math.floor(seconds / 30))).toBe(code);
  });

  it('accepts one step of drift but never a code already used', () => {
    const now = new Date(1_700_000_000_000);
    const step = totp.stepAt(now);
    expect(totp.verify(secret, totp.compute(secret, step - 1), now, null)).toBe(step - 1);
    expect(totp.verify(secret, totp.compute(secret, step - 2), now, null)).toBeNull();
    expect(totp.verify(secret, totp.compute(secret, step), now, step)).toBeNull();
    expect(totp.verify(secret, '12345', now, null)).toBeNull();
  });

  it('round-trips base32 secrets', () => {
    const s = totp.newSecret();
    expect(totp.base32Decode(totp.base32Encode(s))).toEqual(s);
  });
});

describe('secrets', () => {
  it('hashes passwords with PBKDF2 and verifies only the right one', async () => {
    const hash = await secrets.hashPassword('Correct-Horse-9');
    expect(hash.startsWith('pbkdf2-sha512$')).toBe(true);
    expect((await secrets.verifyPassword(hash, 'Correct-Horse-9')).valid).toBe(true);
    expect((await secrets.verifyPassword(hash, 'correct-horse-9')).valid).toBe(false);
  });

  it('matches human codes ignoring case, spaces and hyphens', () => {
    const code = secrets.newHumanCode();
    expect(code).toMatch(/^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/);
    expect(secrets.hashHumanCode(code.toLowerCase().replace(/-/g, ' '))).toBe(secrets.hashHumanCode(code));
  });

  it('encrypts secrets bound to their purpose', () => {
    process.env.SB_DATA_KEY = Buffer.alloc(32, 7).toString('base64');
    const sealed = secrets.protect(Buffer.from('top secret'), 'mfa-secret');
    expect(secrets.unprotect(sealed, 'mfa-secret').toString()).toBe('top secret');
    expect(() => secrets.unprotect(sealed, 'other')).toThrow();
  });
});

describe('rules', () => {
  it('validates GSTINs with their check character and state', () => {
    const gstin = rules.gstinComplete('33AAACG1234A1Z');
    expect(rules.gstinValid(gstin)).toBe(true);
    expect(rules.gstinValid(gstin.slice(0, 14) + (gstin[14] === 'A' ? 'B' : 'A'))).toBe(false);
    expect(() => rules.validateGstin(gstin, '29')).toThrow(/belongs to state 33/);
  });

  it('applies the password policy', () => {
    expect(rules.passwordProblem('short', 'priya')).toMatch(/at least 10/);
    expect(rules.passwordProblem('priya-password-1', 'priya')).toMatch(/username/);
    expect(rules.passwordProblem('aaaaaaaaaaaa', 'priya')).toMatch(/too simple/);
    expect(rules.passwordProblem('Strong-Pass-42', 'priya')).toBeNull();
  });

  it('normalises usernames and codes', () => {
    expect(rules.normalizeUsername('  Priya.K ')).toBe('priya.k');
    expect(() => rules.normalizeUsername('a')).toThrow();
    expect(rules.businessCode(' smkt ')).toBe('SMKT');
    expect(() => rules.stateCode('00')).toThrow();
  });
});

describe('permissions', () => {
  const b = 'b1';
  const owner: ActiveGrant[] = [{ assignmentId: 'a', roleCode: R.Owner, businessId: b, storeId: null }];
  const storeManager: ActiveGrant[] = [{ assignmentId: 'm', roleCode: R.Manager, businessId: b, storeId: 's1' }];

  it('needs a business-wide grant for business checks, and accepts the store grant for that store', () => {
    expect(covers(storeManager, P.UsersManage, b, null)).toBe(false);
    expect(covers(storeManager, P.UsersManage, b, 's1')).toBe(true);
    expect(covers(storeManager, P.UsersManage, b, 's2')).toBe(false);
  });

  it('never lets anyone grant or manage beyond their own permissions', () => {
    expect(canGrant(storeManager, P.RolesAssign, Roles.get(R.Cashier), b, 's1')).toBe(true);
    expect(canGrant(storeManager, P.RolesAssign, Roles.get(R.Owner), b, 's1')).toBe(false);
    expect(canManage(storeManager, owner, P.UsersManage)).toBe(false);
    expect(canManage(owner, storeManager, P.UsersManage)).toBe(true);
  });

  it('keeps the same roles as the specification', () => {
    expect(Roles.all().map((r) => r.code).sort()).toEqual(Object.values(R).sort());
    expect(Roles.get(R.Owner).permissions.size).toBe(Object.keys(P).length);
  });
});
