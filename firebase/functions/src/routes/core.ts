import type { Request, Response, Router } from 'express';
import { clock } from '../core/clock.js';
import { C, doc, InstallationId } from '../core/db.js';
import { settings } from '../core/settings.js';
import { clearAuthCookies, rateLimit, requireSession, uuidParams, writeAuthCookies, type Policy } from '../http/pipeline.js';
import * as approvals from '../services/approvals.js';
import * as identity from '../services/identity.js';
import * as organisation from '../services/organisation.js';
import { runSetup, setupStatus } from '../services/setup.js';
import * as users from '../services/users.js';

export const Version = '0.1.0';

type Handler = (req: Request, res: Response) => Promise<unknown>;

/** Registers a route: its sign-in policy, the strict rate limit if asked, and a JSON answer (204 when nothing comes back). */
export function route(router: Router, method: 'get' | 'post' | 'put' | 'delete', path: string, policy: Policy, handler: Handler, strict = false): void {
  const chain = [uuidParams, requireSession(policy), ...(strict ? [rateLimit('auth')] : [])];
  router[method](path, ...chain, async (req: Request, res: Response) => {
    const result = await handler(req, res);
    if (res.headersSent) return;
    if (result === undefined) res.status(204).end();
    else res.json(result);
  });
}

function created(res: Response, location: string, body: unknown): undefined {
  res.status(201).location(location).json(body);
  return undefined;
}

export function coreRoutes(router: Router): void {
  // Health: live = the process answers; ready = Firestore answers.
  router.get('/health/live', (_req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.json({ status: 'Healthy', totalDurationMs: 0, checkedAtUtc: clock.now().toISOString(), checks: [] });
  });
  router.get('/health/ready', async (_req, res) => {
    const started = Date.now();
    let ok = true;
    try {
      await doc(C.installation, InstallationId).get();
    } catch {
      ok = false;
    }
    const ms = Date.now() - started;
    res.setHeader('Cache-Control', 'no-store');
    res.status(ok ? 200 : 503).json({
      status: ok ? 'Healthy' : 'Unhealthy', totalDurationMs: ms, checkedAtUtc: clock.now().toISOString(),
      checks: [
        { name: 'database', status: ok ? 'Healthy' : 'Unhealthy', description: ok ? null : 'Firestore cannot be reached.', durationMs: ms },
        // The .NET API checked for pending database migrations here; Firestore has none (records carry their own shape).
        { name: 'schema', status: 'Healthy', description: 'Firestore: no migrations to apply.', durationMs: 0 },
      ],
    });
  });

  route(router, 'get', '/api/v1/system/info', 'anonymous', async () => ({
    application: 'SupermarketBilling', version: Version, environment: settings.environment, serverTimeUtc: clock.now().toISOString(),
    archiveWebEnabled: settings.archiveWebEnabled,
  }));

  // Setup and sign-in.
  route(router, 'get', '/api/v1/setup/status', 'anonymous', () => setupStatus());
  route(router, 'post', '/api/v1/setup', 'anonymous', (req) => runSetup(req.ctx, req.body ?? {}), true);
  route(router, 'post', '/api/v1/auth/login', 'anonymous', async (req, res) => {
    const outcome = await identity.login(req.ctx, req.body ?? {});
    writeAuthCookies(res, outcome.sessionToken, outcome.csrfToken);
    return outcome.me;
  }, true);
  route(router, 'post', '/api/v1/auth/logout', 'any', async (req, res) => {
    await identity.logout(req.ctx);
    clearAuthCookies(res);
  });
  route(router, 'get', '/api/v1/auth/me', 'any', (req) => identity.me(req.ctx));
  route(router, 'post', '/api/v1/auth/mfa/verify', 'mfa_pending', (req) => identity.verifyMfa(req.ctx, req.body?.code), true);
  route(router, 'post', '/api/v1/auth/mfa/setup', 'credential_setup', (req) => identity.beginMfaSetup(req.ctx));
  route(router, 'post', '/api/v1/auth/mfa/confirm', 'credential_setup', (req) => identity.confirmMfaSetup(req.ctx, req.body?.code));
  route(router, 'post', '/api/v1/auth/mfa/disable', 'active', (req) => identity.disableMfa(req.ctx, req.body ?? {}));
  route(router, 'post', '/api/v1/auth/password/change', 'credential_setup', (req) => identity.changePassword(req.ctx, req.body ?? {}));
  route(router, 'post', '/api/v1/auth/password/reset', 'anonymous', (req) => identity.resetPassword(req.ctx, req.body ?? {}), true);
  route(router, 'get', '/api/v1/auth/sessions', 'active', (req) => identity.listSessions(req.ctx));
  route(router, 'delete', '/api/v1/auth/sessions/:sessionId', 'active', (req) => identity.revokeOwnSession(req.ctx, req.params.sessionId as string));

  // Businesses, stores, users, roles, approvals and audit.
  route(router, 'get', '/api/v1/roles', 'active', async () => users.listRoles());
  route(router, 'get', '/api/v1/businesses', 'active', (req) => organisation.listBusinesses(req.ctx));
  route(router, 'post', '/api/v1/businesses', 'active', async (req, res) => {
    const business = await organisation.createBusiness(req.ctx, req.body ?? {});
    return created(res, `/api/v1/businesses/${business.id}`, business);
  });
  route(router, 'get', '/api/v1/businesses/:businessId', 'active', (req) => organisation.getBusiness(req.ctx, p(req, 'businessId')));
  route(router, 'put', '/api/v1/businesses/:businessId', 'active', (req) => organisation.updateBusiness(req.ctx, p(req, 'businessId'), req.body ?? {}));
  route(router, 'get', '/api/v1/businesses/:businessId/stores', 'active', (req) => organisation.listStores(req.ctx, p(req, 'businessId')));
  route(router, 'post', '/api/v1/businesses/:businessId/stores', 'active', async (req, res) => {
    const store = await organisation.createStore(req.ctx, p(req, 'businessId'), req.body ?? {});
    return created(res, `/api/v1/businesses/${p(req, 'businessId')}/stores/${store.id}`, store);
  });
  route(router, 'put', '/api/v1/businesses/:businessId/stores/:storeId', 'active',
    (req) => organisation.updateStore(req.ctx, p(req, 'businessId'), p(req, 'storeId'), req.body ?? {}));

  const userBase = '/api/v1/businesses/:businessId/users';
  route(router, 'get', userBase, 'active', (req) => users.listUsers(req.ctx, p(req, 'businessId')));
  route(router, 'post', userBase, 'active', async (req, res) => {
    const result = await users.createUser_(req.ctx, p(req, 'businessId'), req.body ?? {});
    return created(res, `/api/v1/businesses/${p(req, 'businessId')}/users/${result.user.id}`, result);
  });
  route(router, 'put', `${userBase}/:userId/active`, 'active', (req) => users.setActive(req.ctx, p(req, 'businessId'), p(req, 'userId'), !!req.body?.isActive));
  route(router, 'post', `${userBase}/:userId/unlock`, 'active', (req) => users.unlock(req.ctx, p(req, 'businessId'), p(req, 'userId')));
  route(router, 'post', `${userBase}/:userId/password-reset`, 'active', (req) => users.issuePasswordReset(req.ctx, p(req, 'businessId'), p(req, 'userId')));
  route(router, 'post', `${userBase}/:userId/mfa-reset`, 'active', (req) => users.resetMfa(req.ctx, p(req, 'businessId'), p(req, 'userId')));
  route(router, 'post', `${userBase}/:userId/sessions/revoke`, 'active',
    async (req) => ({ revoked: await users.revokeUserSessions(req.ctx, p(req, 'businessId'), p(req, 'userId')) }));
  route(router, 'post', `${userBase}/:userId/roles`, 'active', async (req, res) => {
    const outcome = await users.grantRole(req.ctx, p(req, 'businessId'), p(req, 'userId'), req.body ?? {});
    res.status(outcome.outcome === 'pending_approval' ? 202 : 200).json(outcome);
    return undefined;
  });
  route(router, 'delete', `${userBase}/:userId/roles/:assignmentId`, 'active',
    (req) => users.revokeRole(req.ctx, p(req, 'businessId'), p(req, 'userId'), p(req, 'assignmentId')));

  route(router, 'get', '/api/v1/businesses/:businessId/approvals', 'active',
    (req) => approvals.listApprovals(req.ctx, p(req, 'businessId'), typeof req.query.status === 'string' ? req.query.status : undefined));
  route(router, 'post', '/api/v1/approvals/:approvalId/approve', 'active', (req) => approvals.approve(req.ctx, p(req, 'approvalId'), req.body?.note));
  route(router, 'post', '/api/v1/approvals/:approvalId/reject', 'active', (req) => approvals.reject(req.ctx, p(req, 'approvalId'), req.body?.note));
  route(router, 'post', '/api/v1/approvals/:approvalId/cancel', 'active', (req) => approvals.cancel(req.ctx, p(req, 'approvalId')));
  route(router, 'get', '/api/v1/businesses/:businessId/audit', 'active', (req) => approvals.auditTrail(req.ctx, p(req, 'businessId'),
    req.query.before !== undefined ? Number(req.query.before) : undefined, req.query.limit !== undefined ? Number(req.query.limit) : 100));
}

export function p(req: Request, name: string): string {
  return req.params[name] as string;
}
