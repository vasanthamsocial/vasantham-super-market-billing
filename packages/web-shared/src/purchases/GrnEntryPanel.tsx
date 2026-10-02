'use client';

import { useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  AllocationMethodLabels,
  ExpenseKindLabels,
  GrnStatusLabels,
  PurchaseClassificationLabels,
  PurchasePermission,
  type Grn,
  type GrnRequest,
  type ProductDetail,
  type PurchaseOrder,
  type Supplier,
} from '../types';
import { ErrorText, Notice } from '../ui';
import { moneyFormat, quantityFormat, StoreSelect, useStoreChoice } from '../stock/StockPanel';
import { GrnAttachments, ItemPicker, newKey, parseNumber, SupplierSelect } from './PurchaseShared';

interface PackChoice {
  id: string;
  label: string;
  mrps: number[];
}

interface DraftLine {
  key: string;
  description: string;
  variantUnitId: string;
  /** Pack choices when the line came from the catalogue; fixed when it came from an order. */
  packs: PackChoice[] | null;
  unitCode: string;
  tracksBatches: boolean | null;
  tracksExpiry: boolean | null;
  quantity: string;
  free: string;
  rate: string;
  mrp: string;
  discount: string;
  gst: string;
  batch: string;
  expiry: string;
  selling: string;
  updatePrice: boolean;
  costReason: string;
  lossReason: string;
}

interface DraftExpense {
  key: string;
  kind: string;
  amount: string;
  method: string;
  note: string;
}

function today(): string {
  return new Date().toLocaleDateString('en-CA');
}

function lineFromProduct(product: ProductDetail): DraftLine {
  const packs: PackChoice[] = product.variants
    .filter((v) => v.isActive)
    .flatMap((v) =>
      v.units.map((u) => ({
        id: u.id,
        label: `${product.variants.length > 1 ? `${v.name} - ` : ''}${u.unitCode}${u.isBase ? '' : ` (${u.factorToBase} ${product.baseUnitCode})`}`,
        mrps: v.mrps.filter((m) => m.isActive && m.variantUnitId === u.id).map((m) => m.mrp),
      })),
    );
  const first = packs[0];
  return {
    ...blankLine(product.name, first?.id ?? ''),
    packs,
    tracksBatches: product.tracksBatches,
    tracksExpiry: product.tracksExpiry,
    mrp: first && first.mrps.length === 1 ? String(first.mrps[0]) : '',
  };
}

/** A line whose pack is fixed and whose batch tracking is not known here (the server checks it). */
function blankLine(description: string, variantUnitId: string): DraftLine {
  return {
    key: newKey(),
    description,
    variantUnitId,
    packs: null,
    unitCode: '',
    tracksBatches: null,
    tracksExpiry: null,
    quantity: '',
    free: '',
    rate: '',
    mrp: '',
    discount: '',
    gst: '',
    batch: '',
    expiry: '',
    selling: '',
    updatePrice: false,
    costReason: '',
    lossReason: '',
  };
}

/** Enters a goods receipt from a supplier's invoice. The server previews it as it is typed: taxes, landed costs, cost changes and anything that stops it from being saved. */
export function GrnEntryPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const { stores, storeId, setStoreId, error: storesError } = useStoreChoice();
  const suppliers = useApiData<Supplier[]>(business ? `${business}/suppliers` : null);
  const [supplierId, setSupplierId] = useState('');
  const [classification, setClassification] = useState('UNREGISTERED');
  const [invoiceNumber, setInvoiceNumber] = useState('');
  const [invoiceDate, setInvoiceDate] = useState(today);
  const [printedTotal, setPrintedTotal] = useState('');
  const [orderId, setOrderId] = useState('');
  const [notes, setNotes] = useState('');
  const [lines, setLines] = useState<DraftLine[]>([]);
  const [expenses, setExpenses] = useState<DraftExpense[]>([]);
  // One key per receipt: a retry after a lost response returns the same receipt instead of saving it twice.
  const [idempotencyKey, setIdempotencyKey] = useState(newKey);
  const [preview, setPreview] = useState<Grn | null>(null);
  const [previewError, setPreviewError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState<Grn | null>(null);
  const previewSequence = useRef(0);

  const orders = useApiData<PurchaseOrder[]>(business && storeId ? `${business}/purchase-orders?storeId=${storeId}&status=OPEN` : null);
  const supplierOrders = (orders.data ?? []).filter((o) => o.supplierId === supplierId);
  const canSetPrices = hasPermission(PurchasePermission.Prices);

  function update(key: string, change: Partial<DraftLine>) {
    setLines((current) => current.map((l) => (l.key === key ? { ...l, ...change } : l)));
  }

  function updateExpense(key: string, change: Partial<DraftExpense>) {
    setExpenses((current) => current.map((e) => (e.key === key ? { ...e, ...change } : e)));
  }

  function chooseSupplier(id: string) {
    setSupplierId(id);
    setOrderId('');
    const supplier = (suppliers.data ?? []).find((s) => s.id === id);
    if (supplier) setClassification(supplier.gstin ? 'GST_TAX_INVOICE' : 'UNREGISTERED');
  }

  function fillFromOrder() {
    const order = supplierOrders.find((o) => o.id === orderId);
    if (!order) return;
    setLines(
      order.lines
        .filter((l) => l.outstanding > 0)
        .map((l) => ({
          ...blankLine(l.description, l.variantUnitId),
          unitCode: l.unitCode,
          quantity: String(l.outstanding),
          rate: l.rate === null ? '' : String(l.rate),
        })),
    );
  }

  /** The request as typed, or why it cannot be checked yet. */
  function build(): { request: GrnRequest | null; missing: string | null } {
    if (!storeId || !supplierId) return { request: null, missing: 'Choose the store and the supplier.' };
    if (!invoiceNumber.trim()) return { request: null, missing: "Enter the supplier's invoice number." };
    if (lines.length === 0) return { request: null, missing: 'Add the items received.' };
    const requestLines = [];
    for (const l of lines) {
      const quantity = parseNumber(l.quantity);
      const rate = parseNumber(l.rate);
      if (quantity === null || rate === null || Number.isNaN(quantity) || Number.isNaN(rate)) {
        return { request: null, missing: `Enter the quantity and rate of ${l.description}.` };
      }
      const free = parseNumber(l.free);
      const mrp = parseNumber(l.mrp);
      const discount = parseNumber(l.discount);
      const gst = parseNumber(l.gst);
      const selling = parseNumber(l.selling);
      if ([free, mrp, discount, gst, selling].some((n) => n !== null && Number.isNaN(n))) {
        return { request: null, missing: `Check the numbers entered for ${l.description}.` };
      }
      requestLines.push({
        variantUnitId: l.variantUnitId,
        quantity,
        rate,
        freeQuantity: free ?? 0,
        mrp,
        discountPercent: discount,
        batchNumber: l.batch.trim() || null,
        expiresOn: l.expiry || null,
        sellingPrice: selling,
        costChangeReason: l.costReason.trim() || null,
        lossLeaderReason: l.lossReason.trim() || null,
        gstRatePercent: gst,
        updateSellingPrice: l.updatePrice && selling !== null,
      });
    }
    const requestExpenses = [];
    for (const e of expenses) {
      const amount = parseNumber(e.amount);
      if (amount === null || Number.isNaN(amount)) return { request: null, missing: `Enter the ${ExpenseKindLabels[e.kind]?.toLowerCase() ?? 'expense'} amount.` };
      requestExpenses.push({ kind: e.kind, amount, method: e.method, note: e.note.trim() || null });
    }
    const total = parseNumber(printedTotal);
    return {
      request: {
        storeId,
        supplierId,
        supplierInvoiceNumber: invoiceNumber.trim(),
        supplierInvoiceDate: invoiceDate,
        classification,
        lines: requestLines,
        expenses: requestExpenses,
        supplierInvoiceTotal: total === null || Number.isNaN(total) ? null : total,
        purchaseOrderReference: null,
        notes: notes.trim() || null,
        idempotencyKey: null,
        purchaseOrderId: orderId || null,
      },
      missing: null,
    };
  }

  const { request, missing } = build();
  const requestJson = request ? JSON.stringify(request) : null;

  // Live preview, half a second after the last change; only the newest answer is shown.
  useEffect(() => {
    if (!business || !requestJson) {
      setPreview(null);
      setPreviewError(null);
      return;
    }
    const sequence = ++previewSequence.current;
    const timer = setTimeout(async () => {
      try {
        const result = await api.post<Grn>(`${business}/grns/preview`, JSON.parse(requestJson));
        if (sequence === previewSequence.current) {
          setPreview(result);
          setPreviewError(null);
        }
      } catch (caught) {
        if (sequence === previewSequence.current) {
          setPreview(null);
          setPreviewError(caught);
        }
      }
    }, 500);
    return () => clearTimeout(timer);
  }, [business, requestJson]);

  const previewMatches = preview !== null && requestJson !== null;
  const generalIssues = previewMatches ? preview.issues.filter((i) => i.lineNumber === null) : [];

  async function save() {
    if (!business || !request) return;
    setBusy(true);
    setError(null);
    try {
      const grn = await api.post<Grn>(`${business}/grns`, { ...request, idempotencyKey });
      setSaved(grn);
      setLines([]);
      setExpenses([]);
      setInvoiceNumber('');
      setPrintedTotal('');
      setNotes('');
      setOrderId('');
      setIdempotencyKey(newKey());
      await orders.reload();
    } catch (caught) {
      // Refused by the server: nothing was saved, so a corrected receipt goes out under a new key.
      if (caught instanceof ApiError && caught.status !== 0) setIdempotencyKey(newKey());
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      {saved ? (
        <section className="sb-card" aria-labelledby="saved-grn-heading">
          <header className="sb-card__header">
            <h2 id="saved-grn-heading">Saved {saved.number}</h2>
          </header>
          <Notice tone={saved.status === 'POSTED' ? 'success' : 'warning'}>
            {saved.status === 'POSTED'
              ? `Goods receipt ${saved.number} is posted: the stock is in at a landed cost of Rs. ${moneyFormat.format(saved.landedTotal)}.`
              : `Goods receipt ${saved.number} is ${GrnStatusLabels[saved.status]?.toLowerCase() ?? saved.status}: stock goes in once a manager approves it.`}
          </Notice>
          <p className="sb-muted">Attach the scanned supplier invoice:</p>
          <GrnAttachments business={business} grnId={saved.id} canUpload />
        </section>
      ) : null}

      <section className="sb-card" aria-labelledby="grn-entry-heading">
        <header className="sb-card__header">
          <h2 id="grn-entry-heading">Receive goods</h2>
        </header>
        <ErrorText error={storesError ?? suppliers.error} />
        <div className="sb-form" data-testid="grn-form">
          <div className="sb-form-row">
            <StoreSelect stores={stores} value={storeId} onChange={(id) => { setStoreId(id); setOrderId(''); }} />
            <SupplierSelect suppliers={suppliers.data ?? []} value={supplierId} onChange={chooseSupplier} />
            <label className="sb-field">
              <span className="sb-field__label">Document</span>
              <select className="sb-input" value={classification} onChange={(event) => setClassification(event.target.value)}>
                {Object.entries(PurchaseClassificationLabels).map(([value, label]) => (
                  <option key={value} value={value}>{label}</option>
                ))}
              </select>
            </label>
          </div>
          <div className="sb-form-row">
            <label className="sb-field">
              <span className="sb-field__label">Supplier invoice number</span>
              <input className="sb-input" value={invoiceNumber} maxLength={40} onChange={(event) => setInvoiceNumber(event.target.value)} />
            </label>
            <label className="sb-field">
              <span className="sb-field__label">Invoice date</span>
              <input className="sb-input" type="date" value={invoiceDate} onChange={(event) => setInvoiceDate(event.target.value)} />
            </label>
            <label className="sb-field">
              <span className="sb-field__label">Invoice total printed (Rs.)</span>
              <input className="sb-input" inputMode="decimal" value={printedTotal} onChange={(event) => setPrintedTotal(event.target.value)} />
            </label>
          </div>
          {supplierOrders.length > 0 ? (
            <div className="sb-inline-form">
              <label className="sb-field">
                <span className="sb-field__label">Against order</span>
                <select className="sb-input" value={orderId} onChange={(event) => setOrderId(event.target.value)}>
                  <option value="">No order</option>
                  {supplierOrders.map((o) => (
                    <option key={o.id} value={o.id}>{o.number} ({o.orderDate})</option>
                  ))}
                </select>
              </label>
              {orderId ? (
                <button type="button" className="sb-button sb-button--secondary" onClick={fillFromOrder}>
                  Fill outstanding items
                </button>
              ) : null}
            </div>
          ) : null}

          <ItemPicker business={business} onPick={(product) => setLines((current) => [...current, lineFromProduct(product)])} />

          {lines.length > 0 ? (
            <table className="sb-table" data-testid="grn-lines">
              <thead>
                <tr>
                  <th>Item</th>
                  <th>Pack</th>
                  <th>Quantity</th>
                  <th>Free</th>
                  <th>Rate (Rs.)</th>
                  <th>MRP</th>
                  <th>Disc. %</th>
                  <th>GST %</th>
                  <th>Batch / expiry</th>
                  <th>Selling price</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {lines.map((l, index) => {
                  const result = previewMatches ? preview.lines[index] : undefined;
                  const issues = previewMatches ? preview.issues.filter((i) => i.lineNumber === index + 1) : [];
                  const pack = l.packs?.find((p) => p.id === l.variantUnitId);
                  return [
                    <tr key={l.key}>
                      <td>{l.description}</td>
                      <td>
                        {l.packs ? (
                          <select
                            className="sb-input"
                            aria-label={`Pack of ${l.description}`}
                            value={l.variantUnitId}
                            onChange={(event) => {
                              const next = l.packs!.find((p) => p.id === event.target.value);
                              update(l.key, { variantUnitId: event.target.value, mrp: next && next.mrps.length === 1 ? String(next.mrps[0]) : l.mrp });
                            }}
                          >
                            {l.packs.map((p) => (
                              <option key={p.id} value={p.id}>{p.label}</option>
                            ))}
                          </select>
                        ) : (
                          l.unitCode
                        )}
                      </td>
                      <td>
                        <input className="sb-input" inputMode="decimal" aria-label={`Quantity of ${l.description}`} value={l.quantity} onChange={(e) => update(l.key, { quantity: e.target.value })} />
                      </td>
                      <td>
                        <input className="sb-input" inputMode="decimal" aria-label={`Free quantity of ${l.description}`} value={l.free} onChange={(e) => update(l.key, { free: e.target.value })} />
                      </td>
                      <td>
                        <input className="sb-input" inputMode="decimal" aria-label={`Rate of ${l.description}`} value={l.rate} onChange={(e) => update(l.key, { rate: e.target.value })} />
                      </td>
                      <td>
                        <input className="sb-input" inputMode="decimal" aria-label={`MRP of ${l.description}`} list={pack ? `mrps-${l.key}` : undefined} value={l.mrp} onChange={(e) => update(l.key, { mrp: e.target.value })} />
                        {pack ? (
                          <datalist id={`mrps-${l.key}`}>
                            {pack.mrps.map((m) => (
                              <option key={m} value={m} />
                            ))}
                          </datalist>
                        ) : null}
                      </td>
                      <td>
                        <input className="sb-input" inputMode="decimal" aria-label={`Discount percent of ${l.description}`} value={l.discount} onChange={(e) => update(l.key, { discount: e.target.value })} />
                      </td>
                      <td>
                        <input
                          className="sb-input"
                          inputMode="decimal"
                          aria-label={`GST rate of ${l.description}`}
                          placeholder={result ? String(result.gstRatePercent) : 'As item'}
                          title="Only when the supplier's invoice shows a different rate"
                          value={l.gst}
                          onChange={(e) => update(l.key, { gst: e.target.value })}
                        />
                      </td>
                      <td>
                        {l.tracksBatches !== false ? (
                          <input className="sb-input" aria-label={`Batch of ${l.description}`} placeholder="Batch" value={l.batch} onChange={(e) => update(l.key, { batch: e.target.value })} />
                        ) : null}
                        {l.tracksExpiry !== false ? (
                          <input className="sb-input" type="date" aria-label={`Expiry of ${l.description}`} value={l.expiry} onChange={(e) => update(l.key, { expiry: e.target.value })} />
                        ) : null}
                        {l.tracksBatches === false && l.tracksExpiry === false ? <span className="sb-muted">-</span> : null}
                      </td>
                      <td>
                        <input
                          className="sb-input"
                          inputMode="decimal"
                          aria-label={`Selling price of ${l.description}`}
                          placeholder="Current price"
                          value={l.selling}
                          onChange={(e) => update(l.key, { selling: e.target.value })}
                        />
                        {canSetPrices && l.selling.trim() !== '' ? (
                          <label className="sb-check">
                            <input type="checkbox" checked={l.updatePrice} onChange={(e) => update(l.key, { updatePrice: e.target.checked })} /> Set as new price
                          </label>
                        ) : null}
                      </td>
                      <td>
                        <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setLines((c) => c.filter((x) => x.key !== l.key))}>
                          Remove
                        </button>
                      </td>
                    </tr>,
                    <tr key={`${l.key}-result`} data-testid={`grn-line-result-${index + 1}`}>
                      <td colSpan={11}>
                        {result ? (
                          <span className="sb-muted">
                            Tax Rs. {moneyFormat.format(result.cgst + result.sgst + result.igst + result.cess)}
                            {result.expenseShare > 0 ? `, expenses Rs. ${moneyFormat.format(result.expenseShare)}` : ''}
                            {result.nonRecoverableTax > 0 ? `, tax not recoverable Rs. ${moneyFormat.format(result.nonRecoverableTax)}` : ''}
                            . Landed Rs. {moneyFormat.format(result.landedTotal)} = Rs. {result.landedUnitCost.toFixed(4)} per stock unit over {quantityFormat.format(result.baseQuantity)} units
                            {result.freeQuantity > 0 ? ' (free goods included)' : ''}.
                          </span>
                        ) : null}
                        {result?.costChange ? (
                          <p className={result.costChange.needsReason ? 'sb-error' : 'sb-muted'}>
                            Cost {result.costChange.percentChange > 0 ? 'up' : 'down'} {Math.abs(result.costChange.percentChange).toFixed(2)}% (Rs.{' '}
                            {result.costChange.previousUnitCost.toFixed(4)} to {result.costChange.newUnitCost.toFixed(4)} a unit) since {result.costChange.previousGrnNumber} from{' '}
                            {result.costChange.previousSupplier} on {result.costChange.previousDate}
                            {result.costChange.needsApproval ? '. A manager must approve this receipt.' : '.'}
                          </p>
                        ) : null}
                        {result?.costChange?.needsReason ? (
                          <input
                            className="sb-input"
                            aria-label={`Reason for the cost change of ${l.description}`}
                            placeholder="Reason for the cost change"
                            value={l.costReason}
                            onChange={(e) => update(l.key, { costReason: e.target.value })}
                          />
                        ) : null}
                        {result?.belowCost ? (
                          <>
                            <p className="sb-error">
                              Selling at Rs. {moneyFormat.format(result.belowCost.sellingPrice)} is below the landed cost of Rs. {moneyFormat.format(result.belowCost.costPerPack)} a pack (loss Rs.{' '}
                              {moneyFormat.format(result.belowCost.lossPerPack)}, margin {result.belowCost.marginPercent.toFixed(2)}%).
                            </p>
                            <input
                              className="sb-input"
                              aria-label={`Loss-leader reason for ${l.description}`}
                              placeholder="Loss-leader reason (if allowed)"
                              value={l.lossReason}
                              onChange={(e) => update(l.key, { lossReason: e.target.value })}
                            />
                          </>
                        ) : null}
                        {issues.map((i) => (
                          <p key={i.code} className="sb-error" role="alert">{i.message}</p>
                        ))}
                      </td>
                    </tr>,
                  ];
                })}
              </tbody>
            </table>
          ) : (
            <p className="sb-muted">No items added yet.</p>
          )}

          <h3>Freight and other expenses</h3>
          {expenses.map((e) => (
            <div className="sb-inline-form" key={e.key}>
              <select className="sb-input" aria-label="Expense kind" value={e.kind} onChange={(event) => updateExpense(e.key, { kind: event.target.value })}>
                {Object.entries(ExpenseKindLabels).map(([value, label]) => (
                  <option key={value} value={value}>{label}</option>
                ))}
              </select>
              <input className="sb-input" inputMode="decimal" aria-label="Expense amount" placeholder="Amount (Rs.)" value={e.amount} onChange={(event) => updateExpense(e.key, { amount: event.target.value })} />
              <select className="sb-input" aria-label="Spread the expense" value={e.method} onChange={(event) => updateExpense(e.key, { method: event.target.value })}>
                {Object.entries(AllocationMethodLabels).map(([value, label]) => (
                  <option key={value} value={value}>{label}</option>
                ))}
              </select>
              <input className="sb-input" aria-label="Expense note" placeholder="Note" value={e.note} onChange={(event) => updateExpense(e.key, { note: event.target.value })} />
              <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setExpenses((c) => c.filter((x) => x.key !== e.key))}>
                Remove
              </button>
            </div>
          ))}
          <div>
            <button
              type="button"
              className="sb-button sb-button--secondary"
              onClick={() => setExpenses((c) => [...c, { key: newKey(), kind: 'FREIGHT', amount: '', method: 'QUANTITY', note: '' }])}
            >
              Add expense
            </button>
          </div>

          <label className="sb-field">
            <span className="sb-field__label">Notes (optional)</span>
            <input className="sb-input" value={notes} maxLength={500} onChange={(event) => setNotes(event.target.value)} />
          </label>

          {missing ? <p className="sb-muted">{missing}</p> : null}
          <ErrorText error={previewError} />
          {previewMatches ? <GrnTotals grn={preview} /> : null}
          {generalIssues.map((i) => (
            <p key={i.code} className="sb-error" role="alert">{i.message}</p>
          ))}
          {previewMatches && preview.needsApproval && preview.issues.length === 0 ? (
            <Notice tone="warning">This receipt needs a manager&apos;s approval before its stock goes in.</Notice>
          ) : null}
          <ErrorText error={error} />
          <button className="sb-button" type="button" disabled={busy || !previewMatches || preview.issues.length > 0} onClick={() => void save()}>
            {busy ? 'Please wait...' : 'Save goods receipt'}
          </button>
        </div>
      </section>
    </>
  );
}

export function GrnTotals({ grn }: { grn: Grn }) {
  const rows: [string, number][] = [
    ['Gross', grn.grossTotal],
    ['Discount', -grn.discountTotal],
    ['Taxable', grn.taxableTotal],
    ...(grn.igstTotal > 0 ? [['IGST', grn.igstTotal] as [string, number]] : [['CGST', grn.cgstTotal] as [string, number], ['SGST', grn.sgstTotal] as [string, number]]),
    ...(grn.cessTotal > 0 ? [['Cess', grn.cessTotal] as [string, number]] : []),
    ...(grn.roundOff !== 0 ? [['Round off', grn.roundOff] as [string, number]] : []),
    ['Invoice total', grn.invoiceTotal],
    ['Expenses', grn.expensesTotal],
    ['Landed cost', grn.landedTotal],
  ];
  return (
    <table className="sb-table sb-table--compact" data-testid="grn-totals">
      <tbody>
        {rows.map(([label, value]) => (
          <tr key={label}>
            <th scope="row">{label}</th>
            <td className="sb-num">{moneyFormat.format(value)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
