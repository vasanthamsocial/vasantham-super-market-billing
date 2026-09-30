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

// ---- Catalogue, prices and tax registration ----

export interface UnitInfo {
  id: string;
  code: string;
  name: string;
  decimalPlaces: number;
  isActive: boolean;
}

export interface NamedItem {
  id: string;
  name: string;
  isActive: boolean;
}

export interface CategoryInfo extends NamedItem {
  parentId: string | null;
}

export interface CustomerGroupInfo extends NamedItem {
  code: string;
}

export interface ProductSummary {
  id: string;
  code: string;
  name: string;
  categoryName: string | null;
  brandName: string | null;
  hsnSac: string;
  supplyType: string;
  gstRatePercent: number;
  variantCount: number;
  isActive: boolean;
}

export interface VariantUnitInfo {
  id: string;
  unitId: string;
  unitCode: string;
  factorToBase: number;
  isBase: boolean;
}

export interface BarcodeInfo {
  id: string;
  variantUnitId: string;
  code: string;
  type: string;
  isActive: boolean;
}

export interface MrpInfo {
  id: string;
  variantUnitId: string;
  mrp: number;
  effectiveFrom: string;
  isActive: boolean;
}

export interface VariantInfo {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
  units: VariantUnitInfo[];
  barcodes: BarcodeInfo[];
  mrps: MrpInfo[];
}

export interface ProductDetail {
  id: string;
  code: string;
  name: string;
  printName: string;
  categoryId: string | null;
  brandId: string | null;
  baseUnitId: string;
  baseUnitCode: string;
  hsnSac: string;
  supplyType: string;
  gstRatePercent: number;
  cessRatePercent: number;
  isWeighed: boolean;
  tracksBatches: boolean;
  tracksExpiry: boolean;
  tracksSerials: boolean;
  isActive: boolean;
  rowVersion: number;
  variants: VariantInfo[];
}

export interface PriceRuleInfo {
  id: string;
  variantUnitId: string;
  unitCode: string;
  rateType: string;
  channel: string;
  price: number;
  taxInclusive: boolean;
  mrp: number | null;
  storeId: string | null;
  customerGroupId: string | null;
  membersOnly: boolean;
  minQuantity: number;
  maxQuantity: number | null;
  validFromUtc: string;
  validToUtc: string | null;
  priority: number;
  status: 'PENDING_APPROVAL' | 'ACTIVE' | 'REJECTED' | 'RETIRED';
  note: string | null;
}

export interface PriceQuote {
  ruleId: string | null;
  rateType: string | null;
  unitPrice: number | null;
  taxInclusive: boolean;
  unitPriceInclusive: number | null;
  minimumPriceInclusive: number | null;
  belowMinimum: boolean;
  aboveMrp: boolean;
  taxRatePercent: number;
}

export interface TaxRegistrationInfo {
  id: string;
  mode: 'GST_REGULAR' | 'GST_COMPOSITION' | 'NOT_GST_REGISTERED';
  effectiveFrom: string;
  gstin: string | null;
  reason: string;
  evidenceReference: string | null;
  recordedBy: string;
  approvalRequestId: string | null;
  recordedAtUtc: string;
  isCurrent: boolean;
}

export const CatalogPermission = {
  View: 'catalog.view',
  Manage: 'catalog.manage',
  PricesManage: 'prices.manage',
  TaxReview: 'tax.review',
} as const;

export const TaxModeLabels: Record<string, string> = {
  GST_REGULAR: 'GST regular',
  GST_COMPOSITION: 'GST composition',
  NOT_GST_REGISTERED: 'Not GST registered',
};

export const RateTypeLabels: Record<string, string> = {
  STANDARD: 'Standard',
  STORE: 'Store',
  QUANTITY_SLAB: 'Quantity slab',
  MEMBER: 'Member',
  CUSTOMER_GROUP: 'Customer group',
  PROMOTIONAL: 'Promotional',
  MINIMUM: 'Minimum selling price',
};
