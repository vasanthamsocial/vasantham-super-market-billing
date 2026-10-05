'use client';

import { useCallback, useEffect, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from 'react';
import { api, ApiError, errorMessage } from '../api';
import { useAuth } from '../auth/AuthContext';
import {
  InvoiceKindLabels,
  PaymentMethodLabels,
  type BarcodeLookup,
  ReceiptMethodLabels,
  type CounterDebtor,
  type DebtorReceipt,
  type BuyerRequest,
  type CartRequest,
  type CartTotals,
  type CounterDeliveryOptions,
  FulfilmentModeLabels,
  type FulfilmentRequest,
  type Invoice,
  type ParkedBill,
  type PaymentRequest,
  type PosContext,
  type ProductDetail,
  type ProductSummary,
  type SupervisorApproval,
} from '../types';
import { counterAgent, loadAgentSettings, type AgentSettings } from './counterAgent';
import { CashMovementDialog, CloseShiftDialog, OpenShiftPanel } from '../shifts/ShiftDialogs';
import type { ShiftSummary } from '../types';
import { HardwareDialog } from './HardwareDialog';
import { InvoiceReceipt } from './InvoiceReceipt';
import { ReturnDialog } from './ReturnDialog';
import { SupervisorApprovalForm } from './SupervisorApprovalForm';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const qtyFormat = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });

/** A line as the cashier entered it. Names, prices and taxes always come from the server's pricing of the cart. */
interface DraftLine {
  key: string;
  variantUnitId: string;
  quantity: number;
  mrp: number | null;
  overridePrice: number | null;
  overrideApprovalToken: string | null;
  discountAmount: number | null;
}

type Dialog =
  | { kind: 'search'; results: ProductSummary[]; quantity: number }
  | { kind: 'quantity'; title: string; onValue: (value: number) => void; initial?: string; weighed?: boolean }
  | { kind: 'mrp'; options: number[]; onChoose: (mrp: number) => void }
  | { kind: 'amount'; title: string; label: string; initial: string; onValue: (value: number | null) => void }
  | { kind: 'buyer' }
  | { kind: 'park' }
  | { kind: 'parked'; bills: ParkedBill[] }
  | { kind: 'approval'; approvalKind: 'PRICE_OVERRIDE' | 'DISCOUNT'; lineKey?: string; variantUnitId?: string; price?: number; maxAmount?: number; what: string }
  | { kind: 'pay' }
  | { kind: 'receive' }
  | { kind: 'return' }
  | { kind: 'hardware' }
  | { kind: 'cash' }
  | { kind: 'closeShift' }
  | { kind: 'done'; invoice: Invoice };

function newKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
}

function parseAmount(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed === '') return null;
  const number = Number(trimmed);
  return Number.isFinite(number) ? number : Number.NaN;
}

/**
 * The counter billing screen. Keyboard first: the scan box always has focus; scan or type and press Enter.
 * F2 find, F3 customer (account or details), F4 quantity, F5 price, F6 item discount, F7 bill discount, F8 park, F9 parked bills,
 * F12 pay, Delete remove the selected line, arrow keys move the selection, Esc closes a dialog.
 */
export function PosScreen() {
  const { membership, me } = useAuth();
  const [shift, setShift] = useState<ShiftSummary | null | undefined>(undefined);
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const [context, setContext] = useState<PosContext | null>(null);
  const [contextError, setContextError] = useState<unknown>(null);
  const [lines, setLines] = useState<DraftLine[]>([]);
  const [selected, setSelected] = useState(0);
  const [billDiscount, setBillDiscount] = useState<number | null>(null);
  const [discountApproval, setDiscountApproval] = useState<{ token: string; max: number } | null>(null);
  const [buyer, setBuyer] = useState<BuyerRequest | null>(null);
  // The customer's account: its prices apply and the bill can go on account (details refreshed with every price check).
  const [debtorId, setDebtorId] = useState<string | null>(null);
  const [chosenDebtor, setChosenDebtor] = useState<CounterDebtor | null>(null);
  const [channel, setChannel] = useState('RETAIL');
  const [cart, setCart] = useState<CartTotals | null>(null);
  const [cartError, setCartError] = useState<unknown>(null);
  const [pricing, setPricing] = useState(false);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [scan, setScan] = useState('');
  const scanRef = useRef<HTMLInputElement>(null);
  const pricingRun = useRef(0);
  const [agent, setAgent] = useState<AgentSettings | null>(null);

  // Hardware settings belong to this PC (localStorage), read once in the browser.
  useEffect(() => setAgent(loadAgentSettings()), []);

  // Customer display: the last item and the running total, as the server priced them.
  useEffect(() => {
    if (!agent || !cart || cart.lines.length === 0) return;
    const last = cart.lines[cart.lines.length - 1]!;
    counterAgent.display(agent, `${last.description} ${money.format(last.total)}`, `Total Rs. ${money.format(cart.grandTotal)}`).catch(() => undefined);
  }, [agent, cart]);

  const loadContext = useCallback(async () => {
    try {
      setContext(await api.get<PosContext>('/api/v1/pos/context'));
      setShift((await api.get<ShiftSummary | undefined>('/api/v1/pos/shift')) ?? null);
      setContextError(null);
    } catch (caught) {
      setContextError(caught);
    }
  }, []);

  useEffect(() => {
    void loadContext();
  }, [loadContext]);

  const cartRequest = useCallback(
    (): CartRequest => ({
      channel,
      lines: lines.map((l) => ({
        variantUnitId: l.variantUnitId,
        quantity: l.quantity,
        mrp: l.mrp,
        overridePrice: l.overridePrice,
        overrideApprovalToken: l.overrideApprovalToken,
        discountAmount: l.discountAmount,
      })),
      billDiscountAmount: billDiscount,
      buyer,
      debtorId,
    }),
    [channel, lines, billDiscount, buyer, debtorId],
  );

  // Every change is priced by the server; the screen shows the server's figures only.
  useEffect(() => {
    if (!context || lines.length === 0) {
      setCart(null);
      setCartError(null);
      return;
    }
    const run = ++pricingRun.current;
    setPricing(true);
    const timer = window.setTimeout(async () => {
      try {
        const priced = await api.post<CartTotals>('/api/v1/pos/cart', cartRequest());
        if (run === pricingRun.current) {
          setCart(priced);
          setCartError(null);
        }
      } catch (caught) {
        if (run === pricingRun.current) {
          setCart(null);
          setCartError(caught);
        }
      } finally {
        if (run === pricingRun.current) setPricing(false);
      }
    }, 120);
    return () => window.clearTimeout(timer);
  }, [context, lines, cartRequest]);

  const focusScan = useCallback(() => window.setTimeout(() => scanRef.current?.focus(), 0), []);
  const closeDialog = useCallback(() => {
    setDialog(null);
    focusScan();
  }, [focusScan]);

  function addLine(variantUnitId: string, quantity: number, mrp: number | null) {
    setLines((current) => {
      const existing = current.findIndex((l) => l.variantUnitId === variantUnitId && l.mrp === mrp && l.overridePrice === null);
      if (existing >= 0) {
        setSelected(existing);
        return current.map((l, i) => (i === existing ? { ...l, quantity: Math.round((l.quantity + quantity) * 1000) / 1000 } : l));
      }
      setSelected(current.length);
      return [...current, { key: newKey(), variantUnitId, quantity, mrp, overridePrice: null, overrideApprovalToken: null, discountAmount: null }];
    });
    setMessage(null);
  }

  /** Asks for whatever the item still needs (weight, which MRP), then adds it. */
  function addItem(variantUnitId: string, isWeighed: boolean, mrps: number[], quantity: number | null) {
    const withMrp = (qty: number) => {
      if (mrps.length > 1) {
        setDialog({ kind: 'mrp', options: [...mrps].sort((a, b) => a - b), onChoose: (mrp) => { addLine(variantUnitId, qty, mrp); closeDialog(); } });
      } else {
        addLine(variantUnitId, qty, mrps[0] ?? null);
        closeDialog();
      }
    };
    if (quantity === null && isWeighed) {
      setDialog({ kind: 'quantity', title: 'Weight / quantity', onValue: withMrp, weighed: true });
    } else {
      withMrp(quantity ?? 1);
    }
  }

  async function addProduct(productId: string, quantity: number | null) {
    if (!business) return;
    const product = await api.get<ProductDetail>(`${business}/catalog/products/${productId}`);
    const variant = product.variants.find((v) => v.isActive) ?? product.variants[0];
    const pack = variant?.units.find((u) => u.isBase) ?? variant?.units[0];
    if (!variant || !pack) throw new Error(`${product.name} has nothing to sell.`);
    const mrps = variant.mrps.filter((m) => m.isActive && m.variantUnitId === pack.id).map((m) => m.mrp);
    addItem(pack.id, product.isWeighed, [...new Set(mrps)], quantity);
  }

  async function onScan(event: FormEvent) {
    event.preventDefault();
    const raw = scan.trim();
    if (!raw || !business) return;
    setScan('');
    // "3*8901234567890" sells three of the scanned item.
    const multiplied = /^(\d+(?:\.\d+)?)\*(.+)$/.exec(raw);
    const quantity = multiplied ? Number(multiplied[1]) : null;
    const code = multiplied ? multiplied[2]!.trim() : raw;
    try {
      if (/^\d{8,14}$/.test(code)) {
        try {
          const found = await api.get<BarcodeLookup>(`${business}/catalog/lookup?barcode=${encodeURIComponent(code)}`);
          addItem(found.variantUnitId, found.isWeighed, found.mrps, quantity);
          return;
        } catch (caught) {
          if (!(caught instanceof ApiError && caught.status === 404)) throw caught;
        }
      }
      const results = await api.get<ProductSummary[]>(`${business}/catalog/products?take=10&search=${encodeURIComponent(code)}`);
      const active = results.filter((p) => p.isActive);
      if (active.length === 0) {
        setMessage(`Nothing found for "${code}".`);
      } else if (active.length === 1) {
        await addProduct(active[0]!.id, quantity);
      } else {
        setDialog({ kind: 'search', results: active, quantity: quantity ?? 1 });
      }
    } catch (caught) {
      setMessage(errorMessage(caught));
    }
  }

  function updateSelected(change: Partial<DraftLine>) {
    setLines((current) => current.map((l, i) => (i === selected ? { ...l, ...change } : l)));
  }

  function newBill() {
    setLines([]);
    setSelected(0);
    setBillDiscount(null);
    setDiscountApproval(null);
    setBuyer(null);
    setDebtorId(null);
    setChosenDebtor(null);
    setChannel('RETAIL');
    setCart(null);
    setCartError(null);
    setMessage(null);
    void loadContext();
    closeDialog();
  }

  const discountApproved = !!cart && (!cart.needsDiscountApproval || (!!discountApproval && cart.discountTotal <= discountApproval.max));
  const pendingPriceApproval = cart?.lines.find((l) => l.needsPriceApproval);

  function startPayment() {
    if (!cart || pricing || cartError) return;
    if (pendingPriceApproval) {
      const draft = lines[pendingPriceApproval.lineNumber - 1];
      setDialog({
        kind: 'approval',
        approvalKind: 'PRICE_OVERRIDE',
        lineKey: draft?.key,
        variantUnitId: pendingPriceApproval.variantUnitId,
        price: draft?.overridePrice ?? pendingPriceApproval.unitPrice,
        what: `Price Rs. ${money.format(draft?.overridePrice ?? pendingPriceApproval.unitPrice)} for ${pendingPriceApproval.description}`,
      });
      return;
    }
    if (!discountApproved) {
      setDialog({ kind: 'approval', approvalKind: 'DISCOUNT', maxAmount: cart.discountTotal, what: `Discounts of Rs. ${money.format(cart.discountTotal)} on this bill` });
      return;
    }
    setDialog({ kind: 'pay' });
  }

  async function park(label: string) {
    await api.post('/api/v1/pos/parked', { label: label || null, cart: cartRequest() });
    newBill();
    setMessage('Bill parked. Press F9 to bring it back.');
  }

  async function showParked() {
    try {
      setDialog({ kind: 'parked', bills: await api.get<ParkedBill[]>('/api/v1/pos/parked') });
    } catch (caught) {
      setMessage(errorMessage(caught));
    }
  }

  async function retrieve(id: string) {
    const parked = await api.post<CartRequest>(`/api/v1/pos/parked/${id}/retrieve`);
    setLines(
      parked.lines.map((l) => ({
        key: newKey(),
        variantUnitId: l.variantUnitId,
        quantity: l.quantity,
        mrp: l.mrp ?? null,
        overridePrice: l.overridePrice ?? null,
        overrideApprovalToken: l.overrideApprovalToken ?? null,
        discountAmount: l.discountAmount ?? null,
      })),
    );
    setChannel(parked.channel);
    setBillDiscount(parked.billDiscountAmount ?? null);
    setBuyer(parked.buyer ?? null);
    setDebtorId(parked.debtorId ?? null);
    setChosenDebtor(null);
    setSelected(0);
    closeDialog();
  }

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Escape' && dialog && dialog.kind !== 'done') {
      event.preventDefault();
      closeDialog();
      return;
    }
    if (dialog) return;
    const line = lines[selected];
    const handled = (() => {
      switch (event.key) {
        case 'F2':
          scanRef.current?.focus();
          return true;
        case 'F3':
          setDialog({ kind: 'buyer' });
          return true;
        case 'F4':
          if (line) setDialog({ kind: 'quantity', title: 'Quantity', initial: String(line.quantity), onValue: (q) => { updateSelected({ quantity: q }); closeDialog(); } });
          return true;
        case 'F5':
          if (line)
            setDialog({
              kind: 'amount',
              title: 'Change price (per pack)',
              label: 'New price (Rs.), empty to use the normal price',
              initial: line.overridePrice?.toString() ?? '',
              onValue: (price) => { updateSelected({ overridePrice: price, overrideApprovalToken: null }); closeDialog(); },
            });
          return true;
        case 'F6':
          if (line)
            setDialog({
              kind: 'amount',
              title: 'Item discount',
              label: 'Discount on this line (Rs.)',
              initial: line.discountAmount?.toString() ?? '',
              onValue: (amount) => { updateSelected({ discountAmount: amount }); closeDialog(); },
            });
          return true;
        case 'F7':
          setDialog({
            kind: 'amount',
            title: 'Bill discount',
            label: 'Discount on the whole bill (Rs.)',
            initial: billDiscount?.toString() ?? '',
            onValue: (amount) => { setBillDiscount(amount); closeDialog(); },
          });
          return true;
        case 'F8':
          if (lines.length > 0) setDialog({ kind: 'park' });
          return true;
        case 'F9':
          void showParked();
          return true;
        case 'F10':
          setDialog({ kind: 'return' });
          return true;
        case 'F11':
          setDialog({ kind: 'hardware' });
          return true;
        case 'F12':
          startPayment();
          return true;
        case 'ArrowDown':
          setSelected((s) => Math.min(s + 1, Math.max(lines.length - 1, 0)));
          return true;
        case 'ArrowUp':
          setSelected((s) => Math.max(s - 1, 0));
          return true;
        case 'Delete':
          if (scan === '' && line) {
            setLines((current) => current.filter((_, i) => i !== selected));
            setSelected((s) => Math.max(0, Math.min(s, lines.length - 2)));
            return true;
          }
          return false;
        default:
          return false;
      }
    })();
    if (handled) event.preventDefault();
  }

  const account = cart?.debtor ?? (chosenDebtor && chosenDebtor.id === debtorId ? chosenDebtor : null);

  if (contextError) {
    return (
      <section className="sb-card sb-pos" data-testid="pos-not-ready">
        <h2>Billing counter</h2>
        <p className="sb-error" role="alert">{errorMessage(contextError)}</p>
      </section>
    );
  }

  if (!context || shift === undefined) return <p className="sb-muted">Opening the counter...</p>;

  // Billing happens only in the signed-in cashier's own open shift.
  if (shift === null) return <OpenShiftPanel counterCode={context.counterCode} onOpened={setShift} />;
  if (shift.cashierUserId !== me?.userId) {
    return (
      <section className="sb-card sb-pos" data-testid="pos-other-shift">
        <h2>Counter {context.counterCode}</h2>
        <p className="sb-notice sb-notice--warning" role="status">
          {shift.cashier}&apos;s shift is open on this counter. They must close it (or a manager can close it under Shifts) before you can bill here.
        </p>
      </section>
    );
  }

  return (
    <div className="sb-pos" onKeyDown={onKeyDown} data-testid="pos">
      <header className="sb-pos__header">
        <strong data-testid="pos-counter">Counter {context.counterCode}</strong>
        <span>{context.storeName}</span>
        <span className="sb-muted">Next bill {context.nextInvoiceNumber}</span>
        <span className="sb-muted" data-testid="pos-shift">Shift since {new Date(shift.openedAtUtc).toLocaleTimeString('en-IN', { timeStyle: 'short' })}</span>
        <label className="sb-check">
          <select className="sb-input sb-input--inline" aria-label="Billing type" value={channel} onChange={(e) => setChannel(e.target.value)}>
            <option value="RETAIL">Retail</option>
            <option value="WHOLESALE">Wholesale</option>
          </select>
        </label>
        {buyer?.name ? <span data-testid="pos-buyer">Customer: {buyer.name}{buyer.gstin ? ` (${buyer.gstin})` : ''}</span> : null}
        {account ? (
          <span data-testid="pos-account">
            Account: {account.name} - owes Rs. {money.format(account.balance)}
            {account.status === 'ACTIVE' ? `, credit left Rs. ${money.format(Math.max(account.available, 0))}` : ' (on hold: no credit)'}
          </span>
        ) : null}
      </header>

      <div className="sb-pos__body">
        <section className="sb-pos__items" aria-label="Bill items">
          <form onSubmit={onScan} className="sb-pos__scan">
            <input
              ref={scanRef}
              autoFocus
              className="sb-input sb-pos__scan-input"
              aria-label="Scan or type an item"
              placeholder="Scan a barcode, or type a name or code and press Enter (3*code for three)"
              value={scan}
              onChange={(e) => setScan(e.target.value)}
              disabled={!!dialog}
            />
          </form>
          {message ? <p className="sb-notice sb-notice--warning" role="status">{message}</p> : null}
          {cartError ? <p className="sb-error" role="alert" data-testid="pos-error">{errorMessage(cartError)}</p> : null}
          <table className="sb-table sb-pos__lines" data-testid="pos-lines">
            <thead>
              <tr>
                <th>#</th>
                <th>Item</th>
                <th className="sb-num">Qty</th>
                <th className="sb-num">Price</th>
                <th className="sb-num">Discount</th>
                <th className="sb-num">Amount</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l, i) => {
                const priced = cart?.lines[i];
                return (
                  <tr key={l.key} className={i === selected ? 'sb-pos__selected' : undefined} onClick={() => setSelected(i)} aria-selected={i === selected}>
                    <td>{i + 1}</td>
                    <td>
                      {priced?.description ?? '...'}
                      {priced?.mrp ? <span className="sb-muted"> MRP {money.format(priced.mrp)}</span> : null}
                      {priced?.needsPriceApproval ? <span className="sb-chip sb-chip--pending"> needs approval</span> : null}
                      {priced?.belowMinimum ? <span className="sb-chip sb-chip--pending"> below minimum</span> : null}
                    </td>
                    <td className="sb-num">{qtyFormat.format(l.quantity)} {priced?.unitCode ?? ''}</td>
                    <td className="sb-num">{priced ? money.format(priced.unitPrice) : ''}{l.overridePrice !== null ? ' *' : ''}</td>
                    <td className="sb-num">{priced && priced.itemDiscount + priced.billDiscount > 0 ? money.format(priced.itemDiscount + priced.billDiscount) : ''}</td>
                    <td className="sb-num">{priced ? money.format(priced.total) : ''}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          {lines.length === 0 ? <p className="sb-muted">Scan the first item to start a bill.</p> : null}
        </section>

        <aside className="sb-pos__totals" aria-label="Bill total">
          <div className="sb-pos__grand" data-testid="pos-total">
            <span>Total</span>
            <strong>{cart ? `Rs. ${money.format(cart.grandTotal)}` : lines.length > 0 ? '...' : 'Rs. 0.00'}</strong>
          </div>
          {cart ? (
            <dl className="sb-pos__summary">
              <dt>Document</dt>
              <dd>{InvoiceKindLabels[cart.kind] ?? cart.kind}</dd>
              <dt>Items</dt>
              <dd>{cart.lines.length}</dd>
              {cart.discountTotal > 0 ? (<><dt>Discount</dt><dd>{money.format(cart.discountTotal)}</dd></>) : null}
              {cart.taxMode === 'GST_REGULAR' ? (
                <>
                  <dt>Taxable</dt>
                  <dd>{money.format(cart.taxableTotal)}</dd>
                  {cart.isInterState ? (<><dt>IGST</dt><dd>{money.format(cart.igstTotal)}</dd></>) : (<><dt>CGST + SGST</dt><dd>{money.format(cart.cgstTotal + cart.sgstTotal)}</dd></>)}
                </>
              ) : null}
              {cart.roundOff !== 0 ? (<><dt>Round off</dt><dd>{money.format(cart.roundOff)}</dd></>) : null}
            </dl>
          ) : null}
          <button type="button" className="sb-button sb-pos__pay" disabled={!cart || pricing} onClick={startPayment}>
            Pay (F12)
          </button>
          <div className="sb-actions">
            <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setDialog({ kind: 'cash' })}>Cash in/out</button>
            <button type="button" className="sb-button sb-button--secondary sb-button--small" disabled={!account} onClick={() => setDialog({ kind: 'receive' })}>
              Take payment
            </button>
            <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setDialog({ kind: 'closeShift' })}>Close shift</button>
          </div>
          <ul className="sb-pos__keys" aria-label="Keyboard shortcuts">
            <li><kbd>F2</kbd> Find</li>
            <li><kbd>F3</kbd> Customer</li>
            <li><kbd>F4</kbd> Quantity</li>
            <li><kbd>F5</kbd> Price</li>
            <li><kbd>F6</kbd> Item discount</li>
            <li><kbd>F7</kbd> Bill discount</li>
            <li><kbd>F8</kbd> Park</li>
            <li><kbd>F9</kbd> Parked bills</li>
            <li><kbd>F10</kbd> Return</li>
            <li><kbd>F11</kbd> Hardware</li>
            <li><kbd>Del</kbd> Remove line</li>
          </ul>
        </aside>
      </div>

      {dialog ? (
        <PosDialogs
          dialog={dialog}
          context={context}
          cart={cart}
          close={closeDialog}
          addProduct={async (id, quantity) => {
            try {
              await addProduct(id, quantity);
            } catch (caught) {
              setMessage(errorMessage(caught));
              closeDialog();
            }
          }}
          setBuyer={(b) => { setBuyer(b); closeDialog(); }}
          buyer={buyer}
          account={account}
          chooseAccount={(d) => { setDebtorId(d?.id ?? null); setChosenDebtor(d); closeDialog(); }}
          park={park}
          retrieve={retrieve}
          approved={(approval, d) => {
            if (d.approvalKind === 'DISCOUNT') {
              setDiscountApproval({ token: approval.token, max: d.maxAmount ?? 0 });
            } else {
              setLines((current) => current.map((l) => (l.key === d.lineKey ? { ...l, overridePrice: d.price ?? null, overrideApprovalToken: approval.token } : l)));
            }
            setMessage(`Approved by ${approval.approvedBy}. Press F12 to continue.`);
            closeDialog();
          }}
          issue={async (payments, key, negativeOverride, creditApprovalToken, fulfilment) =>
            api.post<Invoice>('/api/v1/pos/invoices', {
              idempotencyKey: key,
              cart: cartRequest(),
              payments,
              expectedGrandTotal: cart?.grandTotal ?? 0,
              discountApprovalToken: discountApproval?.token ?? null,
              negativeStockOverride: negativeOverride,
              creditApprovalToken,
              fulfilment,
            })
          }
          done={(invoice) => {
            setDialog({ kind: 'done', invoice });
            if (agent) {
              if (agent.autoPrint) {
                counterAgent
                  .printReceipt(agent, invoice, invoice.payments.some((p) => p.method === 'CASH'))
                  .catch((e: unknown) => setMessage(`Receipt not printed: ${errorMessage(e)} Use Print receipt to retry.`));
              }
              counterAgent.display(agent, 'Thank you!', invoice.changeDue > 0 ? `Change Rs. ${money.format(invoice.changeDue)}` : `Paid Rs. ${money.format(invoice.grandTotal)}`).catch(() => undefined);
            }
          }}
          agent={agent}
          setAgent={(a) => { setAgent(a); closeDialog(); }}
          shiftClosed={() => { setShift(null); newBill(); }}
          newBill={newBill}
        />
      ) : null}
    </div>
  );
}

function Modal({ title, children, testId, onClose }: { title: string; children: ReactNode; testId: string; onClose?: () => void }) {
  return (
    <div className="sb-modal" role="dialog" aria-modal="true" aria-label={title} data-testid={testId}>
      <div className="sb-modal__box">
        <header className="sb-card__header">
          <h2>{title}</h2>
          {onClose ? <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={onClose}>Close (Esc)</button> : null}
        </header>
        {children}
      </div>
    </div>
  );
}

function PosDialogs(props: {
  dialog: Dialog;
  context: PosContext;
  cart: CartTotals | null;
  close: () => void;
  addProduct: (productId: string, quantity: number) => Promise<void>;
  buyer: BuyerRequest | null;
  setBuyer: (buyer: BuyerRequest | null) => void;
  account: CounterDebtor | null;
  chooseAccount: (debtor: CounterDebtor | null) => void;
  park: (label: string) => Promise<void>;
  retrieve: (id: string) => Promise<void>;
  approved: (approval: SupervisorApproval, dialog: Extract<Dialog, { kind: 'approval' }>) => void;
  issue: (payments: PaymentRequest[], idempotencyKey: string, negativeOverride: boolean, creditApprovalToken: string | null, fulfilment: FulfilmentRequest | null) => Promise<Invoice>;
  done: (invoice: Invoice) => void;
  newBill: () => void;
  agent: AgentSettings | null;
  setAgent: (agent: AgentSettings | null) => void;
  shiftClosed: () => void;
}) {
  const { dialog, close } = props;
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  async function run(work: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await work();
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  switch (dialog.kind) {
    case 'search':
      return (
        <Modal title="Choose the item" testId="pos-search" onClose={close}>
          <ul className="sb-plain-list">
            {dialog.results.map((p, i) => (
              <li key={p.id}>
                <button type="button" className="sb-button sb-button--secondary sb-pos__choice" autoFocus={i === 0} onClick={() => void props.addProduct(p.id, dialog.quantity)}>
                  {p.code} - {p.name}
                </button>
              </li>
            ))}
          </ul>
        </Modal>
      );
    case 'quantity':
      return (
        <Modal title={dialog.title} testId="pos-quantity" onClose={close}>
          <ValueForm
            label="Quantity"
            initial={dialog.initial ?? ''}
            onSubmit={(v) => { const n = Number(v); if (Number.isFinite(n) && n > 0) dialog.onValue(n); }}
            fill={dialog.weighed && props.agent ? { label: 'Read scale', get: async () => String((await counterAgent.readWeight(props.agent!)).kilograms) } : undefined}
          />
        </Modal>
      );
    case 'mrp':
      return (
        <Modal title="Which MRP is on the pack?" testId="pos-mrp" onClose={close}>
          <ul className="sb-plain-list">
            {dialog.options.map((m, i) => (
              <li key={m}>
                <button type="button" className="sb-button sb-button--secondary sb-pos__choice" autoFocus={i === 0} onClick={() => dialog.onChoose(m)}>
                  MRP Rs. {money.format(m)}
                </button>
              </li>
            ))}
          </ul>
        </Modal>
      );
    case 'amount':
      return (
        <Modal title={dialog.title} testId="pos-amount" onClose={close}>
          <ValueForm
            label={dialog.label}
            initial={dialog.initial}
            onSubmit={(v) => {
              const amount = parseAmount(v);
              if (amount === null || (!Number.isNaN(amount) && amount >= 0)) dialog.onValue(amount);
            }}
          />
        </Modal>
      );
    case 'buyer':
      return (
        <Modal title="Customer details" testId="pos-buyer-dialog" onClose={close}>
          <AccountPicker current={props.account} choose={props.chooseAccount} />
          <h3>Or details for this bill only</h3>
          <form
            className="sb-form"
            onSubmit={(e) => {
              e.preventDefault();
              const data = new FormData(e.currentTarget);
              const value = (name: string) => (String(data.get(name) ?? '').trim() || null);
              const buyer = { name: value('name'), gstin: value('gstin'), phone: value('phone'), address: value('address'), stateCode: value('stateCode') };
              props.setBuyer(Object.values(buyer).some((v) => v) ? buyer : null);
            }}
          >
            <LabelledInput label="Name" name="name" initial={props.buyer?.name} autoFocus />
            <LabelledInput label="GSTIN (for a GST invoice to a business)" name="gstin" initial={props.buyer?.gstin} />
            <LabelledInput label="Phone" name="phone" initial={props.buyer?.phone} />
            <LabelledInput label="Address" name="address" initial={props.buyer?.address} />
            <LabelledInput label="State code (if from another state, without GSTIN)" name="stateCode" initial={props.buyer?.stateCode} />
            <button className="sb-button" type="submit">Save customer</button>
          </form>
        </Modal>
      );
    case 'park':
      return (
        <Modal title="Park this bill" testId="pos-park" onClose={close}>
          <ValueForm label="Label (optional, for example the customer)" initial="" onSubmit={(v) => void run(() => props.park(v))} busy={busy} />
          {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
        </Modal>
      );
    case 'parked':
      return (
        <Modal title="Parked bills" testId="pos-parked" onClose={close}>
          {dialog.bills.length === 0 ? <p className="sb-muted">No parked bills on this counter.</p> : null}
          <ul className="sb-plain-list">
            {dialog.bills.map((b, i) => (
              <li key={b.id}>
                <button type="button" className="sb-button sb-button--secondary sb-pos__choice" autoFocus={i === 0} onClick={() => void run(() => props.retrieve(b.id))}>
                  {b.label ?? 'Unnamed'} - {b.items} item{b.items === 1 ? '' : 's'}, {new Date(b.parkedAtUtc).toLocaleTimeString('en-IN', { timeStyle: 'short' })} by {b.parkedBy}
                </button>
              </li>
            ))}
          </ul>
          {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
        </Modal>
      );
    case 'approval':
      return (
        <Modal title="Supervisor approval" testId="pos-approval" onClose={close}>
          <SupervisorApprovalForm
            what={dialog.what}
            request={{ kind: dialog.approvalKind, variantUnitId: dialog.variantUnitId, price: dialog.price, maxAmount: dialog.maxAmount }}
            onApproved={(approval) => props.approved(approval, dialog)}
          />
        </Modal>
      );
    case 'return':
      return <ReturnDialog businessId={props.context.businessId} onClose={close} />;
    case 'hardware':
      return <HardwareDialog current={props.agent} onSaved={props.setAgent} onClose={close} />;
    case 'cash':
      return <CashMovementDialog onDone={() => close()} onClose={close} />;
    case 'closeShift':
      return <CloseShiftDialog onClose={close} onClosed={props.shiftClosed} />;
    case 'pay':
      return (
        <PaymentDialog
          total={props.cart?.grandTotal ?? 0}
          account={props.account}
          canOverrideNegative={props.context.canOverrideNegativeStock}
          issue={props.issue}
          done={props.done}
          close={close}
        />
      );
    case 'receive':
      return props.account ? <ReceiveDialog account={props.account} onClose={close} /> : null;
    case 'done':
      return (
        <Modal title={`Bill ${dialog.invoice.number} done`} testId="pos-done">
          <div className="sb-pos__done">
            <div>
              {dialog.invoice.changeDue > 0 ? (
                <p className="sb-pos__change" data-testid="pos-change">Give change: Rs. {money.format(dialog.invoice.changeDue)}</p>
              ) : (
                <p className="sb-pos__change">No change due.</p>
              )}
              {dialog.invoice.fulfilment && dialog.invoice.fulfilment.status === 'AWAITING_DISPATCH' ? (
                <p className="sb-notice" data-testid="pos-delivery">
                  {FulfilmentModeLabels[dialog.invoice.fulfilment.mode]}
                  {dialog.invoice.fulfilment.transporterName ? `: ${dialog.invoice.fulfilment.transporterName}` : ''}
                  {dialog.invoice.fulfilment.destinationBranch ? ` to ${dialog.invoice.fulfilment.destinationBranch}` : ''}. Goes to dispatch.
                </p>
              ) : null}
              {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
              <div className="sb-actions">
                <button
                  type="button"
                  className="sb-button"
                  onClick={() => {
                    const agent = props.agent;
                    if (agent) void run(() => counterAgent.printReceipt(agent, dialog.invoice, false));
                    else window.print();
                  }}
                >
                  Print receipt
                </button>
                <a className="sb-button sb-button--secondary" href={`/api/v1/pos/invoices/${dialog.invoice.id}/pdf`} target="_blank" rel="noopener" data-testid="pos-pdf">
                  PDF invoice
                </a>
                <button type="button" className="sb-button sb-button--secondary" autoFocus onClick={props.newBill}>New bill (Enter)</button>
              </div>
            </div>
            <InvoiceReceipt invoice={dialog.invoice} />
          </div>
        </Modal>
      );
  }
}

function PaymentDialog({
  total,
  account,
  canOverrideNegative,
  issue,
  done,
  close,
}: {
  total: number;
  account: CounterDebtor | null;
  canOverrideNegative: boolean;
  issue: (payments: PaymentRequest[], idempotencyKey: string, negativeOverride: boolean, creditApprovalToken: string | null, fulfilment: FulfilmentRequest | null) => Promise<Invoice>;
  done: (invoice: Invoice) => void;
  close: () => void;
}) {
  const methods = Object.keys(PaymentMethodLabels).filter((m) => m !== 'ON_ACCOUNT' || account);
  const [amounts, setAmounts] = useState<Record<string, string>>({ CASH: total.toFixed(2), CARD: '', UPI: '', WALLET: '', CREDIT_NOTE: '', ON_ACCOUNT: '' });
  const [references, setReferences] = useState<Record<string, string>>({ CARD: '', UPI: '', WALLET: '', CREDIT_NOTE: '' });
  const [negativeOverride, setNegativeOverride] = useState(false);
  // Over the credit limit: a supervisor approves the amount over, then the same bill is sent again.
  const [creditApproval, setCreditApproval] = useState<{ token: string; by: string } | null>(null);
  const [askCredit, setAskCredit] = useState<number | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  // One key per exact request: a retry after a lost response cannot bill twice; a changed request gets a new key.
  const attempt = useRef<{ body: string; key: string } | null>(null);
  // How the goods reach the customer: pickup unless chosen (the customer's usual way is filled in).
  const [options, setOptions] = useState<CounterDeliveryOptions | null>(null);
  const [delivery, setDelivery] = useState<FulfilmentRequest>({ mode: 'PICKUP' });
  const accountId = account?.id ?? null;
  useEffect(() => {
    let live = true;
    api
      .get<CounterDeliveryOptions>(`/api/v1/pos/delivery-options${accountId ? `?debtorId=${accountId}` : ''}`)
      .then((o) => {
        if (!live) return;
        setOptions(o);
        const p = o.preference;
        setDelivery(p ? { mode: p.mode, transporterId: p.transporterId, destinationBranchId: p.destinationBranchId, deliveryAddress: o.deliveryAddress } : { mode: 'PICKUP', deliveryAddress: o.deliveryAddress });
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, [accountId]);
  const lorry = options?.transporters.find((t) => t.id === delivery.transporterId) ?? null;

  const paid = Object.values(amounts).reduce((sum, v) => sum + (Number(v) || 0), 0);
  const change = Math.round((paid - total) * 100) / 100;

  async function submit(event: FormEvent) {
    event.preventDefault();
    const payments = Object.entries(amounts)
      .map(([method, value]) => ({ method, amount: Math.round((Number(value) || 0) * 100) / 100, reference: references[method]?.trim() || null }))
      .filter((p) => p.amount > 0);
    const fulfilment: FulfilmentRequest | null =
      delivery.mode === 'PICKUP'
        ? null
        : {
            mode: delivery.mode,
            deliveryAddress: delivery.deliveryAddress?.trim() || null,
            transporterId: delivery.mode === 'LORRY' ? delivery.transporterId || null : null,
            destinationBranchId: delivery.mode === 'LORRY' ? delivery.destinationBranchId || null : null,
          };
    const body = JSON.stringify({ payments, negativeOverride, credit: creditApproval?.token ?? null, fulfilment });
    if (attempt.current?.body !== body) attempt.current = { body, key: newKey() };
    setBusy(true);
    setError(null);
    try {
      done(await issue(payments, attempt.current.key, negativeOverride, creditApproval?.token ?? null, fulfilment));
    } catch (caught) {
      // Refused by the server: nothing was billed, so a corrected attempt gets a fresh key.
      if (caught instanceof ApiError) attempt.current = null;
      if (caught instanceof ApiError && caught.code === 'credit.limit_exceeded' && account) {
        const onAccount = payments.filter((p) => p.method === 'ON_ACCOUNT').reduce((sum, p) => sum + p.amount, 0);
        setAskCredit(Math.round((account.balance + onAccount - account.creditLimit) * 100) / 100);
      }
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title={`Payment: Rs. ${money.format(total)}`} testId="pos-pay" onClose={close}>
      <form className="sb-form" onSubmit={submit}>
        {methods.map((method, i) => (
          <div key={method} className="sb-form-row">
            <LabelledInput
              label={PaymentMethodLabels[method]!}
              name={method}
              value={amounts[method] ?? ''}
              onChange={(v) => setAmounts((a) => ({ ...a, [method]: v }))}
              inputMode="decimal"
              autoFocus={i === 0}
            />
            {method !== 'CASH' && method !== 'ON_ACCOUNT' ? (
              <LabelledInput
                label={method === 'CREDIT_NOTE' ? 'Credit note number' : `${PaymentMethodLabels[method]} reference`}
                name={`${method}-ref`}
                value={references[method] ?? ''}
                onChange={(v) => setReferences((r) => ({ ...r, [method]: v }))}
              />
            ) : null}
          </div>
        ))}
        <p className="sb-pos__change" data-testid="pay-change">
          {change >= 0 ? `Change: Rs. ${money.format(change)}` : `Still due: Rs. ${money.format(-change)}`}
        </p>
        {canOverrideNegative ? (
          <label className="sb-check">
            <input type="checkbox" checked={negativeOverride} onChange={(e) => setNegativeOverride(e.target.checked)} /> Confirm selling below zero stock (where the rule allows it)
          </label>
        ) : null}
        {account && Number(amounts.ON_ACCOUNT) > 0 ? (
          <p className="sb-muted" data-testid="pay-account">
            On account for {account.name}: owes Rs. {money.format(account.balance)}, limit Rs. {money.format(account.creditLimit)}, due in {account.creditPeriodDays} days.
          </p>
        ) : null}
        <fieldset className="sb-pos__delivery" data-testid="pay-delivery">
          <legend>Delivery</legend>
          <label className="sb-field">
            <span className="sb-field__label">How the goods go</span>
            <select className="sb-input" value={delivery.mode} onChange={(e) => setDelivery((d) => ({ ...d, mode: e.target.value }))}>
              {Object.entries(FulfilmentModeLabels).map(([value, label]) => (
                <option key={value} value={value} disabled={value === 'LORRY' && !options?.transporters.length}>{label}</option>
              ))}
            </select>
          </label>
          {delivery.mode !== 'PICKUP' ? (
            <label className="sb-field">
              <span className="sb-field__label">Deliver to</span>
              <input className="sb-input" value={delivery.deliveryAddress ?? ''} onChange={(e) => setDelivery((d) => ({ ...d, deliveryAddress: e.target.value }))} />
            </label>
          ) : null}
          {delivery.mode === 'LORRY' ? (
            <div className="sb-form-row">
              <label className="sb-field">
                <span className="sb-field__label">Lorry service</span>
                <select className="sb-input" value={delivery.transporterId ?? ''}
                  onChange={(e) => setDelivery((d) => ({ ...d, transporterId: e.target.value || null, destinationBranchId: null }))}>
                  <option value="">Choose...</option>
                  {(options?.transporters ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
                </select>
              </label>
              <label className="sb-field">
                <span className="sb-field__label">Destination branch</span>
                <select className="sb-input" value={delivery.destinationBranchId ?? ''} onChange={(e) => setDelivery((d) => ({ ...d, destinationBranchId: e.target.value || null }))}>
                  <option value="">Decide at booking</option>
                  {(lorry?.destinations ?? []).map((b) => <option key={b.id} value={b.id}>{b.name}, {b.city}</option>)}
                </select>
              </label>
            </div>
          ) : null}
        </fieldset>
        {creditApproval ? <p className="sb-notice sb-notice--success" role="status">Credit approved by {creditApproval.by}. Complete the bill.</p> : null}
        {error ? <p className="sb-error" role="alert" data-testid="pay-error">{errorMessage(error)}</p> : null}
        <button className="sb-button" type="submit" disabled={busy || change < 0}>{busy ? 'Saving...' : 'Complete bill (Enter)'}</button>
      </form>
      {askCredit !== null && !creditApproval ? (
        <SupervisorApprovalForm
          what={`Going Rs. ${money.format(askCredit)} over ${account?.name ?? 'the customer'}'s credit limit`}
          request={{ kind: 'CREDIT_LIMIT', maxAmount: askCredit }}
          onApproved={(approval) => {
            setCreditApproval({ token: approval.token, by: approval.approvedBy });
            setAskCredit(null);
            setError(null);
          }}
        />
      ) : null}
    </Modal>
  );
}

/** Finds a customer account by name, code or phone; choosing one bills to it (its prices, and on-account payment). */
function AccountPicker({ current, choose }: { current: CounterDebtor | null; choose: (debtor: CounterDebtor | null) => void }) {
  const [search, setSearch] = useState('');
  const [found, setFound] = useState<CounterDebtor[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  return (
    <section aria-label="Customer account" data-testid="pos-account-picker">
      <h3>Customer account</h3>
      {current ? (
        <p>
          {current.name} ({current.code}){' '}
          <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => choose(null)}>Bill without the account</button>
        </p>
      ) : null}
      <form
        className="sb-inline-form"
        onSubmit={async (e) => {
          e.preventDefault();
          setError(null);
          try {
            setFound(await api.get<CounterDebtor[]>(`/api/v1/pos/debtors?search=${encodeURIComponent(search.trim())}`));
          } catch (caught) {
            setError(caught);
          }
        }}
      >
        <input className="sb-input" aria-label="Find a customer account" placeholder="Name, code or phone" value={search} onChange={(e) => setSearch(e.target.value)} autoFocus />
        <button className="sb-button sb-button--secondary" type="submit">Find</button>
      </form>
      {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
      {found && found.length === 0 ? <p className="sb-muted">No matching accounts.</p> : null}
      <ul className="sb-plain-list">
        {(found ?? []).map((d) => (
          <li key={d.id}>
            <button type="button" className="sb-button sb-button--secondary sb-pos__choice" onClick={() => choose(d)}>
              {d.code} - {d.name}: owes Rs. {money.format(d.balance)}
              {d.status === 'ACTIVE' ? `, credit left Rs. ${money.format(Math.max(d.available, 0))}` : ', on hold'}
              {d.overdue > 0 ? ` (overdue Rs. ${money.format(d.overdue)})` : ''}
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** Money from the chosen customer towards their account, taken at this counter in the open shift. */
function ReceiveDialog({ account, onClose }: { account: CounterDebtor; onClose: () => void }) {
  const [method, setMethod] = useState('CASH');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<DebtorReceipt | null>(null);
  const attempt = useRef<{ body: string; key: string } | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    const value = parseAmount(amount);
    if (value === null || Number.isNaN(value) || value <= 0) {
      setError(new Error('Enter the amount received.'));
      return;
    }
    const body = JSON.stringify({ debtorId: account.id, method, amount: value, reference: reference.trim() || null });
    if (attempt.current?.body !== body) attempt.current = { body, key: newKey() };
    setBusy(true);
    setError(null);
    try {
      setDone(await api.post<DebtorReceipt>('/api/v1/pos/debtor-receipts', { ...JSON.parse(body), idempotencyKey: attempt.current.key }));
    } catch (caught) {
      if (caught instanceof ApiError) attempt.current = null;
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title={`Payment from ${account.name}`} testId="pos-receive" onClose={onClose}>
      {done ? (
        <p className="sb-notice sb-notice--success" role="status">
          Receipt {done.number}: Rs. {money.format(done.amount)} received
          {done.appliedTo.length > 0 ? ` for ${done.appliedTo.map((a) => a.documentNumber ?? 'opening balance').join(', ')}` : ''}. Now owes Rs.{' '}
          {money.format(done.balanceAfter)}.
        </p>
      ) : (
        <form className="sb-form" onSubmit={submit}>
          <p className="sb-muted">Owes Rs. {money.format(account.balance)}{account.overdue > 0 ? `, of which Rs. ${money.format(account.overdue)} overdue` : ''}.</p>
          <label className="sb-field">
            <span className="sb-field__label">Paid by</span>
            <select className="sb-input" value={method} onChange={(e) => setMethod(e.target.value)}>
              {Object.entries(ReceiptMethodLabels).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
          </label>
          <LabelledInput label="Amount received (Rs.)" name="receive-amount" value={amount} onChange={setAmount} inputMode="decimal" autoFocus />
          {method !== 'CASH' ? (
            <LabelledInput label={method === 'CHEQUE' ? 'Cheque number' : 'Reference'} name="receive-reference" value={reference} onChange={setReference} />
          ) : null}
          {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
          <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Saving...' : 'Record payment'}</button>
        </form>
      )}
    </Modal>
  );
}

function ValueForm({
  label,
  initial,
  onSubmit,
  busy,
  fill,
}: {
  label: string;
  initial: string;
  onSubmit: (value: string) => void;
  busy?: boolean;
  fill?: { label: string; get: () => Promise<string> };
}) {
  const [value, setValue] = useState(initial);
  const [fillError, setFillError] = useState<unknown>(null);
  return (
    <form
      className="sb-form"
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit(value);
      }}
    >
      <LabelledInput label={label} name="value" value={value} onChange={setValue} autoFocus inputMode="decimal" />
      {fill ? (
        <button
          type="button"
          className="sb-button sb-button--secondary"
          onClick={() => {
            setFillError(null);
            fill.get().then(setValue).catch(setFillError);
          }}
        >
          {fill.label}
        </button>
      ) : null}
      {fillError ? <p className="sb-error" role="alert">{errorMessage(fillError)}</p> : null}
      <button className="sb-button" type="submit" disabled={busy}>OK (Enter)</button>
    </form>
  );
}

function LabelledInput({
  label,
  name,
  initial,
  value,
  onChange,
  type = 'text',
  autoFocus,
  autoComplete,
  inputMode,
}: {
  label: string;
  name: string;
  initial?: string | null;
  value?: string;
  onChange?: (value: string) => void;
  type?: string;
  autoFocus?: boolean;
  autoComplete?: string;
  inputMode?: 'decimal' | 'text';
}) {
  const id = `pos-${name}`;
  return (
    <div className="sb-field">
      <label className="sb-field__label" htmlFor={id}>{label}</label>
      <input
        id={id}
        className="sb-input"
        name={name}
        type={type}
        autoFocus={autoFocus}
        autoComplete={autoComplete}
        inputMode={inputMode}
        {...(onChange ? { value: value ?? '', onChange: (e) => onChange(e.target.value) } : { defaultValue: initial ?? '' })}
      />
    </div>
  );
}
