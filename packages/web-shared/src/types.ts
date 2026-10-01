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

export const StockPermission = {
  View: 'stock.view',
  Adjust: 'stock.adjust',
  Transfer: 'stock.transfer',
  Count: 'stock.count',
  Settings: 'stock.settings',
  NegativeOverride: 'stock.negative_override',
} as const;

export const StockDocumentTypeLabels: Record<string, string> = {
  OPENING: 'Opening stock',
  ADJUSTMENT: 'Adjustment',
  DAMAGE: 'Damage',
  WASTAGE: 'Wastage',
  TRANSFER: 'Transfer to another store',
  COUNT: 'Physical count',
};

export const MovementTypeLabels: Record<string, string> = {
  OPENING: 'Opening',
  ADJUSTMENT_IN: 'Adjustment in',
  ADJUSTMENT_OUT: 'Adjustment out',
  DAMAGE: 'Damage',
  WASTAGE: 'Wastage',
  TRANSFER_OUT: 'Transfer out',
  TRANSFER_IN: 'Transfer in',
  COUNT_GAIN: 'Count gain',
  COUNT_LOSS: 'Count loss',
  RECEIPT: 'Purchase receipt',
  PURCHASE_RETURN: 'Purchase return',
  SALE: 'Sale',
  SALE_RETURN: 'Sale return',
};

export const ValuationMethodLabels: Record<string, string> = {
  FIFO: 'FIFO (first in, first out)',
  FEFO: 'FEFO (first expiry, first out)',
  WEIGHTED_AVERAGE: 'Weighted average',
};

export const NegativeStockModeLabels: Record<string, string> = {
  DISABLED: 'Not allowed',
  WARN_OVERRIDE: 'Allowed with manager override',
  ENABLED_WITH_LIMIT: 'Allowed down to a limit',
};

export interface StockOnHand {
  variantId: string;
  variantCode: string;
  variantName: string;
  unitCode: string;
  quantity: number;
  averageCost: number;
  value: number;
  minimumQuantity: number | null;
  reorderQuantity: number | null;
  isLow: boolean;
  isNegative: boolean;
}

export interface StockLedgerEntry {
  sequence: number;
  occurredAtUtc: string;
  businessDate: string;
  movementType: string;
  quantity: number;
  unitCost: number;
  value: number;
  balanceAfter: number;
  batchNumber: string | null;
  documentType: string;
  documentId: string;
  documentNumber: string | null;
  postedBy: string;
}

export interface StockMovement {
  sequence: number;
  storeId: string;
  variantId: string;
  variantName: string;
  batchNumber: string | null;
  movementType: string;
  quantity: number;
  unitCost: number;
  value: number;
  balanceAfter: number;
}

export interface StockDocument {
  id: string;
  type: string;
  number: string;
  storeId: string;
  targetStoreId: string | null;
  businessDate: string;
  reason: string;
  note: string | null;
  negativeStockOverride: boolean;
  postedBy: string;
  postedAtUtc: string;
  movements: StockMovement[];
}

export interface StockDocumentSummary {
  id: string;
  type: string;
  number: string;
  storeId: string;
  targetStoreId: string | null;
  businessDate: string;
  reason: string;
  postedBy: string;
  postedAtUtc: string;
}

export interface BatchStock {
  batchId: string;
  variantId: string;
  variantName: string;
  batchNumber: string;
  expiresOn: string | null;
  daysToExpiry: number | null;
  isExpired: boolean;
  quantity: number;
}

export interface StockValuation {
  storeId: string;
  storeName: string;
  valuationMethod: string;
  items: number;
  negativeItems: number;
  totalValue: number;
}

export interface InventorySettings {
  valuationMethod: string;
  valuationLocked: boolean;
}

export interface NegativeStockRule {
  id: string;
  storeId: string | null;
  productId: string | null;
  mode: string;
  limitQuantity: number | null;
  reason: string;
  createdBy: string;
  createdAtUtc: string;
  isActive: boolean;
  approvalRequestId: string | null;
  supersededAtUtc: string | null;
}

export interface SetNegativeStockRuleResult {
  outcome: 'active' | 'pending_approval';
  ruleId: string;
  approvalRequestId: string | null;
  message: string;
}

export const SalesPermission = {
  Bill: 'pos.bill',
  PriceOverride: 'pos.price_override',
  Discount: 'pos.discount',
  CountersManage: 'counters.manage',
  View: 'sales.view',
} as const;

export const InvoiceKindLabels: Record<string, string> = {
  TAX_INVOICE: 'Tax invoice',
  BILL_OF_SUPPLY: 'Bill of supply',
  INVOICE: 'Invoice',
};

export const PaymentMethodLabels: Record<string, string> = {
  CASH: 'Cash',
  CARD: 'Card',
  UPI: 'UPI',
  WALLET: 'Wallet',
};

export interface BarcodeLookup {
  productId: string;
  productName: string;
  printName: string;
  variantId: string;
  variantName: string;
  variantUnitId: string;
  unitCode: string;
  factorToBase: number;
  barcode: string;
  supplyType: string;
  gstRatePercent: number;
  cessRatePercent: number;
  isWeighed: boolean;
  mrps: number[];
}

export interface Counter {
  id: string;
  storeId: string;
  code: string;
  name: string;
  isActive: boolean;
  activeDevices: number;
  nextInvoiceNumber: string;
  rowVersion: number;
}

export interface CounterDevice {
  id: string;
  name: string;
  enrolledBy: string;
  enrolledAtUtc: string;
  lastSeenAtUtc: string | null;
  revokedAtUtc: string | null;
  isThisDevice: boolean;
}

export interface PosContext {
  businessId: string;
  businessName: string;
  storeId: string;
  storeName: string;
  storeStateCode: string;
  counterId: string;
  counterCode: string;
  counterName: string;
  deviceId: string;
  deviceName: string;
  taxMode: string;
  nextInvoiceNumber: string;
  canOverridePrices: boolean;
  canDiscount: boolean;
  canOverrideNegativeStock: boolean;
}

export interface CartLineRequest {
  variantUnitId: string;
  quantity: number;
  mrp?: number | null;
  overridePrice?: number | null;
  overrideApprovalToken?: string | null;
  discountAmount?: number | null;
  discountPercent?: number | null;
  batchId?: string | null;
}

export interface BuyerRequest {
  name: string | null;
  gstin: string | null;
  phone: string | null;
  address: string | null;
  stateCode: string | null;
}

export interface CartRequest {
  channel: string;
  lines: CartLineRequest[];
  billDiscountAmount?: number | null;
  billDiscountPercent?: number | null;
  buyer?: BuyerRequest | null;
}

export interface CartLine {
  lineNumber: number;
  variantId: string;
  variantUnitId: string;
  description: string;
  unitCode: string;
  hsnSac: string;
  quantity: number;
  mrp: number | null;
  unitPrice: number;
  taxInclusive: boolean;
  rateType: string;
  priceRuleId: string | null;
  belowMinimum: boolean;
  needsPriceApproval: boolean;
  supplyType: string;
  gstRatePercent: number;
  cessRatePercent: number;
  gross: number;
  itemDiscount: number;
  billDiscount: number;
  taxable: number;
  cgst: number;
  sgst: number;
  igst: number;
  cess: number;
  total: number;
}

export interface CartTotals {
  kind: string;
  taxMode: string;
  isInterState: boolean;
  placeOfSupplyStateCode: string;
  lines: CartLine[];
  grossTotal: number;
  discountTotal: number;
  taxableTotal: number;
  cgstTotal: number;
  sgstTotal: number;
  igstTotal: number;
  cessTotal: number;
  roundOff: number;
  grandTotal: number;
  needsDiscountApproval: boolean;
}

export interface PaymentRequest {
  method: string;
  amount: number;
  reference: string | null;
}

export interface Invoice {
  id: string;
  number: string;
  kind: string;
  taxMode: string;
  channel: string;
  businessDate: string;
  issuedAtUtc: string;
  storeId: string;
  counterId: string;
  counterCode: string;
  cashier: string;
  sellerName: string;
  sellerGstin: string | null;
  sellerAddress: string;
  sellerStateCode: string;
  buyerName: string | null;
  buyerGstin: string | null;
  buyerPhone: string | null;
  buyerAddress: string | null;
  placeOfSupplyStateCode: string;
  isInterState: boolean;
  lines: CartLine[];
  grossTotal: number;
  discountTotal: number;
  taxableTotal: number;
  cgstTotal: number;
  sgstTotal: number;
  igstTotal: number;
  cessTotal: number;
  roundOff: number;
  grandTotal: number;
  paidTotal: number;
  changeDue: number;
  payments: { method: string; amount: number; reference: string | null }[];
  declaration: string | null;
}

export interface InvoiceSummary {
  id: string;
  number: string;
  kind: string;
  businessDate: string;
  issuedAtUtc: string;
  counterCode: string;
  cashier: string;
  buyerName: string | null;
  grandTotal: number;
}

export interface ParkedBill {
  id: string;
  label: string | null;
  items: number;
  parkedBy: string;
  parkedAtUtc: string;
}

export interface SupervisorApproval {
  approvalId: string;
  token: string;
  approvedBy: string;
  expiresAtUtc: string;
}
