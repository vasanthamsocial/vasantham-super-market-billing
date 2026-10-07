import type { Transaction } from 'firebase-admin/firestore';
import { audit, grants, loadGrants, userId, type RequestContext } from '../core/context.js';
import { clock } from '../core/clock.js';
import { C, db, doc, getAll, inTransaction } from '../core/db.js';
import { AppError } from '../core/errors.js';
import { newId } from '../core/ids.js';
import * as secrets from '../core/secrets.js';
import { settings } from '../core/settings.js';
import { canApproveGrant, canGrant, canManage, covers, P, R, Roles, type ActiveGrant, type RoleDefinition } from '../domain/permissions.js';
import * as rules from '../domain/rules.js';
import { requirePermission } from './access.js';
import { cmp, createUser, ensurePasswordPolicy, isLockedOut, newUser, valid, type UserDoc } from './identity.js';
import { duplicate } from './organisation.js';

export const RoleGrantApprovalType = 'role.grant';

interface RoleGrantPayload {
  userId: string;
  roleCode: string;
  businessId: string;
  storeId: string | null;
}

export function listRoles() {
  return Roles.all().map((r) => ({ code: r.code, name: r.name, isPrivileged: r.isPrivileged, businessWideOnly: r.businessWideOnly,
    permissions: [...r.permissions].sort(cmp) }));
}

/** Role grants pending approval in a business (people whose first, privileged role is waiting still show on the list). */
async function pendingGrants(businessId: string): Promise<RoleGrantPayload[]> {
  const now = clock.now().toISOString();
  const snap = await db.collection(C.approvalRequests).where('businessId', '==', businessId).where('type', '==', RoleGrantApprovalType)
    .where('status', '==', 'pending').get();
  return snap.docs.map((d) => d.data()).filter((a) => a.expiresAtUtc > now).map((a) => JSON.parse(a.payloadJson) as RoleGrantPayload);
}

async function userDto(user: UserDoc, businessId: string, pending?: RoleGrantPayload[]) {
  pending ??= await pendingGrants(businessId);
  const snap = await db.collection(C.roleAssignments).where('userId', '==', user.id).where('businessId', '==', businessId).where('revokedAtUtc', '==', null).get();
  const roles = snap.docs.map((d) => d.data()).sort((a, b) => cmp(a.roleCode, b.roleCode));
  const stores = await getAll(C.stores, roles.map((r) => r.storeId).filter(Boolean));
  return {
    id: user.id,
    username: user.username,
    displayName: user.displayName,
    isActive: user.isActive,
    isLockedOut: isLockedOut(user, clock.now()),
    mfaEnabled: user.mfaEnabled,
    mustChangePassword: user.mustChangePassword,
    lastLoginAtUtc: user.lastLoginAtUtc,
    roles: roles.map((r) => ({ assignmentId: r.id, roleCode: r.roleCode, roleName: Roles.get(r.roleCode).name, storeId: r.storeId ?? null,
      storeName: stores.get(r.storeId)?.name ?? null })),
    pendingRoles: [...new Set(pending.filter((p) => p.userId === user.id).map((p) => Roles.get(p.roleCode).name))].sort(cmp),
  };
}

export async function listUsers(ctx: RequestContext, businessId: string) {
  const actor = await grants(ctx);
  const businessWide = covers(actor, P.UsersView, businessId, null);
  const visibleStores = new Set(actor.filter((g) => g.businessId === businessId && g.storeId !== null && Roles.get(g.roleCode).permissions.has(P.UsersView))
    .map((g) => g.storeId!));
  if (!businessWide && visibleStores.size === 0) await requirePermission(ctx, P.UsersView, businessId);

  const assignments = await db.collection(C.roleAssignments).where('businessId', '==', businessId).where('revokedAtUtc', '==', null).get();
  const ids = new Set(assignments.docs.map((d) => d.data()).filter((a) => businessWide || (a.storeId && visibleStores.has(a.storeId))).map((a) => a.userId as string));
  const pending = (await pendingGrants(businessId)).filter((p) => businessWide || (p.storeId && visibleStores.has(p.storeId)));
  for (const p of pending) ids.add(p.userId);
  const users = [...(await getAll<UserDoc>(C.users, [...ids])).values()].sort((a, b) => cmp(a.username, b.username));
  const result = [];
  for (const user of users) result.push(await userDto(user, businessId, pending));
  return result;
}

async function ensureStoreInBusiness(businessId: string, storeId: string | null | undefined): Promise<void> {
  if (!storeId) return;
  const store = (await doc(C.stores, storeId).get()).data();
  if (!store || store.businessId !== businessId) throw AppError.notFound('Store');
}

/** Is there anyone, other than the excluded people, who could approve this role grant? */
export async function anyEligibleApprover(tx: Transaction, businessId: string, storeId: string | null, role: RoleDefinition, exclude: string[]): Promise<boolean> {
  const snap = await tx.get(db.collection(C.roleAssignments).where('businessId', '==', businessId).where('revokedAtUtc', '==', null));
  const byUser = new Map<string, ActiveGrant[]>();
  for (const d of snap.docs) {
    const a = d.data();
    if (exclude.includes(a.userId)) continue;
    const list = byUser.get(a.userId) ?? [];
    list.push({ assignmentId: a.id, roleCode: a.roleCode, businessId: a.businessId, storeId: a.storeId ?? null });
    byUser.set(a.userId, list);
  }
  if (byUser.size === 0) return false;
  const users = await tx.getAll(...[...byUser.keys()].map((id) => doc(C.users, id)));
  return users.some((u) => u.exists && u.data()!.isActive && canApproveGrant(byUser.get(u.id)!, businessId, storeId, role));
}

/**
 * Non-privileged grants apply at once. A privileged grant becomes an approval request for another authorised person,
 * unless nobody else could approve it (a single-owner shop): then it applies, with the waiver in the audit trail.
 * Call only after every read of the transaction (it writes).
 */
function grantOrRequest(tx: Transaction, ctx: RequestContext, approverExists: boolean, target: { id: string; username: string }, role: RoleDefinition,
  businessId: string, storeId: string | null, reason: string | null, now: Date) {
  if (role.isPrivileged && approverExists) {
    const id = newId(now);
    tx.create(doc(C.approvalRequests, id), {
      id, businessId, type: RoleGrantApprovalType, summary: `Grant ${role.name} to ${target.username}`,
      payloadJson: JSON.stringify({ userId: target.id, roleCode: role.code, businessId, storeId } satisfies RoleGrantPayload),
      reason: reason?.trim() || null, status: 'pending', requestedByUserId: userId(ctx), requestedAtUtc: now.toISOString(),
      expiresAtUtc: new Date(now.getTime() + settings.approvalLifetimeDays * 86_400_000).toISOString(),
      decidedByUserId: null, decidedAtUtc: null, decisionNote: null, rowVersion: 1,
    });
    audit(tx, ctx, { eventType: 'approval.requested', entityType: 'approval_request', entityId: id, businessId, storeId,
      details: { type: RoleGrantApprovalType, summary: `Grant ${role.name} to ${target.username}`, reason } });
    return { outcome: 'pending_approval', assignmentId: null, approvalRequestId: id,
      message: `${role.name} is a privileged role. The request is waiting for another authorised person to approve it.` };
  }

  const grantId = writeGrant(tx, target.id, role, businessId, storeId, userId(ctx), null, now);
  const waived = role.isPrivileged;
  audit(tx, ctx, { eventType: 'role.granted', entityType: 'role_assignment', entityId: grantId, businessId, storeId,
    details: { user: target.username, role: role.code, approval: waived ? 'waived_no_other_approver' : 'not_required' } });
  return { outcome: 'granted', assignmentId: grantId, approvalRequestId: null,
    message: waived ? `${role.name} granted. No other person could approve it, so this was recorded as a waived approval.` : `${role.name} granted.` };
}

export function writeGrant(tx: Transaction, forUserId: string, role: RoleDefinition, businessId: string, storeId: string | null, grantedBy: string | null,
  approvalRequestId: string | null, now: Date): string {
  if (role.businessWideOnly && storeId) {
    throw AppError.validation('role.business_wide_only', `The ${role.name} role applies to the whole business and cannot be limited to one store.`);
  }
  const id = newId(now);
  tx.create(doc(C.roleAssignments, id), { id, userId: forUserId, roleCode: role.code, businessId, storeId, grantedByUserId: grantedBy,
    grantedAtUtc: now.toISOString(), approvalRequestId, revokedByUserId: null, revokedAtUtc: null });
  return id;
}

export async function createUser_(ctx: RequestContext, businessId: string, body: { username?: string; displayName?: string; temporaryPassword?: string;
  roleCode?: string; storeId?: string | null }) {
  const role = valid(() => Roles.get(body.roleCode ?? ''));
  const storeId = body.storeId ?? null;
  const actor = await grants(ctx);
  await ensureStoreInBusiness(businessId, storeId);
  if (!covers(actor, P.UsersManage, businessId, storeId) || !canGrant(actor, P.RolesAssign, role, businessId, storeId)) {
    await requirePermission(ctx, P.UsersManage, businessId, storeId);
    throw AppError.forbidden(`You cannot create a user with the ${role.name} role.`);
  }
  if (role.businessWideOnly && storeId) {
    throw AppError.validation('role.business_wide_only', `The ${role.name} role applies to the whole business and cannot be limited to one store.`);
  }

  const username = valid(() => rules.normalizeUsername(body.username));
  ensurePasswordPolicy(body.temporaryPassword, username);
  const now = clock.now();
  const user = newUser(username, valid(() => rules.displayName(body.displayName)), await secrets.hashPassword(body.temporaryPassword!), now, true);
  let outcome;
  try {
    outcome = await inTransaction(async (tx) => {
      const approverExists = role.isPrivileged && (await anyEligibleApprover(tx, businessId, storeId, role, [userId(ctx), user.id]));
      createUser(tx, user);
      audit(tx, ctx, { eventType: 'user.created', entityType: 'user', entityId: user.id, businessId, storeId,
        details: { username: user.username, displayName: user.displayName } });
      return grantOrRequest(tx, ctx, approverExists, user, role, businessId, storeId, 'New user', now);
    });
  } catch (e) {
    duplicate(e, 'A user with this username already exists.');
  }
  return { user: await userDto(user, businessId), role: outcome };
}

export async function grantRole(ctx: RequestContext, businessId: string, targetId: string, body: { roleCode?: string; storeId?: string | null; reason?: string | null }) {
  const role = valid(() => Roles.get(body.roleCode ?? ''));
  const storeId = body.storeId ?? null;
  const actor = await grants(ctx);
  await ensureStoreInBusiness(businessId, storeId);
  if (!canGrant(actor, P.RolesAssign, role, businessId, storeId)) {
    await requirePermission(ctx, P.RolesAssign, businessId, storeId);
    throw AppError.forbidden(`You cannot grant the ${role.name} role.`);
  }
  if (targetId === userId(ctx)) throw AppError.forbidden('You cannot change your own roles.');
  return inTransaction(async (tx) => {
    const target = (await tx.get(doc(C.users, targetId))).data() as UserDoc | undefined;
    if (!target) throw AppError.notFound('User');
    const approverExists = role.isPrivileged && (await anyEligibleApprover(tx, businessId, storeId, role, [userId(ctx), targetId]));
    return grantOrRequest(tx, ctx, approverExists, target, role, businessId, storeId, body.reason ?? null, clock.now());
  });
}

async function ensureAnotherOwner(tx: Transaction, businessId: string, excluding: string): Promise<void> {
  const owners = await tx.get(db.collection(C.roleAssignments).where('businessId', '==', businessId).where('roleCode', '==', R.Owner)
    .where('revokedAtUtc', '==', null));
  const others = owners.docs.map((d) => d.data().userId as string).filter((id) => id !== excluding);
  const users = others.length === 0 ? [] : await tx.getAll(...others.map((id) => doc(C.users, id)));
  if (!users.some((u) => u.exists && u.data()!.isActive)) {
    throw AppError.conflict('owner.last', "This is the business's last active owner. Add another owner first.");
  }
}

export async function revokeRole(ctx: RequestContext, businessId: string, targetId: string, assignmentId: string): Promise<void> {
  const actor = await grants(ctx);
  const assignment = (await doc(C.roleAssignments, assignmentId).get()).data();
  if (!assignment || assignment.userId !== targetId || assignment.businessId !== businessId) {
    await requirePermission(ctx, P.RolesAssign, businessId);
    throw AppError.notFound('Role assignment');
  }
  if (!canGrant(actor, P.RolesAssign, Roles.get(assignment.roleCode), businessId, assignment.storeId ?? null)) {
    await requirePermission(ctx, P.RolesAssign, businessId, assignment.storeId ?? null);
    throw AppError.forbidden('You cannot revoke this role.');
  }
  if (targetId === userId(ctx)) throw AppError.forbidden('You cannot change your own roles.');
  await inTransaction(async (tx) => {
    const ref = doc(C.roleAssignments, assignmentId);
    const current = (await tx.get(ref)).data()!;
    if (current.roleCode === R.Owner) await ensureAnotherOwner(tx, businessId, targetId);
    if (current.revokedAtUtc !== null) throw AppError.validation('role.already_revoked', 'This role has already been revoked.');
    tx.update(ref, { revokedByUserId: userId(ctx), revokedAtUtc: clock.now().toISOString() });
    audit(tx, ctx, { eventType: 'role.revoked', entityType: 'role_assignment', entityId: assignmentId, businessId, storeId: current.storeId ?? null,
      details: { userId: targetId, role: current.roleCode } });
  });
}

/** The target, if the actor may manage them: for every grant they hold, the actor has the permission and at least the same. */
async function manageable(ctx: RequestContext, businessId: string, targetId: string, permission: string): Promise<void> {
  const target = await loadGrants(targetId);
  if (!target.some((g) => g.businessId === businessId)) {
    await requirePermission(ctx, P.UsersView, businessId);
    throw AppError.notFound('User');
  }
  if (!canManage(await grants(ctx), target, permission)) {
    await requirePermission(ctx, P.UsersView, businessId);
    throw AppError.forbidden('You cannot manage this user: they hold roles beyond your own permissions or in places you do not manage.');
  }
}

async function sessionsOf(tx: Transaction, forUserId: string) {
  return tx.get(db.collection(C.sessions).where('userId', '==', forUserId).where('revokedAtUtc', '==', null));
}

export async function setActive(ctx: RequestContext, businessId: string, targetId: string, isActive: boolean) {
  await manageable(ctx, businessId, targetId, P.UsersManage);
  if (targetId === userId(ctx)) throw AppError.forbidden('You cannot disable your own account.');
  const ownerships = await db.collection(C.roleAssignments).where('userId', '==', targetId).where('roleCode', '==', R.Owner).where('revokedAtUtc', '==', null).get();
  const user = await inTransaction(async (tx) => {
    const now = clock.now();
    const ref = doc(C.users, targetId);
    const user = (await tx.get(ref)).data() as UserDoc;
    const sessions = await sessionsOf(tx, targetId);
    if (!isActive) {
      for (const o of ownerships.docs) await ensureAnotherOwner(tx, o.data().businessId, targetId);
      for (const s of sessions.docs) tx.update(s.ref, { revokedAtUtc: now.toISOString(), revokedReason: 'user_disabled' });
    }
    const updated = { ...user, isActive, rowVersion: user.rowVersion + 1 };
    tx.update(ref, { isActive, rowVersion: updated.rowVersion });
    audit(tx, ctx, { eventType: isActive ? 'user.enabled' : 'user.disabled', entityType: 'user', entityId: targetId, businessId });
    return updated;
  });
  return userDto(user, businessId);
}

export async function unlock(ctx: RequestContext, businessId: string, targetId: string) {
  await manageable(ctx, businessId, targetId, P.UsersUnlock);
  const user = await inTransaction(async (tx) => {
    const ref = doc(C.users, targetId);
    const user = (await tx.get(ref)).data() as UserDoc;
    const change = { failedLoginCount: 0, lockedUntilUtc: null, rowVersion: user.rowVersion + 1 };
    tx.update(ref, change);
    audit(tx, ctx, { eventType: 'user.unlocked', entityType: 'user', entityId: targetId, businessId });
    return { ...user, ...change };
  });
  return userDto(user, businessId);
}

/** A one-time reset code, shown once to the manager; only its hash is stored. */
export async function issuePasswordReset(ctx: RequestContext, businessId: string, targetId: string) {
  await manageable(ctx, businessId, targetId, P.UsersManage);
  if (targetId === userId(ctx)) throw AppError.validation('reset.self', "Use 'Change password' for your own account.");
  const code = secrets.newHumanCode();
  return inTransaction(async (tx) => {
    const now = clock.now();
    const previous = await tx.get(db.collection(C.passwordResetTokens).where('userId', '==', targetId).where('usedAtUtc', '==', null));
    for (const p of previous.docs) tx.update(p.ref, { usedAtUtc: now.toISOString() });
    const id = newId(now);
    const expiresAtUtc = new Date(now.getTime() + settings.passwordResetMinutes * 60_000).toISOString();
    tx.create(doc(C.passwordResetTokens, id), { id, userId: targetId, tokenHash: secrets.hashHumanCode(code), issuedByUserId: userId(ctx),
      issuedAtUtc: now.toISOString(), expiresAtUtc, usedAtUtc: null });
    audit(tx, ctx, { eventType: 'user.password_reset_issued', entityType: 'user', entityId: targetId, businessId, details: { expires: expiresAtUtc } });
    return { resetCode: code, expiresAtUtc };
  });
}

/** Turns off two-step verification for someone who lost their phone and codes, and signs them out. */
export async function resetMfa(ctx: RequestContext, businessId: string, targetId: string) {
  await manageable(ctx, businessId, targetId, P.UsersManage);
  if (targetId === userId(ctx)) throw AppError.validation('mfa.reset_self', "Use 'My account' to change your own two-step verification.");
  const codes = await db.collection(C.mfaRecoveryCodes).where('userId', '==', targetId).get();
  const user = await inTransaction(async (tx) => {
    const now = clock.now();
    const ref = doc(C.users, targetId);
    const user = (await tx.get(ref)).data() as UserDoc;
    const sessions = await sessionsOf(tx, targetId);
    if (!user.mfaEnabled && !user.mfaPendingSecretProtected) throw AppError.validation('mfa.not_enabled', 'Two-step verification is not turned on for this user.');
    const change = { mfaEnabled: false, mfaSecretProtected: null, mfaPendingSecretProtected: null, mfaLastUsedStep: null, rowVersion: user.rowVersion + 1 };
    tx.update(ref, change);
    for (const c of codes.docs) tx.delete(c.ref);
    for (const s of sessions.docs) tx.update(s.ref, { revokedAtUtc: now.toISOString(), revokedReason: 'mfa_reset' });
    audit(tx, ctx, { eventType: 'user.mfa_reset', entityType: 'user', entityId: targetId, businessId, details: { sessionsRevoked: sessions.size } });
    return { ...user, ...change };
  });
  return userDto(user, businessId);
}

export async function revokeUserSessions(ctx: RequestContext, businessId: string, targetId: string): Promise<number> {
  await manageable(ctx, businessId, targetId, P.UsersManage);
  return inTransaction(async (tx) => {
    const now = clock.now();
    const sessions = await sessionsOf(tx, targetId);
    for (const s of sessions.docs) tx.update(s.ref, { revokedAtUtc: now.toISOString(), revokedReason: 'revoked_by_admin' });
    audit(tx, ctx, { eventType: 'user.sessions_revoked', entityType: 'user', entityId: targetId, businessId, details: { count: sessions.size } });
    return sessions.size;
  });
}
