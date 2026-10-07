import type { Transaction } from 'firebase-admin/firestore';
import { audit, grants, userId, type RequestContext } from '../core/context.js';
import { clock } from '../core/clock.js';
import { C, db, doc, getAll, inTransaction } from '../core/db.js';
import { AppError } from '../core/errors.js';
import { canApproveGrant, P, Roles, type ActiveGrant } from '../domain/permissions.js';
import { requirePermission } from './access.js';
import { cmp } from './identity.js';
import { RoleGrantApprovalType, writeGrant } from './users.js';

export interface ApprovalDoc {
  id: string;
  businessId: string;
  type: string;
  summary: string;
  payloadJson: string;
  reason: string | null;
  status: string;
  requestedByUserId: string;
  requestedAtUtc: string;
  expiresAtUtc: string;
  decidedByUserId: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  rowVersion: number;
}

/**
 * One kind of change that needs maker-checker approval: who may decide it, and what approval (or its absence) does.
 * The decision and the change it authorises are written in the same transaction. Later stages register more types.
 */
export interface ApprovalHandler {
  type: string;
  canDecide(actor: readonly ActiveGrant[], actorUserId: string, request: ApprovalDoc): boolean;
  /** Reads first (Firestore transactions), then returns the writes to make. */
  apply(tx: Transaction, ctx: RequestContext, request: ApprovalDoc, now: Date): Promise<void>;
  closedWithoutApproval(tx: Transaction, ctx: RequestContext, request: ApprovalDoc, now: Date): Promise<void>;
}

const handlers = new Map<string, ApprovalHandler>();

export function registerApprovalHandler(handler: ApprovalHandler): void {
  handlers.set(handler.type, handler);
}

function handler(request: ApprovalDoc): ApprovalHandler {
  const found = handlers.get(request.type);
  if (!found) throw AppError.validation('approval.unknown_type', `No handler for approval type '${request.type}'.`);
  return found;
}

function canDecide(actor: readonly ActiveGrant[], actorUserId: string, request: ApprovalDoc): boolean {
  return request.requestedByUserId !== actorUserId && handler(request).canDecide(actor, actorUserId, request);
}

/** Granting a privileged role: the decider holds approvals.decide business-wide and every permission the role gives, and is not the person. */
registerApprovalHandler({
  type: RoleGrantApprovalType,
  canDecide(actor, actorUserId, request) {
    const payload = JSON.parse(request.payloadJson);
    return payload.userId !== actorUserId && canApproveGrant(actor, request.businessId, payload.storeId ?? null, Roles.get(payload.roleCode));
  },
  async apply(tx, ctx, request, now) {
    const payload = JSON.parse(request.payloadJson);
    const user = (await tx.get(doc(C.users, payload.userId))).data();
    if (!user || !user.isActive) throw AppError.conflict('approval.user_inactive', 'The user no longer exists or is disabled. Reject this request instead.');
    const grantId = writeGrant(tx, payload.userId, Roles.get(payload.roleCode), payload.businessId, payload.storeId ?? null, request.requestedByUserId, request.id, now);
    audit(tx, ctx, { eventType: 'role.granted', entityType: 'role_assignment', entityId: grantId, businessId: payload.businessId, storeId: payload.storeId ?? null,
      details: { user: user.username, role: payload.roleCode, approval: request.id } });
  },
  async closedWithoutApproval() {},
});

export async function listApprovals(ctx: RequestContext, businessId: string, status: string | undefined) {
  await requirePermission(ctx, P.ApprovalsView, businessId);
  const actor = await grants(ctx);
  let query = db.collection(C.approvalRequests).where('businessId', '==', businessId);
  if (status?.trim()) query = query.where('status', '==', status);
  const rows = (await query.get()).docs.map((d) => d.data() as ApprovalDoc).sort((a, b) => cmp(b.requestedAtUtc, a.requestedAtUtc)).slice(0, 200);
  const users = await getAll(C.users, rows.flatMap((r) => [r.requestedByUserId, r.decidedByUserId ?? '']));
  const now = clock.now().toISOString();
  return rows.map((a) => {
    const effective = a.status === 'pending' && now >= a.expiresAtUtc ? 'expired' : a.status;
    return {
      id: a.id, businessId: a.businessId, type: a.type, summary: a.summary, reason: a.reason, status: effective, requestedByUserId: a.requestedByUserId,
      requestedBy: users.get(a.requestedByUserId)?.displayName ?? '?', requestedAtUtc: a.requestedAtUtc, expiresAtUtc: a.expiresAtUtc,
      decidedByUserId: a.decidedByUserId, decidedBy: a.decidedByUserId ? users.get(a.decidedByUserId)?.displayName ?? null : null, decidedAtUtc: a.decidedAtUtc,
      decisionNote: a.decisionNote, canDecide: effective === 'pending' && canDecide(actor, userId(ctx), a),
    };
  });
}

function ensurePending(request: ApprovalDoc, now: Date): void {
  if (request.status !== 'pending') throw AppError.validation('approval.not_pending', `This request is already ${request.status}.`);
  if (now.toISOString() >= request.expiresAtUtc) throw AppError.validation('approval.expired', 'This request has expired. Submit a new one.');
}

async function forDecision(ctx: RequestContext, approvalId: string): Promise<ApprovalDoc> {
  const request = (await doc(C.approvalRequests, approvalId).get()).data() as ApprovalDoc | undefined;
  if (!request) throw AppError.notFound('Approval request');
  await requirePermission(ctx, P.ApprovalsDecide, request.businessId);
  if (request.requestedByUserId === userId(ctx)) {
    throw AppError.forbidden('You cannot approve or reject your own request. Another authorised person must decide.');
  }
  if (!canDecide(await grants(ctx), userId(ctx), request)) {
    throw AppError.forbidden('You are not allowed to decide this request (it needs a permission you do not hold, or it benefits you).');
  }
  return request;
}

async function decide(ctx: RequestContext, approvalId: string, status: 'approved' | 'rejected', note: string | null | undefined): Promise<void> {
  await forDecision(ctx, approvalId);
  if (status === 'rejected' && !note?.trim()) throw AppError.validation('approval.reason_required', 'A reason is required to reject a request.');
  await inTransaction(async (tx) => {
    const now = clock.now();
    const ref = doc(C.approvalRequests, approvalId);
    const request = (await tx.get(ref)).data() as ApprovalDoc;
    ensurePending(request, now);
    // The handler reads first; the decision is written with what it applies.
    if (status === 'approved') await handler(request).apply(tx, ctx, request, now);
    else await handler(request).closedWithoutApproval(tx, ctx, request, now);
    tx.update(ref, { status, decidedByUserId: userId(ctx), decidedAtUtc: now.toISOString(), decisionNote: note?.trim() || null, rowVersion: request.rowVersion + 1 });
    audit(tx, ctx, { eventType: `approval.${status}`, entityType: 'approval_request', entityId: approvalId, businessId: request.businessId,
      details: { type: request.type, summary: request.summary, note: note ?? null } });
  });
}

export const approve = (ctx: RequestContext, id: string, note: string | null | undefined) => decide(ctx, id, 'approved', note);
export const reject = (ctx: RequestContext, id: string, note: string | null | undefined) => decide(ctx, id, 'rejected', note);

export async function cancel(ctx: RequestContext, approvalId: string): Promise<void> {
  await inTransaction(async (tx) => {
    const now = clock.now();
    const ref = doc(C.approvalRequests, approvalId);
    const request = (await tx.get(ref)).data() as ApprovalDoc | undefined;
    if (!request || request.requestedByUserId !== userId(ctx)) throw AppError.notFound('Approval request');
    ensurePending(request, now);
    await handler(request).closedWithoutApproval(tx, ctx, request, now);
    tx.update(ref, { status: 'cancelled', decidedAtUtc: now.toISOString(), rowVersion: request.rowVersion + 1 });
    audit(tx, ctx, { eventType: 'approval.cancelled', entityType: 'approval_request', entityId: approvalId, businessId: request.businessId });
  });
}

/** The business's audit trail, newest first, paged by sequence. */
export async function auditTrail(ctx: RequestContext, businessId: string, before: number | undefined, limit: number) {
  await requirePermission(ctx, P.AuditView, businessId);
  let query = db.collection(C.auditEvents).where('businessId', '==', businessId).orderBy('sequence', 'desc');
  if (before !== undefined) query = query.where('sequence', '<', before);
  const rows = (await query.limit(Math.min(Math.max(limit, 1), 500)).get()).docs.map((d) => d.data());
  const users = await getAll(C.users, rows.map((r) => r.actorUserId).filter(Boolean));
  return rows.map((e) => ({
    sequence: e.sequence, occurredAtUtc: e.occurredAtUtc, eventType: e.eventType, entityType: e.entityType, entityId: e.entityId, actorUserId: e.actorUserId,
    actor: e.actorUserId ? users.get(e.actorUserId)?.displayName ?? null : null, storeId: e.storeId, payloadJson: e.payloadJson,
  }));
}
