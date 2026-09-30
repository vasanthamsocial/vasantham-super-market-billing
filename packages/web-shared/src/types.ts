// Types mirroring the API contracts (SupermarketBilling.Application.Contracts).

export type SessionState = 'active' | 'mfa_required' | 'mfa_enrolment_required' | 'password_change_required';

export interface RoleGrant {
  assignmentId: string;
  roleCode: string;
  roleName: string;
  storeId: string | null;
  storeName: string | null;
}

export interface Membership {
  businessId: string;
  businessCode: string;
  businessName: string;
  roles: RoleGrant[];
  permissions: string[];
}

export interface Me {
  userId: string;
  username: string;
  displayName: string;
  sessionState: SessionState;
  mfaEnabled: boolean;
  mfaRequiredByPolicy: boolean;
  memberships: Membership[];
}

export interface Business {
  id: string;
  code: string;
  legalName: string;
  tradeName: string;
  stateCode: string;
  gstin: string | null;
  address: string | null;
  isActive: boolean;
  requireMfaForPrivilegedUsers: boolean;
  rowVersion: number;
}

export interface Store {
  id: string;
  businessId: string;
  code: string;
  name: string;
  stateCode: string;
  gstin: string | null;
  address: string | null;
  timeZone: string;
  isActive: boolean;
  rowVersion: number;
}

export interface User {
  id: string;
  username: string;
  displayName: string;
  isActive: boolean;
  isLockedOut: boolean;
  mfaEnabled: boolean;
  mustChangePassword: boolean;
  lastLoginAtUtc: string | null;
  roles: RoleGrant[];
  /** Roles requested for this user that are waiting for a second person's approval. */
  pendingRoles: string[];
}

export interface Role {
  code: string;
  name: string;
  isPrivileged: boolean;
  businessWideOnly: boolean;
  permissions: string[];
}

export interface GrantRoleResponse {
  outcome: 'granted' | 'pending_approval';
  assignmentId: string | null;
  approvalRequestId: string | null;
  message: string;
}

export interface Approval {
  id: string;
  businessId: string;
  type: string;
  summary: string;
  reason: string | null;
  status: 'pending' | 'approved' | 'rejected' | 'cancelled' | 'expired';
  requestedByUserId: string;
  requestedBy: string;
  requestedAtUtc: string;
  expiresAtUtc: string;
  decidedByUserId: string | null;
  decidedBy: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  canDecide: boolean;
}

export interface AuditEvent {
  sequence: number;
  occurredAtUtc: string;
  eventType: string;
  entityType: string | null;
  entityId: string | null;
  actorUserId: string | null;
  actor: string | null;
  storeId: string | null;
  payloadJson: string;
}

export interface SessionInfo {
  id: string;
  createdAtUtc: string;
  lastSeenAtUtc: string;
  ipAddress: string | null;
  userAgent: string | null;
  isCurrent: boolean;
}

export const Permission = {
  BusinessesManage: 'businesses.manage',
  StoresView: 'stores.view',
  StoresManage: 'stores.manage',
  UsersView: 'users.view',
  UsersManage: 'users.manage',
  UsersUnlock: 'users.unlock',
  RolesAssign: 'roles.assign',
  ApprovalsView: 'approvals.view',
  ApprovalsDecide: 'approvals.decide',
  AuditView: 'audit.view',
} as const;
