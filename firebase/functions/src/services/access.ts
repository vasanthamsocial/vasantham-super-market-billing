import { AppError } from '../core/errors.js';
import { grants, type RequestContext } from '../core/context.js';
import { covers, P, Roles, type ActiveGrant } from '../domain/permissions.js';

export function businessesWith(list: readonly ActiveGrant[], permission: string): string[] {
  return [...new Set(list.filter((g) => Roles.get(g.roleCode).permissions.has(permission)).map((g) => g.businessId))];
}

export async function hasPermission(ctx: RequestContext, permission: string, businessId: string, storeId: string | null): Promise<boolean> {
  return covers(await grants(ctx), permission, businessId, storeId);
}

/**
 * Throws NotFound when the caller has no access to the business at all (so other businesses' existence is not
 * revealed), Forbidden when they can see it but lack the permission.
 */
export async function requirePermission(ctx: RequestContext, permission: string, businessId: string, storeId: string | null = null): Promise<void> {
  const list = await grants(ctx);
  if (covers(list, permission, businessId, storeId)) return;
  throw businessesWith(list, P.StoresView).includes(businessId) ? AppError.forbidden() : AppError.notFound('Business');
}
