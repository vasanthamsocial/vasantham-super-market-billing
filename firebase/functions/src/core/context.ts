import type { Transaction, WriteBatch } from 'firebase-admin/firestore';
import { clock } from './clock.js';
import { C, db, doc } from './db.js';
import { newId } from './ids.js';
import { type ActiveGrant } from '../domain/permissions.js';

/** Who is asking, for one request: their session, and their grants once loaded. */
export interface RequestContext {
  correlationId: string;
  ip: string | null;
  userAgent: string | null;
  session: { id: string; userId: string; username: string; state: string } | null;
  grants?: ActiveGrant[];
}

export function userId(ctx: RequestContext): string {
  if (!ctx.session) throw new Error('The request is not authenticated.');
  return ctx.session.userId;
}

/** Active role grants of a user, in active businesses. */
export async function loadGrants(forUserId: string): Promise<ActiveGrant[]> {
  const snap = await db.collection(C.roleAssignments).where('userId', '==', forUserId).where('revokedAtUtc', '==', null).get();
  const rows = snap.docs.map((d) => d.data());
  const businessIds = [...new Set(rows.map((r) => r.businessId as string))];
  const active = new Set<string>();
  if (businessIds.length > 0) {
    const businesses = await db.getAll(...businessIds.map((id) => doc(C.businesses, id)));
    for (const b of businesses) if (b.exists && b.data()?.isActive) active.add(b.id);
  }
  return rows.filter((r) => active.has(r.businessId)).map((r) => ({
    assignmentId: r.id as string,
    roleCode: r.roleCode as string,
    businessId: r.businessId as string,
    storeId: (r.storeId as string | null) ?? null,
  }));
}

export async function grants(ctx: RequestContext): Promise<ActiveGrant[]> {
  if (!ctx.session) return [];
  ctx.grants ??= await loadGrants(ctx.session.userId);
  return ctx.grants;
}

// Audit trail: immutable events, newest first by sequence. Payloads never hold passwords, codes or tokens.

let lastSequence = 0;

/** Increasing within this instance, and time-ordered across instances (milliseconds x 1000 + a counter). */
function nextSequence(): number {
  const candidate = clock.now().getTime() * 1000;
  lastSequence = candidate > lastSequence ? candidate : lastSequence + 1;
  return lastSequence;
}

export interface AuditEntry {
  eventType: string;
  entityType?: string | null;
  entityId?: string | null;
  businessId?: string | null;
  storeId?: string | null;
  details?: unknown;
  actorUserId?: string | null;
}

/** Adds the event to the same transaction or batch as the change it records. */
export function audit(writer: Transaction | WriteBatch, ctx: RequestContext, entry: AuditEntry): void {
  const now = clock.now();
  const id = newId(now);
  const event = {
    id,
    sequence: nextSequence(),
    occurredAtUtc: now.toISOString(),
    eventType: entry.eventType,
    entityType: entry.entityType ?? null,
    entityId: entry.entityId ?? null,
    actorUserId: entry.actorUserId !== undefined ? entry.actorUserId : ctx.session?.userId ?? null,
    businessId: entry.businessId ?? null,
    storeId: entry.storeId ?? null,
    correlationId: ctx.correlationId,
    payloadJson: JSON.stringify({ ip: ctx.ip, details: entry.details ?? null }),
  };
  (writer as Transaction).create(doc(C.auditEvents, id), event);
}
