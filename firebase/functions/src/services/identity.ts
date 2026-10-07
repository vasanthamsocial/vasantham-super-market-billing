import type { Transaction } from 'firebase-admin/firestore';
import { audit, userId, type RequestContext } from '../core/context.js';
import { clock } from '../core/clock.js';
import { C, db, doc, getAll, inTransaction, uniqueKey, type Doc } from '../core/db.js';
import { AppError, DomainError } from '../core/errors.js';
import { newId } from '../core/ids.js';
import * as secrets from '../core/secrets.js';
import { settings } from '../core/settings.js';
import * as totp from '../core/totp.js';
import { Roles } from '../domain/permissions.js';
import { normalizeUsername, passwordProblem } from '../domain/rules.js';

export const SessionStates = {
  Active: 'active',
  MfaRequired: 'mfa_required',
  MfaEnrolmentRequired: 'mfa_enrolment_required',
  PasswordChangeRequired: 'password_change_required',
} as const;

const MfaSecretPurpose = 'mfa-secret';
const Issuer = 'SupermarketBilling';
const RecoveryCodeCount = 10;
const InvalidCredentials = 'Invalid username or password.';
const LastSeenResolutionMs = 60_000;

export interface UserDoc {
  id: string;
  username: string;
  displayName: string;
  isActive: boolean;
  passwordHash: string;
  passwordChangedAtUtc: string;
  mustChangePassword: boolean;
  failedLoginCount: number;
  lockedUntilUtc: string | null;
  lastLoginAtUtc: string | null;
  mfaEnabled: boolean;
  mfaSecretProtected: string | null;
  mfaPendingSecretProtected: string | null;
  mfaLastUsedStep: number | null;
  createdAtUtc: string;
  rowVersion: number;
}

interface SessionDoc {
  id: string;
  userId: string;
  tokenHash: string;
  csrfTokenHash: string;
  createdAtUtc: string;
  lastSeenAtUtc: string;
  idleExpiresAtUtc: string;
  absoluteExpiresAtUtc: string;
  mfaSatisfied: boolean;
  ipAddress: string | null;
  userAgent: string | null;
  revokedAtUtc: string | null;
  revokedReason: string | null;
}

export function newUser(username: string, name: string, passwordHash: string, now: Date, mustChangePassword: boolean): UserDoc {
  return {
    id: newId(now),
    username,
    displayName: name,
    isActive: true,
    passwordHash,
    passwordChangedAtUtc: now.toISOString(),
    mustChangePassword,
    failedLoginCount: 0,
    lockedUntilUtc: null,
    lastLoginAtUtc: null,
    mfaEnabled: false,
    mfaSecretProtected: null,
    mfaPendingSecretProtected: null,
    mfaLastUsedStep: null,
    createdAtUtc: now.toISOString(),
    rowVersion: 1,
  };
}

/** Creates the user and claims the username in the same transaction (usernames are unique). */
export function createUser(tx: Transaction, user: UserDoc): void {
  tx.create(doc(C.uniques, uniqueKey('username', user.username)), { userId: user.id });
  tx.create(doc(C.users, user.id), user);
}

export function isLockedOut(user: UserDoc, now: Date): boolean {
  return !!user.lockedUntilUtc && new Date(user.lockedUntilUtc) > now;
}

export function ensurePasswordPolicy(password: string | null | undefined, username: string): void {
  const problem = passwordProblem(password ?? '', username);
  if (problem) throw AppError.validation('password.policy', problem);
}

function lockedOut(user: UserDoc): AppError {
  const until = user.lockedUntilUtc ? new Date(user.lockedUntilUtc).toISOString().slice(11, 16) : '';
  return new AppError('locked', 'account_locked',
    `This account is temporarily locked after too many failed attempts. Try again after ${until} UTC or ask a manager to unlock it.`);
}

/** Counts a failure towards the lockout; returns the updated fields to write. */
function failure(user: UserDoc, now: Date): Partial<UserDoc> & { locked: boolean } {
  const count = user.failedLoginCount + 1;
  if (count >= settings.maxFailedLogins) {
    return { failedLoginCount: 0, lockedUntilUtc: new Date(now.getTime() + settings.lockoutMinutes * 60_000).toISOString(), locked: true };
  }
  return { failedLoginCount: count, locked: false };
}

function recordFailure(tx: Transaction, ctx: RequestContext, user: UserDoc, reason: string, now: Date): UserDoc {
  const { locked, ...change } = failure(user, now);
  const updated = { ...user, ...change, rowVersion: user.rowVersion + 1 };
  tx.update(doc(C.users, user.id), { ...change, rowVersion: updated.rowVersion });
  audit(tx, ctx, { eventType: 'auth.login_failed', entityType: 'user', entityId: user.id, details: { reason }, actorUserId: user.id });
  if (locked) audit(tx, ctx, { eventType: 'auth.account_locked', entityType: 'user', entityId: user.id, details: { until: updated.lockedUntilUtc }, actorUserId: user.id });
  return updated;
}

async function findUser(tx: Transaction, username: string | null | undefined): Promise<UserDoc | null> {
  let normalized: string;
  try {
    normalized = normalizeUsername(username);
  } catch {
    return null;
  }
  const claim = await tx.get(doc(C.uniques, uniqueKey('username', normalized)));
  if (!claim.exists) return null;
  const user = await tx.get(doc(C.users, claim.data()!.userId as string));
  return user.exists ? (user.data() as UserDoc) : null;
}

/**
 * Runs work in a transaction in which the user's document is read (and so guarded against concurrent change): parallel
 * guesses cannot lose failed-attempt counts. Bookkeeping written before an expected failure is committed, then the
 * failure is reported.
 */
async function guarded<T>(work: (tx: Transaction) => Promise<T | AppError>): Promise<T> {
  const result = await inTransaction(work);
  if (result instanceof AppError) throw result;
  return result;
}

// Sessions

export interface AuthenticatedSession {
  id: string;
  userId: string;
  username: string;
  state: string;
}

/** Validates the session cookie: the session exists, is not revoked or expired, and its user is active. */
export async function validateSession(token: string): Promise<AuthenticatedSession | null> {
  if (!token || token.length > 100) return null;
  const now = clock.now();
  const found = await db.collection(C.sessions).where('tokenHash', '==', secrets.hashToken(token)).limit(1).get();
  const session = found.docs[0]?.data() as SessionDoc | undefined;
  if (!session || !sessionValid(session, now)) return null;
  const userSnap = await doc(C.users, session.userId).get();
  const user = userSnap.data() as UserDoc | undefined;
  if (!user || !user.isActive) return null;

  // Sliding idle timeout, written at most once a minute per session.
  if (now.getTime() - new Date(session.lastSeenAtUtc).getTime() >= LastSeenResolutionMs) {
    const idle = Math.min(now.getTime() + settings.sessionIdleMinutes * 60_000, new Date(session.absoluteExpiresAtUtc).getTime());
    await doc(C.sessions, session.id).update({ lastSeenAtUtc: now.toISOString(), idleExpiresAtUtc: new Date(idle).toISOString() });
  }

  return { id: session.id, userId: user.id, username: user.username, state: await computeState(user, session.mfaSatisfied) };
}

function sessionValid(s: SessionDoc, now: Date): boolean {
  return s.revokedAtUtc === null && now < new Date(s.idleExpiresAtUtc) && now < new Date(s.absoluteExpiresAtUtc);
}

export async function validateCsrf(sessionId: string, csrfToken: string | undefined): Promise<boolean> {
  if (!csrfToken || csrfToken.length > 100) return false;
  const session = (await doc(C.sessions, sessionId).get()).data() as SessionDoc | undefined;
  return !!session && secrets.sameHash(session.csrfTokenHash, secrets.hashToken(csrfToken));
}

/** The second factor first, then a forced password change, then mandatory MFA enrolment. Only "active" may use the app. */
export async function computeState(user: UserDoc, mfaSatisfied: boolean): Promise<string> {
  if (user.mfaEnabled && !mfaSatisfied) return SessionStates.MfaRequired;
  if (user.mustChangePassword) return SessionStates.PasswordChangeRequired;
  if (!user.mfaEnabled && (await mfaRequiredByPolicy(user.id))) return SessionStates.MfaEnrolmentRequired;
  return SessionStates.Active;
}

/** True when the user holds a privileged role in an active business that requires MFA for privileged users. */
export async function mfaRequiredByPolicy(forUserId: string): Promise<boolean> {
  const assignments = await db.collection(C.roleAssignments).where('userId', '==', forUserId).where('revokedAtUtc', '==', null).get();
  const businessIds = assignments.docs.map((d) => d.data()).filter((a) => Roles.get(a.roleCode).isPrivileged).map((a) => a.businessId as string);
  const businesses = await getAll(C.businesses, businessIds);
  return [...businesses.values()].some((b) => b.isActive && b.requireMfaForPrivilegedUsers);
}

function newSession(user: UserDoc, sessionToken: string, csrfToken: string, now: Date, ctx: RequestContext): SessionDoc {
  return {
    id: newId(now),
    userId: user.id,
    tokenHash: secrets.hashToken(sessionToken),
    csrfTokenHash: secrets.hashToken(csrfToken),
    createdAtUtc: now.toISOString(),
    lastSeenAtUtc: now.toISOString(),
    idleExpiresAtUtc: new Date(now.getTime() + settings.sessionIdleMinutes * 60_000).toISOString(),
    absoluteExpiresAtUtc: new Date(now.getTime() + settings.sessionAbsoluteHours * 3_600_000).toISOString(),
    mfaSatisfied: false,
    ipAddress: ctx.ip,
    userAgent: ctx.userAgent ? ctx.userAgent.slice(0, 300) : null,
    revokedAtUtc: null,
    revokedReason: null,
  };
}

// The signed-in user

export interface MembershipDto {
  businessId: string;
  businessCode: string;
  businessName: string;
  roles: { assignmentId: string; roleCode: string; roleName: string; storeId: string | null; storeName: string | null }[];
  permissions: string[];
}

export interface MeResponse {
  userId: string;
  username: string;
  displayName: string;
  sessionState: string;
  mfaEnabled: boolean;
  mfaRequiredByPolicy: boolean;
  memberships: MembershipDto[];
}

/** Per business: the user's active roles (with stores) and their combined permissions. */
export async function memberships(forUserId: string): Promise<MembershipDto[]> {
  const snap = await db.collection(C.roleAssignments).where('userId', '==', forUserId).where('revokedAtUtc', '==', null).get();
  const rows = snap.docs.map((d) => d.data());
  const businesses = await getAll(C.businesses, rows.map((r) => r.businessId));
  const stores = await getAll(C.stores, rows.map((r) => r.storeId).filter(Boolean));
  const grouped = new Map<string, MembershipDto>();
  const active = rows.filter((r) => businesses.get(r.businessId)?.isActive)
    .sort((a, b) => cmp(businesses.get(a.businessId)!.code, businesses.get(b.businessId)!.code) || cmp(a.roleCode, b.roleCode));
  for (const r of active) {
    const b = businesses.get(r.businessId)!;
    let m = grouped.get(b.id);
    if (!m) {
      m = { businessId: b.id, businessCode: b.code, businessName: b.tradeName, roles: [], permissions: [] };
      grouped.set(b.id, m);
    }
    m.roles.push({ assignmentId: r.id, roleCode: r.roleCode, roleName: Roles.get(r.roleCode).name, storeId: r.storeId ?? null, storeName: stores.get(r.storeId)?.name ?? null });
  }
  for (const m of grouped.values()) {
    m.permissions = [...new Set(m.roles.flatMap((r) => [...Roles.get(r.roleCode).permissions]))].sort(cmp);
  }
  return [...grouped.values()];
}

export function cmp(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

async function buildMe(user: UserDoc, mfaSatisfied: boolean): Promise<MeResponse> {
  return {
    userId: user.id,
    username: user.username,
    displayName: user.displayName,
    sessionState: await computeState(user, mfaSatisfied),
    mfaEnabled: user.mfaEnabled,
    mfaRequiredByPolicy: await mfaRequiredByPolicy(user.id),
    memberships: await memberships(user.id),
  };
}

async function currentSession(ctx: RequestContext): Promise<SessionDoc> {
  return (await doc(C.sessions, ctx.session!.id).get()).data() as SessionDoc;
}

export async function me(ctx: RequestContext): Promise<MeResponse> {
  const user = (await doc(C.users, userId(ctx)).get()).data() as UserDoc;
  return buildMe(user, (await currentSession(ctx)).mfaSatisfied);
}

// Sign-in

export interface LoginOutcome {
  sessionToken: string;
  csrfToken: string;
  me: MeResponse;
}

export async function login(ctx: RequestContext, body: { username?: string; password?: string }): Promise<LoginOutcome> {
  const password = body.password ?? '';
  const sessionToken = secrets.newToken();
  const csrfToken = secrets.newToken();
  const result = await guarded(async (tx) => {
    const now = clock.now();
    const user = await findUser(tx, body.username);
    if (!user || !user.isActive) {
      // Same work and the same answer whether the username is unknown or disabled.
      await secrets.verifyDummy(password);
      audit(tx, ctx, { eventType: 'auth.login_failed', entityType: 'user', entityId: user?.id, details: { reason: user ? 'disabled' : 'unknown_user' }, actorUserId: user?.id ?? null });
      return new AppError('unauthorized', 'invalid_credentials', InvalidCredentials);
    }

    if (isLockedOut(user, now)) {
      audit(tx, ctx, { eventType: 'auth.login_blocked', entityType: 'user', entityId: user.id, details: { reason: 'locked' }, actorUserId: user.id });
      return lockedOut(user);
    }

    const check = await secrets.verifyPassword(user.passwordHash, password);
    if (!check.valid) {
      const updated = recordFailure(tx, ctx, user, 'wrong_password', now);
      return isLockedOut(updated, now) ? lockedOut(updated) : new AppError('unauthorized', 'invalid_credentials', InvalidCredentials);
    }

    const session = newSession(user, sessionToken, csrfToken, now, ctx);
    const change: Partial<UserDoc> = { failedLoginCount: 0, lockedUntilUtc: null, lastLoginAtUtc: now.toISOString(), rowVersion: user.rowVersion + 1 };
    if (check.needsRehash) change.passwordHash = await secrets.hashPassword(password);
    tx.update(doc(C.users, user.id), change);
    tx.create(doc(C.sessions, session.id), session);
    audit(tx, ctx, { eventType: 'auth.login_succeeded', entityType: 'session', entityId: session.id, details: { mfaPending: user.mfaEnabled }, actorUserId: user.id });
    return { user: { ...user, ...change } as UserDoc, session };
  });
  return { sessionToken, csrfToken, me: await buildMe(result.user, result.session.mfaSatisfied) };
}

/**
 * Checks another person's credentials typed in at a counter (a supervisor approving a price or discount), with the same
 * protections as signing in. No session is created. Returns the verified user.
 */
export async function verifyCredentials(ctx: RequestContext, username: string | null, password: string | null, mfaCode: string | null): Promise<UserDoc> {
  return guarded(async (tx) => {
    const now = clock.now();
    const user = await findUser(tx, username);
    if (!user || !user.isActive) {
      await secrets.verifyDummy(password ?? '');
      audit(tx, ctx, { eventType: 'auth.approval_credentials_failed', entityType: 'user', entityId: user?.id, details: { reason: user ? 'disabled' : 'unknown_user' } });
      return new AppError('unauthorized', 'invalid_credentials', InvalidCredentials);
    }
    if (isLockedOut(user, now)) return lockedOut(user);
    if (!(await secrets.verifyPassword(user.passwordHash, password ?? '')).valid) {
      const updated = recordFailure(tx, ctx, user, 'wrong_password_at_approval', now);
      return isLockedOut(updated, now) ? lockedOut(updated) : new AppError('unauthorized', 'invalid_credentials', InvalidCredentials);
    }
    if (user.mustChangePassword) return AppError.forbidden('This person must change their password before they can approve anything.');
    let step: number | null = null;
    let recovery: string | null = null;
    if (user.mfaEnabled) {
      if (!mfaCode?.trim()) return AppError.validation('mfa.code_required', "Enter the approver's two-step verification code.");
      step = totp.verify(secrets.unprotect(user.mfaSecretProtected!, MfaSecretPurpose), mfaCode, now, user.mfaLastUsedStep);
      recovery = step === null ? await findRecoveryCode(tx, user.id, mfaCode) : null;
      if (step === null && recovery === null) {
        const updated = recordFailure(tx, ctx, user, 'wrong_mfa_code_at_approval', now);
        return isLockedOut(updated, now) ? lockedOut(updated) : new AppError('unauthorized', 'mfa.invalid_code', "The approver's code is not valid.");
      }
    }
    if (recovery) tx.update(doc(C.mfaRecoveryCodes, recovery), { usedAtUtc: now.toISOString() });
    const change = { failedLoginCount: 0, lockedUntilUtc: null, ...(step !== null ? { mfaLastUsedStep: step } : {}), rowVersion: user.rowVersion + 1 };
    tx.update(doc(C.users, user.id), change);
    return { ...user, ...change };
  });
}

export async function logout(ctx: RequestContext): Promise<void> {
  await inTransaction(async (tx) => {
    const ref = doc(C.sessions, ctx.session!.id);
    const session = (await tx.get(ref)).data() as SessionDoc | undefined;
    if (!session) return;
    if (session.revokedAtUtc === null) tx.update(ref, { revokedAtUtc: clock.now().toISOString(), revokedReason: 'logout' });
    audit(tx, ctx, { eventType: 'auth.logout', entityType: 'session', entityId: session.id });
  });
}

// Two-step verification

async function findRecoveryCode(tx: Transaction, forUserId: string, code: string | null | undefined): Promise<string | null> {
  if (!code || (code.match(/[0-9a-z]/gi) ?? []).length !== 12) return null;
  const snap = await tx.get(db.collection(C.mfaRecoveryCodes).where('userId', '==', forUserId).where('codeHash', '==', secrets.hashHumanCode(code))
    .where('usedAtUtc', '==', null).limit(1));
  return snap.docs[0]?.id ?? null;
}

async function lockUser(tx: Transaction, id: string): Promise<UserDoc> {
  return (await tx.get(doc(C.users, id))).data() as UserDoc;
}

export async function verifyMfa(ctx: RequestContext, code: string | undefined): Promise<MeResponse> {
  const user = await guarded(async (tx) => {
    const now = clock.now();
    const sessionRef = doc(C.sessions, ctx.session!.id);
    const user = await lockUser(tx, userId(ctx));
    const session = (await tx.get(sessionRef)).data() as SessionDoc;
    if (!user.mfaEnabled || session.mfaSatisfied) return AppError.validation('mfa.not_required', 'This session does not need an MFA code.');
    if (isLockedOut(user, now)) return lockedOut(user);
    const step = totp.verify(secrets.unprotect(user.mfaSecretProtected!, MfaSecretPurpose), code, now, user.mfaLastUsedStep);
    const recovery = step === null ? await findRecoveryCode(tx, user.id, code) : null;
    if (step === null && recovery === null) {
      const updated = recordFailure(tx, ctx, user, 'wrong_mfa_code', now);
      if (isLockedOut(updated, now)) {
        tx.update(sessionRef, { revokedAtUtc: now.toISOString(), revokedReason: 'locked' });
        return lockedOut(updated);
      }
      return new AppError('unauthorized', 'mfa.invalid_code', 'The code is not valid. Check the time on your phone and try again.');
    }
    if (recovery) tx.update(doc(C.mfaRecoveryCodes, recovery), { usedAtUtc: now.toISOString() });
    const change = { failedLoginCount: 0, lockedUntilUtc: null, lastLoginAtUtc: now.toISOString(), ...(step !== null ? { mfaLastUsedStep: step } : {}),
      rowVersion: user.rowVersion + 1 };
    tx.update(doc(C.users, user.id), change);
    tx.update(sessionRef, { mfaSatisfied: true });
    audit(tx, ctx, { eventType: 'auth.mfa_verified', entityType: 'session', entityId: session.id, details: { recoveryCode: recovery !== null } });
    return { ...user, ...change };
  });
  return buildMe(user, true);
}

export async function beginMfaSetup(ctx: RequestContext): Promise<{ secret: string; otpAuthUri: string }> {
  return guarded(async (tx) => {
    const user = await lockUser(tx, userId(ctx));
    if (user.mfaEnabled) return AppError.conflict('mfa.already_enabled', 'MFA is already enabled. Disable it first to set up a new device.');
    const secret = totp.newSecret();
    tx.update(doc(C.users, user.id), { mfaPendingSecretProtected: secrets.protect(secret, MfaSecretPurpose), rowVersion: user.rowVersion + 1 });
    audit(tx, ctx, { eventType: 'auth.mfa_setup_started', entityType: 'user', entityId: user.id });
    return { secret: totp.base32Encode(secret), otpAuthUri: totp.otpAuthUri(Issuer, user.username, secret) };
  });
}

export async function confirmMfaSetup(ctx: RequestContext, code: string | undefined): Promise<{ recoveryCodes: string[] }> {
  const existing = await db.collection(C.mfaRecoveryCodes).where('userId', '==', userId(ctx)).get();
  return guarded(async (tx) => {
    const now = clock.now();
    const user = await lockUser(tx, userId(ctx));
    if (!user.mfaPendingSecretProtected) return AppError.validation('mfa.no_pending_enrolment', 'Start MFA setup first.');
    const step = totp.verify(secrets.unprotect(user.mfaPendingSecretProtected, MfaSecretPurpose), code, now, null);
    if (step === null) return AppError.validation('mfa.invalid_code', 'The code is not valid. Scan the QR code again and enter the current 6-digit code.');
    tx.update(doc(C.users, user.id), {
      mfaSecretProtected: user.mfaPendingSecretProtected, mfaPendingSecretProtected: null, mfaEnabled: true, mfaLastUsedStep: step, rowVersion: user.rowVersion + 1,
    });
    for (const old of existing.docs) tx.delete(old.ref);
    const codes = Array.from({ length: RecoveryCodeCount }, () => secrets.newHumanCode());
    for (const c of codes) {
      const id = newId(now);
      tx.create(doc(C.mfaRecoveryCodes, id), { id, userId: user.id, codeHash: secrets.hashHumanCode(c), createdAtUtc: now.toISOString(), usedAtUtc: null });
    }
    tx.update(doc(C.sessions, ctx.session!.id), { mfaSatisfied: true });
    audit(tx, ctx, { eventType: 'auth.mfa_enabled', entityType: 'user', entityId: user.id });
    return { recoveryCodes: codes };
  });
}

export async function disableMfa(ctx: RequestContext, body: { password?: string; code?: string }): Promise<void> {
  if (await mfaRequiredByPolicy(userId(ctx))) throw AppError.forbidden('Your role requires MFA, so it cannot be turned off.');
  const existing = await db.collection(C.mfaRecoveryCodes).where('userId', '==', userId(ctx)).get();
  await guarded(async (tx) => {
    const now = clock.now();
    const user = await lockUser(tx, userId(ctx));
    if (!user.mfaEnabled) return AppError.validation('mfa.not_enabled', 'MFA is not enabled.');
    const passwordOk = (await secrets.verifyPassword(user.passwordHash, body.password ?? '')).valid;
    const codeOk = totp.verify(secrets.unprotect(user.mfaSecretProtected!, MfaSecretPurpose), body.code, now, user.mfaLastUsedStep) !== null;
    if (!passwordOk || !codeOk) {
      recordFailure(tx, ctx, user, 'mfa_disable_failed', now);
      return new AppError('unauthorized', 'invalid_credentials', 'The password or code is not correct.');
    }
    tx.update(doc(C.users, user.id), { mfaEnabled: false, mfaSecretProtected: null, mfaPendingSecretProtected: null, mfaLastUsedStep: null, rowVersion: user.rowVersion + 1 });
    for (const old of existing.docs) tx.delete(old.ref);
    audit(tx, ctx, { eventType: 'auth.mfa_disabled', entityType: 'user', entityId: user.id });
    return true;
  });
}

// Passwords

/** Revokes a user's active sessions (except one) inside the transaction; returns how many. */
export async function revokeSessions(tx: Transaction, forUserId: string, now: Date, reason: string, except: string | null): Promise<number> {
  const active = await tx.get(db.collection(C.sessions).where('userId', '==', forUserId).where('revokedAtUtc', '==', null));
  let count = 0;
  for (const s of active.docs) {
    if (s.id === except) continue;
    tx.update(s.ref, { revokedAtUtc: now.toISOString(), revokedReason: reason });
    count++;
  }
  return count;
}

export async function changePassword(ctx: RequestContext, body: { currentPassword?: string; newPassword?: string }): Promise<MeResponse> {
  const newHash = body.newPassword ? await secrets.hashPassword(body.newPassword) : '';
  const user = await guarded(async (tx) => {
    const now = clock.now();
    const user = await lockUser(tx, userId(ctx));
    const sessions = await tx.get(db.collection(C.sessions).where('userId', '==', user.id).where('revokedAtUtc', '==', null));
    if (!(await secrets.verifyPassword(user.passwordHash, body.currentPassword ?? '')).valid) {
      recordFailure(tx, ctx, user, 'change_password_wrong_current', now);
      return AppError.validation('password.current_incorrect', 'The current password is not correct.');
    }
    const problem = passwordProblem(body.newPassword ?? '', user.username);
    if (problem) return AppError.validation('password.policy', problem);
    if ((await secrets.verifyPassword(user.passwordHash, body.newPassword!)).valid) {
      return AppError.validation('password.reused', 'The new password must be different from the current one.');
    }
    const change = { passwordHash: newHash, passwordChangedAtUtc: now.toISOString(), mustChangePassword: false, failedLoginCount: 0, lockedUntilUtc: null,
      rowVersion: user.rowVersion + 1 };
    tx.update(doc(C.users, user.id), change);
    let revoked = 0;
    for (const s of sessions.docs) {
      if (s.id === ctx.session!.id) continue;
      tx.update(s.ref, { revokedAtUtc: now.toISOString(), revokedReason: 'password_changed' });
      revoked++;
    }
    audit(tx, ctx, { eventType: 'auth.password_changed', entityType: 'user', entityId: user.id, details: { otherSessionsRevoked: revoked } });
    return { ...user, ...change };
  });
  return buildMe(user, (await currentSession(ctx)).mfaSatisfied);
}

/** Completes a manager-issued reset. The code travels in the request body, never in a URL. */
export async function resetPassword(ctx: RequestContext, body: { username?: string; resetCode?: string; newPassword?: string }): Promise<void> {
  const newHash = body.newPassword ? await secrets.hashPassword(body.newPassword) : '';
  const invalid = AppError.validation('reset.invalid', 'The reset code is not valid or has expired. Ask your manager for a new one.');
  await guarded(async (tx) => {
    const now = clock.now();
    const user = await findUser(tx, body.username);
    if (!user || !user.isActive) return invalid;
    const tokens = await tx.get(db.collection(C.passwordResetTokens).where('userId', '==', user.id).where('tokenHash', '==', secrets.hashHumanCode(body.resetCode)).limit(1));
    const sessions = await tx.get(db.collection(C.sessions).where('userId', '==', user.id).where('revokedAtUtc', '==', null));
    const token = tokens.docs[0]?.data();
    if (!token || token.usedAtUtc !== null || now >= new Date(token.expiresAtUtc)) {
      audit(tx, ctx, { eventType: 'auth.password_reset_failed', entityType: 'user', entityId: user.id, actorUserId: user.id });
      return invalid;
    }
    const problem = passwordProblem(body.newPassword ?? '', user.username);
    if (problem) return AppError.validation('password.policy', problem);
    tx.update(tokens.docs[0]!.ref, { usedAtUtc: now.toISOString() });
    tx.update(doc(C.users, user.id), { passwordHash: newHash, passwordChangedAtUtc: now.toISOString(), mustChangePassword: false, failedLoginCount: 0,
      lockedUntilUtc: null, rowVersion: user.rowVersion + 1 });
    for (const s of sessions.docs) tx.update(s.ref, { revokedAtUtc: now.toISOString(), revokedReason: 'password_reset' });
    audit(tx, ctx, { eventType: 'auth.password_reset_completed', entityType: 'user', entityId: user.id,
      details: { issuedBy: token.issuedByUserId, sessionsRevoked: sessions.size }, actorUserId: user.id });
    return true;
  });
}

// Own sessions

export async function listSessions(ctx: RequestContext): Promise<unknown[]> {
  const now = clock.now();
  const snap = await db.collection(C.sessions).where('userId', '==', userId(ctx)).where('revokedAtUtc', '==', null).get();
  return snap.docs.map((d) => d.data() as SessionDoc).filter((s) => sessionValid(s, now))
    .sort((a, b) => cmp(b.lastSeenAtUtc, a.lastSeenAtUtc))
    .map((s) => ({ id: s.id, createdAtUtc: s.createdAtUtc, lastSeenAtUtc: s.lastSeenAtUtc, ipAddress: s.ipAddress, userAgent: s.userAgent, isCurrent: s.id === ctx.session!.id }));
}

export async function revokeOwnSession(ctx: RequestContext, sessionId: string): Promise<void> {
  await guarded(async (tx) => {
    const ref = doc(C.sessions, sessionId);
    const session = (await tx.get(ref)).data() as SessionDoc | undefined;
    if (!session || session.userId !== userId(ctx)) return AppError.notFound('Session');
    if (session.revokedAtUtc === null) tx.update(ref, { revokedAtUtc: clock.now().toISOString(), revokedReason: 'revoked_by_user' });
    audit(tx, ctx, { eventType: 'auth.session_revoked', entityType: 'session', entityId: session.id });
    return true;
  });
}

/** Turns a domain rule failure into the API's validation answer. */
export function valid<T>(work: () => T): T {
  try {
    return work();
  } catch (e) {
    if (e instanceof DomainError) throw AppError.validation(e.code, e.message);
    throw e;
  }
}

export type { Doc };
