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
  CREDIT_NOTE: 'Credit note',
  ON_ACCOUNT: 'On account',
};

/** A customer account as the counter sees it: what they owe, and the credit left. */
export interface CounterDebtor {
  id: string;
  code: string;
  name: string;
  phone: string | null;
  gstin: string | null;
  status: string;
  creditLimit: number;
  creditPeriodDays: number;
  balance: number;
  overdue: number;
  available: number;
}

export interface DebtorReceipt {
  id: string;
  number: string;
  storeId: string;
  debtorId: string;
  debtorName: string;
  receiptDate: string;
  method: string;
  reference: string | null;
  amount: number;
  note: string;
  receivedBy: string;
  counterCode: string | null;
  shiftId: string | null;
  createdAtUtc: string;
  appliedTo: { chargeEntryId: string; entryType: string; documentNumber: string | null; entryDate: string; amount: number }[];
  unapplied: number;
  balanceAfter: number;
  collectorSessionId: string | null;
  chequeStatus: string | null;
  reversalKind: string | null;
  reversalReason: string | null;
}

export const ReceiptMethodLabels: Record<string, string> = {
  CASH: 'Cash',
  CARD: 'Card',
  UPI: 'UPI',
  BANK_TRANSFER: 'Bank transfer',
  CHEQUE: 'Cheque',
  DEMAND_DRAFT: 'Demand draft',
  OTHER: 'Other',
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
  canOverrideCreditLimit: boolean;
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
  debtorId?: string | null;
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
  debtor: CounterDebtor | null;
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
  debtorId: string | null;
  debtorCode: string | null;
  dueDate: string | null;
  onAccount: number;
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

export interface ReturnableLine {
  originalLineId: string;
  lineNumber: number;
  description: string;
  unitCode: string;
  sold: number;
  returnable: number;
  unitTotal: number;
}

export interface ReturnableInvoice {
  invoice: Invoice;
  lines: ReturnableLine[];
}

export interface CreditNoteLine {
  lineNumber: number;
  originalLineId: string;
  description: string;
  hsnSac: string;
  unitCode: string;
  quantity: number;
  restocked: boolean;
  gstRatePercent: number;
  taxable: number;
  cgst: number;
  sgst: number;
  igst: number;
  cess: number;
  total: number;
}

export interface ReturnPreview {
  lines: CreditNoteLine[];
  taxableTotal: number;
  cgstTotal: number;
  sgstTotal: number;
  igstTotal: number;
  cessTotal: number;
  roundOff: number;
  grandTotal: number;
  needsApproval: boolean;
}

export interface CreditNote {
  id: string;
  number: string;
  originalInvoiceId: string;
  originalInvoiceNumber: string;
  originalInvoiceDate: string;
  taxMode: string;
  businessDate: string;
  issuedAtUtc: string;
  storeId: string;
  counterCode: string;
  cashier: string;
  reason: string;
  lines: CreditNoteLine[];
  grandTotal: number;
  roundOff: number;
  refunds: { method: string; amount: number; reference: string | null }[];
  storeCredit: number;
  storeCreditLeft: number;
}

export const RefundMethodLabels: Record<string, string> = {
  CASH: 'Cash',
  CARD: 'Card',
  UPI: 'UPI',
  WALLET: 'Wallet',
  STORE_CREDIT: 'Store credit (for an exchange)',
  ON_ACCOUNT: "Off the customer's account",
};

export const Denominations = [2000, 500, 200, 100, 50, 20, 10, 5, 2, 1] as const;

export interface DenominationCount {
  denomination: number;
  count: number;
}

export interface MethodTotal {
  method: string;
  amount: number;
}

export interface CashMovementInfo {
  kind: string;
  amount: number;
  reason: string;
  recordedBy: string;
  recordedAtUtc: string;
}

export interface ShiftSummary {
  id: string;
  storeId: string;
  counterId: string;
  counterCode: string;
  cashierUserId: string;
  cashier: string;
  status: 'OPEN' | 'CLOSED';
  businessDate: string;
  openedAtUtc: string;
  closedAtUtc: string | null;
  openingFloat: number;
  invoices: number;
  salesTotal: number;
  returns: number;
  returnsTotal: number;
  payments: MethodTotal[];
  refunds: MethodTotal[];
  movements: CashMovementInfo[];
  expectedCash: number | null;
  countedCash: number | null;
  difference: number | null;
  closeNote: string | null;
  needsReview: boolean;
  reviewedBy: string | null;
  reviewNote: string | null;
  parkedBillsCleared: number;
  rowVersion: number;
  receipts: MethodTotal[] | null;
}

export const CashMovementLabels: Record<string, string> = {
  PAY_IN: 'Cash in (pay-in)',
  PAY_OUT: 'Cash out (expense)',
  DROP: 'Drop to safe',
};

// Purchasing

export const PurchasePermission = {
  View: 'purchases.view',
  Manage: 'purchases.manage',
  Approve: 'purchases.approve',
  Suppliers: 'suppliers.manage',
  Prices: 'prices.manage',
} as const;

export const AccountPermission = {
  DebtorsView: 'debtors.view',
  DebtorsManage: 'debtors.manage',
  Payables: 'payables.manage',
  Receivables: 'receivables.manage',
  Adjust: 'ledgers.adjust',
  Approve: 'ledgers.approve',
} as const;

export const LedgerEntryTypeLabels: Record<string, string> = {
  OPENING: 'Opening balance',
  GRN: 'Goods receipt',
  PAYMENT: 'Payment',
  DEBIT_NOTE: 'Debit note',
  INVOICE: 'Credit sale',
  RECEIPT: 'Receipt',
  CREDIT_NOTE: 'Credit note',
  ADJUSTMENT: 'Correction',
};

export const PartyPaymentMethodLabels: Record<string, string> = {
  CASH: 'Cash',
  BANK_TRANSFER: 'Bank transfer',
  UPI: 'UPI',
  CHEQUE: 'Cheque',
};

export const DebtorStatusLabels: Record<string, string> = {
  ACTIVE: 'Active',
  ON_HOLD: 'On hold (no credit)',
  CLOSED: 'Closed',
};

export interface Debtor extends PartyContact {
  id: string;
  code: string;
  legalName: string;
  displayName: string;
  gstin: string | null;
  stateCode: string;
  address: string | null;
  phone: string | null;
  consentChangedAtUtc: string | null;
  creditLimit: number;
  customerGroupId: string | null;
  status: string;
  balance: number;
  overdue: number;
  rowVersion: number;
}

export interface AccountEntry {
  id: string;
  sequence: number;
  entryType: string;
  storeId: string | null;
  documentId: string | null;
  documentNumber: string | null;
  entryDate: string;
  dueDate: string | null;
  amount: number;
  balanceAfter: number;
  narration: string;
  outstanding: number;
  createdBy: string;
  createdAtUtc: string;
}

export interface Statement {
  partyType: 'SUPPLIER' | 'DEBTOR';
  partyId: string;
  partyName: string;
  from: string | null;
  to: string | null;
  openingBalance: number;
  entries: AccountEntry[];
  closingBalance: number;
}

export interface OpenItem {
  entryId: string;
  entryType: string;
  documentNumber: string | null;
  documentId: string | null;
  entryDate: string;
  dueDate: string | null;
  amount: number;
  remaining: number;
  daysOverdue: number;
}

export interface OpenItems {
  partyType: 'SUPPLIER' | 'DEBTOR';
  partyId: string;
  balance: number;
  overdue: number;
  charges: OpenItem[];
  unappliedPayments: OpenItem[];
  ageing: { notDue: number; days1To30: number; days31To60: number; days61To90: number; over90: number };
}

export interface SupplierPayment {
  id: string;
  number: string;
  storeId: string;
  supplierId: string;
  supplierName: string;
  paymentDate: string;
  method: string;
  reference: string | null;
  amount: number;
  note: string;
  paidBy: string;
  createdAtUtc: string;
  appliedTo: { chargeEntryId: string; entryType: string; documentNumber: string | null; entryDate: string; amount: number }[];
  unapplied: number;
}

export const PurchaseClassificationLabels: Record<string, string> = {
  GST_TAX_INVOICE: 'GST tax invoice',
  BILL_OF_SUPPLY: 'Bill of supply',
  UNREGISTERED: 'Unregistered supplier',
  IMPORT: 'Import',
  REVERSE_CHARGE: 'Reverse charge',
  PENDING_DOCUMENT: 'Invoice not yet received',
  OTHER: 'Other',
};

export const ExpenseKindLabels: Record<string, string> = {
  FREIGHT: 'Freight',
  LOADING: 'Loading',
  INSURANCE: 'Insurance',
  PACKING: 'Packing',
  HANDLING: 'Handling',
  TRANSPORT: 'Transport',
  CUSTOMS: 'Customs',
  OTHER: 'Other',
};

export const AllocationMethodLabels: Record<string, string> = {
  QUANTITY: 'By quantity',
  VALUE: 'By value',
  WEIGHT: 'By weight',
  VOLUME: 'By volume',
  EQUAL: 'Equally',
};

export const GrnStatusLabels: Record<string, string> = {
  PENDING_APPROVAL: 'Waiting for approval',
  POSTED: 'Posted',
  REJECTED: 'Rejected',
};

export const OrderProgressLabels: Record<string, string> = {
  NOT_RECEIVED: 'Not received',
  PARTLY_RECEIVED: 'Partly received',
  RECEIVED: 'Received',
};

/** Contact details shared by suppliers and debtors. */
export interface PartyContact {
  tradeName: string | null;
  contactPerson: string | null;
  email: string | null;
  whatsAppNumber: string | null;
  smsNumber: string | null;
  whatsAppConsent: boolean;
  smsConsent: boolean;
  creditPeriodDays: number;
}

export interface Supplier extends PartyContact {
  id: string;
  code: string;
  name: string;
  gstin: string | null;
  stateCode: string;
  address: string | null;
  phone: string | null;
  isActive: boolean;
  rowVersion: number;
  balance: number;
  overdue: number;
}

export interface PurchaseSettings {
  costReasonThresholdPercent: number;
  costApprovalThresholdPercent: number;
  allowLossLeader: boolean;
  rowVersion: number;
}

export interface GrnLineRequest {
  variantUnitId: string;
  quantity: number;
  rate: number;
  freeQuantity: number;
  mrp: number | null;
  discountPercent: number | null;
  batchNumber: string | null;
  expiresOn: string | null;
  sellingPrice: number | null;
  costChangeReason: string | null;
  lossLeaderReason: string | null;
  gstRatePercent: number | null;
  updateSellingPrice: boolean;
}

export interface GrnExpenseRequest {
  kind: string;
  amount: number;
  method: string;
  note: string | null;
}

export interface GrnRequest {
  storeId: string;
  supplierId: string;
  supplierInvoiceNumber: string;
  supplierInvoiceDate: string;
  classification: string;
  lines: GrnLineRequest[];
  expenses: GrnExpenseRequest[];
  supplierInvoiceTotal: number | null;
  purchaseOrderReference: string | null;
  notes: string | null;
  idempotencyKey: string | null;
  purchaseOrderId: string | null;
}

export interface CostChange {
  previousUnitCost: number;
  newUnitCost: number;
  difference: number;
  percentChange: number;
  previousSupplier: string | null;
  previousGrnNumber: string | null;
  previousDate: string | null;
  needsReason: boolean;
  needsApproval: boolean;
}

export interface BelowCost {
  sellingPrice: number;
  costPerPack: number;
  sellingNetPerPack: number;
  lossPerPack: number;
  marginPercent: number;
}

export interface GrnLine {
  lineNumber: number;
  variantId: string;
  variantUnitId: string;
  description: string;
  unitCode: string;
  factorToBase: number;
  quantity: number;
  freeQuantity: number;
  baseQuantity: number;
  mrp: number | null;
  rate: number;
  discount: number;
  gstRatePercent: number;
  cessRatePercent: number;
  taxable: number;
  cgst: number;
  sgst: number;
  igst: number;
  cess: number;
  total: number;
  expenseShare: number;
  nonRecoverableTax: number;
  landedTotal: number;
  landedUnitCost: number;
  batchNumber: string | null;
  expiresOn: string | null;
  sellingPrice: number | null;
  costChange: CostChange | null;
  belowCost: BelowCost | null;
  costChangeReason: string | null;
  lossLeaderReason: string | null;
  updateSellingPrice: boolean;
}

export interface GrnIssue {
  code: string;
  message: string;
  lineNumber: number | null;
}

export interface Grn {
  id: string;
  number: string;
  status: string;
  storeId: string;
  supplierId: string;
  supplierName: string;
  supplierGstin: string | null;
  supplierInvoiceNumber: string;
  supplierInvoiceDate: string;
  classification: string;
  purchaseOrderReference: string | null;
  isInterState: boolean;
  taxRecoverable: boolean;
  businessDate: string;
  notes: string | null;
  lines: GrnLine[];
  expenses: { kind: string; amount: number; method: string; note: string | null; allocations: number[] }[];
  grossTotal: number;
  discountTotal: number;
  taxableTotal: number;
  cgstTotal: number;
  sgstTotal: number;
  igstTotal: number;
  cessTotal: number;
  roundOff: number;
  invoiceTotal: number;
  expensesTotal: number;
  landedTotal: number;
  needsApproval: boolean;
  approvalRequestId: string | null;
  receivedBy: string | null;
  receivedAtUtc: string | null;
  postedAtUtc: string | null;
  issues: GrnIssue[];
  purchaseOrderId: string | null;
  purchaseOrderNumber: string | null;
}

export interface GrnSummary {
  id: string;
  number: string;
  status: string;
  supplierName: string;
  supplierInvoiceNumber: string;
  supplierInvoiceDate: string;
  classification: string;
  businessDate: string;
  invoiceTotal: number;
  landedTotal: number;
}

export interface PurchaseOrderLine {
  lineNumber: number;
  variantId: string;
  variantUnitId: string;
  description: string;
  unitCode: string;
  ordered: number;
  received: number;
  outstanding: number;
  rate: number | null;
}

export interface PurchaseOrder {
  id: string;
  number: string;
  status: 'OPEN' | 'CLOSED' | 'CANCELLED';
  progress: string;
  storeId: string;
  supplierId: string;
  supplierName: string;
  orderDate: string;
  expectedDate: string | null;
  notes: string | null;
  lines: PurchaseOrderLine[];
  receiptNumbers: string[];
  rowVersion: number;
}

export interface AttachmentInfo {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  sha256: string;
  uploadedBy: string;
  uploadedAtUtc: string;
}

export interface ReturnableGrnLine {
  grnLineId: string;
  lineNumber: number;
  description: string;
  unitCode: string;
  received: number;
  returned: number;
  returnable: number;
  batchNumber: string | null;
  expiresOn: string | null;
  unitValue: number;
}

export interface ReturnableGrn {
  grnId: string;
  number: string;
  status: string;
  storeId: string;
  supplierId: string;
  supplierName: string;
  supplierInvoiceNumber: string;
  supplierInvoiceDate: string;
  lines: ReturnableGrnLine[];
  returnNumbers: string[];
}

export interface PurchaseReturn {
  id: string;
  number: string;
  storeId: string;
  supplierId: string;
  supplierName: string;
  grnId: string;
  grnNumber: string;
  businessDate: string;
  reason: string;
  lines: { lineNumber: number; grnLineId: string; description: string; unitCode: string; quantity: number; taxable: number; total: number; stockValue: number }[];
  taxable: number;
  cgst: number;
  sgst: number;
  igst: number;
  cess: number;
  roundOff: number;
  total: number;
  stockValue: number;
}

export interface PurchaseReturnSummary {
  id: string;
  number: string;
  businessDate: string;
  supplierName: string;
  grnNumber: string;
  reason: string;
  total: number;
}

// Collections

export const CollectionPermission = {
  View: 'collections.view',
  Manage: 'collections.manage',
  Collect: 'collections.collect',
  Receive: 'collections.receive',
  Allocate: 'collections.allocate',
} as const;

export const ScheduleTypeLabels: Record<string, string> = {
  MANUAL: 'Only when a visit is assigned',
  WEEKDAYS: 'On weekdays',
  FORTNIGHTLY: 'Every second week',
  MONTHLY: 'Monthly',
  DUE_DATE: 'By due date',
  SPECIFIC_DATE: 'On a date',
};

export const WeekdayNames = ['SUNDAY', 'MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY'] as const;

export const VisitReasonLabels: Record<string, string> = {
  SCHEDULE: 'Scheduled',
  DUE_DATE: 'Invoice due',
  PROMISE: 'Promised today',
  ASSIGNED: 'Visit assigned',
  BACKUP: 'Covering',
  OVERDUE: 'Overdue',
};

export interface RouteInfo {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  parties: number;
  rowVersion: number;
}

export interface Collector {
  userId: string;
  displayName: string;
  username: string;
}

export interface CollectionPlan {
  debtorId: string;
  debtorCode: string;
  debtorName: string;
  routeId: string | null;
  routeCode: string | null;
  visitSequence: number | null;
  primaryCollectorUserId: string | null;
  primaryCollector: string | null;
  backupCollectorUserId: string | null;
  backupCollector: string | null;
  preferredFrom: string | null;
  preferredTo: string | null;
  scheduleType: string;
  weekdays: string[];
  anchorDate: string | null;
  monthDay: number | null;
  dueOffsetDays: number | null;
  description: string;
}

export interface VisitInfo {
  id: string;
  debtorId: string;
  debtorName: string;
  collectorUserId: string;
  collector: string;
  visitDate: string;
  note: string;
  isCancelled: boolean;
  assignedBy: string;
}

export interface PromiseInfo {
  id: string;
  debtorId: string;
  amount: number;
  promisedDate: string;
  note: string;
  status: 'PENDING' | 'KEPT' | 'BROKEN' | 'CANCELLED';
  paidSince: number;
  recordedBy: string;
  recordedAtUtc: string;
}

export interface AbsenceInfo {
  id: string;
  collectorUserId: string;
  collector: string;
  absentOn: string;
  reason: string;
}

export interface DayParty {
  debtorId: string;
  code: string;
  name: string;
  routeCode: string | null;
  routeName: string | null;
  visitSequence: number | null;
  address: string | null;
  phone: string | null;
  whatsAppNumber: string | null;
  preferredFrom: string | null;
  preferredTo: string | null;
  reasons: string[];
  notes: string[];
  totalBalance: number;
  dueBalance: number;
  overdueBalance: number;
  notYetDueBalance: number;
  oldestUnpaidDocument: string | null;
  oldestUnpaidDueDate: string | null;
  daysOverdue: number;
  lastCollectionDate: string | null;
  lastCollectionAmount: number | null;
  promisedAmount: number | null;
  promisedDate: string | null;
  collectedToday: number;
  status: 'PENDING' | 'COLLECTED' | 'VISITED';
  visitOutcome: string | null;
}

export interface DayList {
  collectorUserId: string;
  collector: string;
  date: string;
  absent: boolean;
  parties: DayParty[];
  dueTotal: number;
  overdueTotal: number;
  collectedTotal: number;
}

export const VisitOutcomeLabels: Record<string, string> = {
  NO_PAYMENT: 'Visited, no payment',
  NOT_AVAILABLE: 'Not available',
  SHOP_CLOSED: 'Shop closed',
  DISPUTED: 'Disputes the amount',
};

export const ChequeStatusLabels: Record<string, string> = {
  RECEIVED: 'Received',
  DEPOSITED: 'Deposited',
  CLEARED: 'Cleared',
  BOUNCED: 'Bounced',
  CANCELLED: 'Cancelled',
  REPLACED: 'Replaced',
};

/** The next steps a cheque may take (as the server allows). */
export const ChequeMoves: Record<string, string[]> = {
  RECEIVED: ['DEPOSITED', 'CANCELLED'],
  DEPOSITED: ['CLEARED', 'BOUNCED'],
  BOUNCED: ['REPLACED'],
  CANCELLED: ['REPLACED'],
};

export interface SessionInstrument {
  chequeId: string;
  kind: string;
  number: string;
  bankName: string | null;
  amount: number;
  debtorName: string;
  status: string;
}

export interface CollectorSessionInfo {
  id: string;
  storeId: string;
  collectorUserId: string;
  collector: string;
  businessDate: string;
  status: 'OPEN' | 'HANDED_OVER' | 'CONFIRMED';
  openedAtUtc: string;
  receipts: number;
  totals: MethodTotal[];
  instruments: SessionInstrument[];
  expectedCash: number | null;
  declaredCash: number | null;
  countedCash: number | null;
  variance: number | null;
  receivedBy: string | null;
  note: string | null;
  handedOverAtUtc: string | null;
  confirmedAtUtc: string | null;
  rowVersion: number;
}

export interface ChequeInfo {
  id: string;
  kind: string;
  number: string;
  bankName: string | null;
  chequeDate: string | null;
  amount: number;
  status: string;
  receiptId: string;
  receiptNumber: string;
  debtorId: string;
  debtorName: string;
  replacedByReceiptNumber: string | null;
  history: { status: string; eventDate: string; note: string | null; recordedBy: string; recordedAtUtc: string }[];
  rowVersion: number;
}

export const MessagingPermission = {
  View: 'messaging.view',
  Manage: 'messaging.manage',
} as const;

export interface MessagingSettings {
  whatsAppEnabled: boolean;
  smsEnabled: boolean;
  sendInvoices: boolean;
  sendReceipts: boolean;
  whatsAppProvider: string;
  smsProvider: string;
  whatsAppReady: boolean;
  rowVersion: number;
}

export interface MessageTemplate {
  kind: string;
  channel: string;
  providerTemplateName: string | null;
  dltTemplateId: string | null;
  languageCode: string;
  body: string;
  isActive: boolean;
  placeholders: string[];
}

export interface MessageEvent {
  status: string;
  detail: string | null;
  atUtc: string;
}

export interface OutboundMessage {
  id: string;
  debtorId: string;
  debtorName: string;
  channel: string;
  kind: string;
  documentNumber: string;
  toNumber: string | null;
  body: string;
  status: string;
  skipReason: string | null;
  attempts: number;
  lastError: string | null;
  createdAtUtc: string;
  sentAtUtc: string | null;
  deliveredAtUtc: string | null;
  readAtUtc: string | null;
  failedAtUtc: string | null;
  attachmentSha256: string | null;
  events: MessageEvent[];
}

export const MessageChannelLabels: Record<string, string> = { WHATSAPP: 'WhatsApp', SMS: 'SMS' };

export const MessageKindLabels: Record<string, string> = { CREDIT_INVOICE: 'Credit invoice', RECEIPT: 'Receipt' };

export const MessageStatusLabels: Record<string, string> = {
  QUEUED: 'Waiting to send',
  SENT: 'Sent',
  DELIVERED: 'Delivered',
  READ: 'Read',
  FAILED: 'Failed',
  SKIPPED: 'Not sent',
  RETRY: 'Attempt failed',
};
