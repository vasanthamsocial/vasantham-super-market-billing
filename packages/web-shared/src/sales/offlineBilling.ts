// Offline counter billing (D-039): the counter agent prices and issues real invoices from the counter's offline series
// while the store server cannot be reached; the POS hands it the price list while online and delivers its bills after.

import type { CartLine, Invoice, PosContext } from '../types';
import { counterAgentCall, type AgentSettings } from './counterAgent';

export interface OfflineLimits {
  maxBills: number;
  maxAmount: number;
  maxHours: number;
}

export interface OfflineStatus {
  ready: boolean;
  refusal: string | null;
  series: string | null;
  nextNumber: string | null;
  cashier: string | null;
  cashierUserId: string | null;
  deviceId: string | null;
  packCreatedAtUtc: string | null;
  items: number;
  limits: OfflineLimits | null;
  pending: number;
  pendingAmount: number;
  oldestPendingUtc: string | null;
}

export interface OfflineItemMatch {
  variantUnitId: string;
  name: string;
  unitCode: string;
  mrps: number[];
  barcode: string | null;
}

export interface OfflineCartLine {
  variantUnitId: string;
  quantity: number;
  mrp: number | null;
}

export interface OfflineCart {
  channel: string;
  lines: OfflineCartLine[];
}

interface Amounts {
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

export interface OfflineBillLine {
  lineNumber: number;
  variantUnitId: string;
  variantId: string;
  productId: string;
  description: string;
  hsnSac: string;
  unitCode: string;
  quantity: number;
  baseQuantity: number;
  mrp: number | null;
  priceRuleId: string;
  rateType: string;
  unitPrice: number;
  taxInclusive: boolean;
  supplyType: string;
  gstRatePercent: number;
  cessRatePercent: number;
  amounts: Amounts;
}

export interface OfflineDraft {
  channel: string;
  kind: string;
  placeOfSupply: string;
  isInterState: boolean;
  lines: OfflineBillLine[];
  result: { kind: string; gross: number; discount: number; taxable: number; cgst: number; sgst: number; igst: number; cess: number; roundOff: number; grandTotal: number };
}

export interface OfflinePayment {
  method: 'CASH' | 'CARD' | 'UPI';
  amount: number;
  reference: string | null;
}

export interface OfflineBill {
  id: string;
  numberPrefix: string;
  sequence: number;
  number: string;
  issuedAtUtc: string;
  businessDate: string;
  cashierUserId: string;
  taxMode: string;
  kind: string;
  channel: string;
  seller: { name: string; gstin: string | null; address: string; stateCode: string };
  buyer: { name: string | null; gstin: string | null; phone: string | null; address: string | null };
  placeOfSupply: string;
  lines: OfflineBillLine[];
  grossTotal: number;
  taxableTotal: number;
  cgstTotal: number;
  sgstTotal: number;
  igstTotal: number;
  cessTotal: number;
  roundOff: number;
  grandTotal: number;
  payments: OfflinePayment[];
  changeDue: number;
}

export interface OfflineBillResult {
  id: string;
  number: string;
  status: 'POSTED' | 'QUARANTINED' | 'DUPLICATE' | 'REJECTED' | 'NOT_PROCESSED';
  reason: string | null;
  review: string | null;
  invoiceId: string | null;
}

export const offlineAgent = {
  status: (s: AgentSettings) => counterAgentCall<OfflineStatus>(s, 'GET', '/offline/status'),
  setPack: (s: AgentSettings, pack: unknown) => counterAgentCall<OfflineStatus>(s, 'POST', '/offline/pack', pack),
  find: (s: AgentSettings, search: string) => counterAgentCall<OfflineItemMatch[]>(s, 'GET', `/offline/items?search=${encodeURIComponent(search)}`),
  price: (s: AgentSettings, cart: OfflineCart) => counterAgentCall<OfflineDraft>(s, 'POST', '/offline/price', cart),
  issue: (s: AgentSettings, request: { id: string; cart: OfflineCart; payments: OfflinePayment[]; expectedGrandTotal: number }) =>
    counterAgentCall<OfflineBill>(s, 'POST', '/offline/bills', request),
  pending: (s: AgentSettings) => counterAgentCall<OfflineBill[]>(s, 'GET', '/offline/bills'),
  acknowledge: (s: AgentSettings, results: { id: string; status: string }[]) => counterAgentCall<OfflineStatus>(s, 'POST', '/offline/bills/ack', { results }),
};

/** The bill as an invoice, to print it the same way (receipt printer through the agent, or the browser). */
export function offlineInvoice(bill: OfflineBill, context: PosContext, cashier: string): Invoice {
  const lines: CartLine[] = bill.lines.map((l) => ({
    lineNumber: l.lineNumber,
    variantId: l.variantId,
    variantUnitId: l.variantUnitId,
    description: l.description,
    unitCode: l.unitCode,
    hsnSac: l.hsnSac,
    quantity: l.quantity,
    mrp: l.mrp,
    unitPrice: l.unitPrice,
    taxInclusive: l.taxInclusive,
    rateType: l.rateType,
    priceRuleId: l.priceRuleId,
    belowMinimum: false,
    needsPriceApproval: false,
    supplyType: l.supplyType,
    gstRatePercent: l.gstRatePercent,
    cessRatePercent: l.cessRatePercent,
    gross: l.amounts.gross,
    itemDiscount: l.amounts.itemDiscount,
    billDiscount: l.amounts.billDiscount,
    taxable: l.amounts.taxable,
    cgst: l.amounts.cgst,
    sgst: l.amounts.sgst,
    igst: l.amounts.igst,
    cess: l.amounts.cess,
    total: l.amounts.total,
  }));
  return {
    id: bill.id,
    number: bill.number,
    kind: bill.kind,
    taxMode: bill.taxMode,
    channel: bill.channel,
    businessDate: bill.businessDate,
    issuedAtUtc: bill.issuedAtUtc,
    storeId: context.storeId,
    counterId: context.counterId,
    counterCode: context.counterCode,
    cashier,
    sellerName: bill.seller.name,
    sellerGstin: bill.seller.gstin,
    sellerAddress: bill.seller.address,
    sellerStateCode: bill.seller.stateCode,
    buyerName: bill.buyer.name,
    buyerGstin: bill.buyer.gstin,
    buyerPhone: bill.buyer.phone,
    buyerAddress: bill.buyer.address,
    placeOfSupplyStateCode: bill.placeOfSupply,
    isInterState: bill.placeOfSupply !== bill.seller.stateCode,
    lines,
    grossTotal: bill.grossTotal,
    discountTotal: 0,
    taxableTotal: bill.taxableTotal,
    cgstTotal: bill.cgstTotal,
    sgstTotal: bill.sgstTotal,
    igstTotal: bill.igstTotal,
    cessTotal: bill.cessTotal,
    roundOff: bill.roundOff,
    grandTotal: bill.grandTotal,
    paidTotal: bill.payments.reduce((sum, p) => sum + p.amount, 0),
    changeDue: bill.changeDue,
    payments: bill.payments,
    declaration: bill.taxMode === 'GST_COMPOSITION' ? 'Composition taxable person, not eligible to collect tax on supplies.' : null,
    debtorId: null,
    debtorCode: null,
    dueDate: null,
    onAccount: 0,
    fulfilment: null,
  };
}
