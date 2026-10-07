import type { Transaction } from 'firebase-admin/firestore';
import { audit, grants, userId, type RequestContext } from '../core/context.js';
import { businessDate, clock, DefaultTimeZone } from '../core/clock.js';
import { C, db, doc, inTransaction, uniqueKey } from '../core/db.js';
import { AppError } from '../core/errors.js';
import { newId } from '../core/ids.js';
import { settings } from '../core/settings.js';
import { P, R } from '../domain/permissions.js';
import * as rules from '../domain/rules.js';
import { businessesWith, hasPermission, requirePermission } from './access.js';
import { cmp, valid } from './identity.js';

export interface BusinessDoc {
  id: string;
  code: string;
  legalName: string;
  tradeName: string;
  stateCode: string;
  gstin: string | null;
  address: string | null;
  isActive: boolean;
  requireMfaForPrivilegedUsers: boolean;
  requirePriceApproval: boolean;
  createdAtUtc: string;
  rowVersion: number;
}

export interface StoreDoc {
  id: string;
  businessId: string;
  code: string;
  name: string;
  stateCode: string;
  gstin: string | null;
  address: string | null;
  timeZone: string;
  isActive: boolean;
  createdAtUtc: string;
  rowVersion: number;
}

export interface BusinessInput {
  code?: string;
  legalName?: string;
  tradeName?: string | null;
  stateCode?: string;
  gstin?: string | null;
  address?: string | null;
  taxRegistrationMode?: string | null;
}

export interface StoreInput {
  code?: string;
  name?: string;
  stateCode?: string;
  gstin?: string | null;
  address?: string | null;
  isActive?: boolean;
  rowVersion?: number;
}

/** The business's editable details, validated as the .NET Business.Update did. */
function businessFields(input: BusinessInput & { requireMfaForPrivilegedUsers?: boolean }) {
  const legalName = rules.required(input.legalName, 'business.legal_name_required', 'Legal name is required.', 200);
  const stateCode = rules.stateCode(input.stateCode);
  return {
    legalName,
    tradeName: input.tradeName?.trim() ? rules.required(input.tradeName, 'business.trade_name_invalid', 'Trade name is too long.', 200) : legalName,
    stateCode,
    gstin: input.gstin?.trim() ? rules.validateGstin(input.gstin, stateCode) : null,
    address: rules.optional(input.address, 'business.address_invalid', 'Address is too long.', 500),
    requireMfaForPrivilegedUsers: input.requireMfaForPrivilegedUsers ?? false,
  };
}

function storeFields(input: StoreInput) {
  const stateCode = rules.stateCode(input.stateCode);
  return {
    name: rules.required(input.name, 'store.name_required', 'Store name is required (max 120 characters).', 120),
    stateCode,
    gstin: input.gstin?.trim() ? rules.validateGstin(input.gstin, stateCode) : null,
    address: rules.optional(input.address, 'store.address_invalid', 'Address is too long.', 500),
  };
}

export function newBusiness(input: BusinessInput, now: Date): BusinessDoc {
  return valid(() => ({
    id: newId(now),
    code: rules.businessCode(input.code),
    ...businessFields(input),
    isActive: true,
    requirePriceApproval: false,
    createdAtUtc: now.toISOString(),
    rowVersion: 1,
  }));
}

export function newStore(businessId: string, input: StoreInput, now: Date): StoreDoc {
  return valid(() => ({
    id: newId(now),
    businessId,
    code: rules.storeCode(input.code),
    ...storeFields(input),
    timeZone: DefaultTimeZone,
    isActive: true,
    createdAtUtc: now.toISOString(),
    rowVersion: 1,
  }));
}

/** The mode chosen at setup; without one, a GSTIN implies regular GST and no GSTIN means not registered. */
export function taxModeFor(input: BusinessInput): string {
  return input.taxRegistrationMode ?? (input.gstin?.trim() ? rules.TaxModes.GstRegular : rules.TaxModes.NotGstRegistered);
}

/**
 * Everything a new business starts with, written in the caller's transaction: the business (its code claimed), the
 * owner's grant, the default units, the initial tax registration, inventory (FIFO) and purchase settings.
 */
export function createBusinessRecords(tx: Transaction, business: BusinessDoc, mode: string, ownerId: string, grantedBy: string | null, now: Date): string {
  const gstin = valid(() => rules.registrationGstin(mode, business.gstin));
  tx.create(doc(C.uniques, uniqueKey('business', business.code)), { businessId: business.id });
  tx.create(doc(C.businesses, business.id), business);
  const grantId = newId(now);
  tx.create(doc(C.roleAssignments, grantId), {
    id: grantId, userId: ownerId, roleCode: R.Owner, businessId: business.id, storeId: null, grantedByUserId: grantedBy, grantedAtUtc: now.toISOString(),
    approvalRequestId: null, revokedByUserId: null, revokedAtUtc: null,
  });
  for (const [code, name, decimals] of rules.DefaultUnits) {
    const id = newId(now);
    tx.create(doc(C.units, id), { id, businessId: business.id, code, name, decimalPlaces: decimals, isActive: true });
  }
  const registrationId = newId(now);
  tx.create(doc(C.taxRegistrations, registrationId), {
    id: registrationId, businessId: business.id, mode, effectiveFrom: businessDate(now), gstin, reason: 'Initial registration recorded at setup',
    evidenceReference: null, recordedByUserId: ownerId, approvalRequestId: null, recordedAtUtc: now.toISOString(),
  });
  tx.create(doc(C.inventorySettings, business.id), { businessId: business.id, valuationMethod: 'FIFO' });
  tx.create(doc(C.purchaseSettings, business.id), { businessId: business.id, costReasonThresholdPercent: 5, costApprovalThresholdPercent: 15, allowLossLeader: false });
  return grantId;
}

export function createStoreRecords(tx: Transaction, store: StoreDoc): void {
  tx.create(doc(C.uniques, uniqueKey('store', store.businessId, store.code)), { storeId: store.id });
  tx.create(doc(C.stores, store.id), store);
}

export function businessDto(b: BusinessDoc) {
  return {
    id: b.id, code: b.code, legalName: b.legalName, tradeName: b.tradeName, stateCode: b.stateCode, gstin: b.gstin, address: b.address, isActive: b.isActive,
    requireMfaForPrivilegedUsers: b.requireMfaForPrivilegedUsers, rowVersion: b.rowVersion, requirePriceApproval: b.requirePriceApproval,
  };
}

export function storeDto(s: StoreDoc) {
  return {
    id: s.id, businessId: s.businessId, code: s.code, name: s.name, stateCode: s.stateCode, gstin: s.gstin, address: s.address, timeZone: s.timeZone,
    isActive: s.isActive, rowVersion: s.rowVersion,
  };
}

function stale(): AppError {
  return AppError.conflict('concurrency', 'This record was changed by someone else. Reload it and try again.');
}

/** A unique key already taken shows as a duplicate (the .NET API's unique-index answer). */
export function duplicate(e: unknown, message: string): never {
  if ((e as { code?: number }).code === 6) throw AppError.conflict('duplicate', message);
  throw e;
}

// Businesses

export async function listBusinesses(ctx: RequestContext) {
  const visible = businessesWith(await grants(ctx), P.StoresView);
  const found = visible.length === 0 ? [] : await db.getAll(...visible.map((id) => doc(C.businesses, id)));
  return found.filter((d) => d.exists).map((d) => d.data() as BusinessDoc).sort((a, b) => cmp(a.code, b.code)).map(businessDto);
}

export async function getBusiness(ctx: RequestContext, businessId: string) {
  const visible = businessesWith(await grants(ctx), P.StoresView);
  const business = visible.includes(businessId) ? (await doc(C.businesses, businessId).get()).data() as BusinessDoc | undefined : undefined;
  if (!business) throw AppError.notFound('Business');
  return businessDto(business);
}

/** Adds another legal business (licence permitting). The creator becomes its owner. */
export async function createBusiness(ctx: RequestContext, input: BusinessInput) {
  if (businessesWith(await grants(ctx), P.BusinessesCreate).length === 0) throw AppError.forbidden('Only an owner can add a business.');
  const now = clock.now();
  const business = newBusiness(input, now);
  try {
    await inTransaction(async (tx) => {
      const count = (await tx.get(db.collection(C.businesses).count())).data().count;
      if (count >= settings.maxBusinesses) {
        throw AppError.conflict('license.max_businesses', `This installation is licensed for ${settings.maxBusinesses} business(es). Contact your supplier to add more.`);
      }
      const grantId = createBusinessRecords(tx, business, taxModeFor(input), userId(ctx), userId(ctx), now);
      audit(tx, ctx, { eventType: 'business.created', entityType: 'business', entityId: business.id, businessId: business.id,
        details: { code: business.code, legalName: business.legalName, gstin: business.gstin } });
      audit(tx, ctx, { eventType: 'role.granted', entityType: 'role_assignment', entityId: grantId, businessId: business.id, details: { role: R.Owner, via: 'business_created' } });
    });
  } catch (e) {
    duplicate(e, 'A business with this code already exists.');
  }
  return businessDto(business);
}

export async function updateBusiness(ctx: RequestContext, businessId: string, input: BusinessInput & { requireMfaForPrivilegedUsers?: boolean; requirePriceApproval?: boolean; rowVersion?: number }) {
  await requirePermission(ctx, P.BusinessesManage, businessId);
  const fields = valid(() => businessFields(input));
  return inTransaction(async (tx) => {
    const ref = doc(C.businesses, businessId);
    const before = (await tx.get(ref)).data() as BusinessDoc | undefined;
    if (!before) throw AppError.notFound('Business');
    if (input.rowVersion !== before.rowVersion) throw stale();
    const after: BusinessDoc = { ...before, ...fields, requirePriceApproval: input.requirePriceApproval ?? false, rowVersion: before.rowVersion + 1 };
    tx.set(ref, after);
    audit(tx, ctx, { eventType: 'business.updated', entityType: 'business', entityId: businessId, businessId, details: { before: businessDto(before), after: businessDto(after) } });
    return businessDto(after);
  });
}

// Stores

export async function listStores(ctx: RequestContext, businessId: string) {
  const businessWide = await hasPermission(ctx, P.StoresView, businessId, null);
  const snap = await db.collection(C.stores).where('businessId', '==', businessId).get();
  let stores = snap.docs.map((d) => d.data() as StoreDoc).sort((a, b) => cmp(a.code, b.code));
  if (!businessWide) {
    // Store-limited staff see only their own stores.
    const visible: StoreDoc[] = [];
    for (const s of stores) if (await hasPermission(ctx, P.StoresView, businessId, s.id)) visible.push(s);
    stores = visible;
    if (stores.length === 0) throw AppError.notFound('Business');
  }
  return stores.map(storeDto);
}

export async function createStore(ctx: RequestContext, businessId: string, input: StoreInput) {
  await requirePermission(ctx, P.StoresManage, businessId);
  const store = newStore(businessId, input, clock.now());
  try {
    await inTransaction(async (tx) => {
      createStoreRecords(tx, store);
      audit(tx, ctx, { eventType: 'store.created', entityType: 'store', entityId: store.id, businessId, storeId: store.id, details: { code: store.code, name: store.name } });
    });
  } catch (e) {
    duplicate(e, 'A store with this code already exists in the business.');
  }
  return storeDto(store);
}

export async function updateStore(ctx: RequestContext, businessId: string, storeId: string, input: StoreInput) {
  await requirePermission(ctx, P.StoresManage, businessId, storeId);
  const fields = valid(() => storeFields(input));
  return inTransaction(async (tx) => {
    const ref = doc(C.stores, storeId);
    const before = (await tx.get(ref)).data() as StoreDoc | undefined;
    if (!before || before.businessId !== businessId) throw AppError.notFound('Store');
    if (input.rowVersion !== before.rowVersion) throw stale();
    const after: StoreDoc = { ...before, ...fields, isActive: input.isActive ?? before.isActive, rowVersion: before.rowVersion + 1 };
    tx.set(ref, after);
    audit(tx, ctx, { eventType: 'store.updated', entityType: 'store', entityId: storeId, businessId, storeId, details: { before: storeDto(before), after: storeDto(after) } });
    return storeDto(after);
  });
}
