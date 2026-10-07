import { audit, type RequestContext } from '../core/context.js';
import { clock } from '../core/clock.js';
import { C, doc, inTransaction, InstallationId } from '../core/db.js';
import { AppError } from '../core/errors.js';
import { newId } from '../core/ids.js';
import * as secrets from '../core/secrets.js';
import { R } from '../domain/permissions.js';
import * as rules from '../domain/rules.js';
import { createUser, ensurePasswordPolicy, newUser, valid } from './identity.js';
import { createBusinessRecords, createStoreRecords, newBusiness, newStore, taxModeFor, type BusinessInput, type StoreInput } from './organisation.js';

/**
 * The first company, business, store and owner of this installation. Allowed once, and only with the setup code: the
 * SB_SETUP_CODE secret, which only whoever controls the Firebase project knows (so the first visitor to the web page
 * cannot claim the installation). One Firebase project is one company, like an in-store server ("edge").
 */
export async function setupStatus(): Promise<{ setupRequired: boolean; deploymentMode: string }> {
  const installation = (await doc(C.installation, InstallationId).get()).data();
  return { setupRequired: !installation?.tenantId, deploymentMode: 'edge' };
}

export interface SetupRequest {
  setupCode?: string;
  companyCode?: string;
  business?: BusinessInput;
  store?: StoreInput;
  ownerUsername?: string;
  ownerDisplayName?: string;
  ownerPassword?: string;
}

function setupCodeMatches(code: string | undefined): boolean {
  const expected = process.env.SB_SETUP_CODE;
  if (!expected || !code?.trim()) return false;
  return secrets.sameHash(secrets.hashHumanCode(expected), secrets.hashHumanCode(code));
}

function ensureNotDone(installation: { tenantId?: string } | undefined, request: SetupRequest): void {
  if (installation?.tenantId) throw AppError.conflict('setup.already_completed', 'Initial setup has already been completed.');
  if (!setupCodeMatches(request.setupCode)) {
    throw new AppError('forbidden', 'setup.code_invalid', 'The setup code is not correct. It is the SB_SETUP_CODE secret of this Firebase project.');
  }
}

export async function runSetup(ctx: RequestContext, request: SetupRequest): Promise<void> {
  // Done or wrong code is answered before the form is checked (and again inside the transaction).
  ensureNotDone((await doc(C.installation, InstallationId).get()).data(), request);
  const now = clock.now();
  const username = valid(() => rules.normalizeUsername(request.ownerUsername));
  ensurePasswordPolicy(request.ownerPassword, username);
  const company = valid(() => ({ code: rules.companyCode(request.companyCode), name: rules.required(request.business?.legalName, 'tenant.name_invalid',
    'Company name is required (max 200 characters).', 200) }));
  const business = newBusiness(request.business ?? {}, now);
  const store = newStore(business.id, request.store ?? {}, now);
  const owner = newUser(username, valid(() => rules.displayName(request.ownerDisplayName)), await secrets.hashPassword(request.ownerPassword!), now, false);
  const mode = taxModeFor(request.business ?? {});

  await inTransaction(async (tx) => {
    // Inside the transaction, so two attempts at once cannot both set up the installation.
    const installationRef = doc(C.installation, InstallationId);
    ensureNotDone((await tx.get(installationRef)).data(), request);

    const tenantId = newId(now);
    tx.create(doc(C.tenants, tenantId), { id: tenantId, code: company.code, name: company.name, isActive: true, createdAtUtc: now.toISOString() });
    tx.set(installationRef, { id: InstallationId, tenantId, createdAtUtc: now.toISOString() });
    createUser(tx, owner);
    const grantId = createBusinessRecords(tx, business, mode, owner.id, null, now);
    createStoreRecords(tx, store);
    const as = { ...ctx, session: null };
    audit(tx, as, { eventType: 'setup.completed', entityType: 'tenant', entityId: tenantId, businessId: business.id, actorUserId: owner.id,
      details: { company: company.code, business: business.code, store: store.code, owner: owner.username, mode: 'edge', taxRegistration: mode } });
    audit(tx, as, { eventType: 'role.granted', entityType: 'role_assignment', entityId: grantId, businessId: business.id, actorUserId: owner.id,
      details: { user: owner.username, role: R.Owner, via: 'initial_setup' } });
  });
}
