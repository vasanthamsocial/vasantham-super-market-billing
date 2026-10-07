import type { NextFunction, Request, RequestHandler, Response } from 'express';
import { randomUUID } from 'node:crypto';
import type { RequestContext } from '../core/context.js';
import { AppError, DomainError, problem } from '../core/errors.js';
import { isUuid } from '../core/ids.js';
import { settings } from '../core/settings.js';
import { SessionStates, validateCsrf, validateSession } from '../services/identity.js';

export const SessionCookie = 'sb_session';
export const CsrfCookie = 'sb_csrf';
export const CsrfHeader = 'x-csrf-token';

declare module 'express-serve-static-core' {
  interface Request {
    ctx: RequestContext;
  }
}

export function cookies(req: Request): Record<string, string> {
  const result: Record<string, string> = {};
  for (const part of (req.headers.cookie ?? '').split(';')) {
    const at = part.indexOf('=');
    if (at > 0) result[part.slice(0, at).trim()] = decodeURIComponent(part.slice(at + 1).trim());
  }
  return result;
}

/** The client address: the first X-Forwarded-For entry (set by the web apps' proxy), else the connection. */
function clientIp(req: Request): string | null {
  const forwarded = req.headers['x-forwarded-for'];
  const first = (Array.isArray(forwarded) ? forwarded[0] : forwarded)?.split(',')[0]?.trim();
  return first || req.socket.remoteAddress || null;
}

/** Defensive headers on every response (the API serves JSON only). */
export const securityHeaders: RequestHandler = (_req, res, next) => {
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('X-Frame-Options', 'DENY');
  res.setHeader('Referrer-Policy', 'no-referrer');
  res.setHeader('Cross-Origin-Opener-Policy', 'same-origin');
  res.setHeader('Cross-Origin-Resource-Policy', 'same-origin');
  res.setHeader('Permissions-Policy', 'camera=(), microphone=(), geolocation=(), payment=()');
  res.setHeader('Content-Security-Policy', "default-src 'none'; frame-ancestors 'none'; base-uri 'none'");
  next();
};

// Rate limits: a fixed one-minute window per client, generous for everything, strict for sign-in, MFA, reset and setup.
// Kept in memory per server instance (see the known limitations).
const windows = new Map<string, { start: number; count: number }>();

function allow(key: string, limit: number): boolean {
  const now = Date.now();
  const window = windows.get(key);
  if (!window || now - window.start >= 60_000) {
    windows.set(key, { start: now, count: 1 });
    if (windows.size > 50_000) windows.clear();
    return true;
  }
  window.count++;
  return window.count <= limit;
}

export function rateLimit(kind: 'global' | 'auth'): RequestHandler {
  return (req, res, next) => {
    const limit = kind === 'auth' ? settings.authPermitPerMinute : settings.permitPerMinute;
    if (!allow(`${kind}|${req.ctx.ip ?? 'unknown'}`, limit)) {
      res.status(429).json(problem(429, 'too_many_requests', 'Too many attempts. Wait a minute and try again.'));
      return;
    }
    next();
  };
}

/**
 * Authenticates from the session cookie and builds the request's context. An invalid cookie counts as signed out.
 * Anti-forgery: every state-changing request made with a session must echo the CSRF token in X-CSRF-Token.
 */
export const authenticate: RequestHandler = async (req, res, next) => {
  req.ctx = { correlationId: randomUUID(), ip: clientIp(req), userAgent: req.headers['user-agent'] ?? null, session: null };
  const token = cookies(req)[SessionCookie];
  if (token) req.ctx.session = await validateSession(token);
  if (req.ctx.session && !['GET', 'HEAD', 'OPTIONS'].includes(req.method)) {
    if (!(await validateCsrf(req.ctx.session.id, req.header(CsrfHeader)))) {
      res.status(403).json(problem(403, 'csrf', 'The security token is missing or wrong. Reload the page and try again.'));
      return;
    }
  }
  next();
};

export type Policy = 'anonymous' | 'any' | 'mfa_pending' | 'credential_setup' | 'active';

const allowedStates: Record<Exclude<Policy, 'anonymous' | 'any'>, readonly string[]> = {
  active: [SessionStates.Active],
  mfa_pending: [SessionStates.MfaRequired],
  credential_setup: [SessionStates.Active, SessionStates.PasswordChangeRequired, SessionStates.MfaEnrolmentRequired],
};

/** Every endpoint needs a fully signed-in session unless it says otherwise (as the .NET fallback policy). */
export function requireSession(policy: Policy): RequestHandler {
  return (req, res, next) => {
    if (policy === 'anonymous') return next();
    const session = req.ctx.session;
    if (!session) {
      res.status(401).json(problem(401, 'not_signed_in', 'Please sign in.'));
      return;
    }
    if (policy === 'any' || allowedStates[policy].includes(session.state)) return next();
    const state = session.state;
    res.status(403).json(state === SessionStates.Active
      ? problem(403, 'forbidden', 'You do not have permission to do this.')
      : problem(403, state, `Finish signing in first (${state.replace(/_/g, ' ')}).`));
  };
}

/** Route ids must be UUIDs (the .NET routes' :guid constraint): anything else is not found. */
export const uuidParams: RequestHandler = (req, res, next) => {
  for (const [name, value] of Object.entries(req.params)) {
    if (name.endsWith('Id') && !isUuid(value)) {
      res.status(404).json(problem(404, 'not_found', 'Not found.'));
      return;
    }
  }
  next();
};

export function writeAuthCookies(res: Response, sessionToken: string, csrfToken: string): void {
  const secure = settings.secureCookies ? '; Secure' : '';
  res.append('Set-Cookie', `${SessionCookie}=${sessionToken}; Path=/; HttpOnly; SameSite=Strict${secure}`);
  res.append('Set-Cookie', `${CsrfCookie}=${csrfToken}; Path=/; SameSite=Strict${secure}`);
}

export function clearAuthCookies(res: Response): void {
  const secure = settings.secureCookies ? '; Secure' : '';
  for (const name of [SessionCookie, CsrfCookie]) {
    res.append('Set-Cookie', `${name}=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT; SameSite=Strict${secure}`);
  }
}

/** Expected failures become problem responses; anything else is logged (never its data) and answered as a 500. */
export function errorHandler(error: unknown, req: Request, res: Response, _next: NextFunction): void {
  if (error instanceof AppError) {
    res.status(error.status).json(problem(error.status, error.code, error.message));
    return;
  }
  if (error instanceof DomainError) {
    res.status(400).json(problem(400, error.code, error.message));
    return;
  }
  const status = (error as { status?: number; type?: string }).status;
  if ((error as { type?: string }).type === 'entity.parse.failed') {
    res.status(400).json(problem(400, 'bad_request', 'The request is malformed.'));
    return;
  }
  if (status === 413) {
    res.status(413).json(problem(413, 'too_large', 'The request is too large.'));
    return;
  }
  console.error(`Unhandled error on ${req.method} ${req.path}: ${(error as Error)?.name ?? 'Error'} ${(error as Error)?.message ?? ''}`);
  res.status(500).json(problem(500, 'server_error', 'Something went wrong on the server.'));
}
