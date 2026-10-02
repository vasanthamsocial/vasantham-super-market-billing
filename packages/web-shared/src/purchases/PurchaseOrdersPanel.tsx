'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { OrderProgressLabels, PurchasePermission, type ProductDetail, type PurchaseOrder, type Supplier } from '../types';
import { ErrorText, Notice } from '../ui';
import { moneyFormat, quantityFormat, StoreSelect, useStoreChoice } from '../stock/StockPanel';
import { ItemPicker, newKey, parseNumber, SupplierSelect } from './PurchaseShared';

interface DraftOrderLine {
  key: string;
  description: string;
  packs: { id: string; label: string }[];
  variantUnitId: string;
  quantity: string;
  rate: string;
}

const statusLabels: Record<string, string> = { OPEN: 'Open', CLOSED: 'Closed', CANCELLED: 'Cancelled' };

/** Purchase orders: what was ordered, what has arrived against them, and what is still outstanding. */
export function PurchaseOrdersPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const { stores, storeId, setStoreId, error: storesError } = useStoreChoice();
  const [status, setStatus] = useState('OPEN');
  const orders = useApiData<PurchaseOrder[]>(business && storeId ? `${business}/purchase-orders?storeId=${storeId}${status ? `&status=${status}` : ''}` : null);
  const [opened, setOpened] = useState<PurchaseOrder | null>(null);
  const [error, setError] = useState<unknown>(null);
  const canManage = hasPermission(PurchasePermission.Manage);

  async function act(order: PurchaseOrder, action: 'close' | 'cancel') {
    setError(null);
    try {
      setOpened(await api.post<PurchaseOrder>(`${business}/purchase-orders/${order.id}/${action}`));
      await orders.reload();
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <>
      {canManage ? <NewOrderCard business={business} storeId={storeId} onPlaced={async (order) => { setOpened(order); await orders.reload(); }} /> : null}
      <section className="sb-card" aria-labelledby="orders-heading">
        <header className="sb-card__header">
          <h2 id="orders-heading">Purchase orders</h2>
        </header>
        <ErrorText error={storesError ?? orders.error ?? error} />
        <div className="sb-inline-form">
          <StoreSelect stores={stores} value={storeId} onChange={(id) => { setStoreId(id); setOpened(null); }} />
          <label className="sb-field">
            <span className="sb-field__label">Status</span>
            <select className="sb-input" value={status} onChange={(event) => setStatus(event.target.value)}>
              <option value="">All</option>
              {Object.entries(statusLabels).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
          </label>
        </div>
        <table className="sb-table" data-testid="orders-table">
          <thead>
            <tr>
              <th>Number</th>
              <th>Date</th>
              <th>Supplier</th>
              <th>Expected</th>
              <th>Status</th>
              <th>Received</th>
            </tr>
          </thead>
          <tbody>
            {(orders.data ?? []).map((o) => (
              <tr key={o.id}>
                <td>
                  <button type="button" className="sb-link" onClick={() => setOpened(o)}>{o.number}</button>
                </td>
                <td>{o.orderDate}</td>
                <td>{o.supplierName}</td>
                <td>{o.expectedDate ?? '-'}</td>
                <td>{statusLabels[o.status] ?? o.status}</td>
                <td>{OrderProgressLabels[o.progress] ?? o.progress}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {orders.data && orders.data.length === 0 ? <p className="sb-muted">No purchase orders here.</p> : null}

        {opened ? (
          <section aria-labelledby="order-view-heading" data-testid="order-view">
            <h3 id="order-view-heading">
              {opened.number}: {opened.supplierName} ({statusLabels[opened.status]}, {OrderProgressLabels[opened.progress]?.toLowerCase()})
            </h3>
            {opened.notes ? <p className="sb-muted">{opened.notes}</p> : null}
            <table className="sb-table">
              <thead>
                <tr>
                  <th>Item</th>
                  <th className="sb-num">Ordered</th>
                  <th className="sb-num">Received</th>
                  <th className="sb-num">Outstanding</th>
                  <th className="sb-num">Agreed rate</th>
                </tr>
              </thead>
              <tbody>
                {opened.lines.map((l) => (
                  <tr key={l.lineNumber}>
                    <td>{l.description} ({l.unitCode})</td>
                    <td className="sb-num">{quantityFormat.format(l.ordered)}</td>
                    <td className="sb-num">{quantityFormat.format(l.received)}</td>
                    <td className="sb-num">{quantityFormat.format(l.outstanding)}</td>
                    <td className="sb-num">{l.rate === null ? '-' : moneyFormat.format(l.rate)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="sb-muted">Receipts: {opened.receiptNumbers.length > 0 ? opened.receiptNumbers.join(', ') : 'none yet'}.</p>
            {canManage && opened.status === 'OPEN' ? (
              <div className="sb-inline-form">
                <button type="button" className="sb-button sb-button--secondary" onClick={() => void act(opened, 'close')}>
                  Close (no more expected)
                </button>
                {opened.receiptNumbers.length === 0 ? (
                  <button type="button" className="sb-button sb-button--secondary" onClick={() => void act(opened, 'cancel')}>
                    Cancel order
                  </button>
                ) : null}
              </div>
            ) : null}
          </section>
        ) : null}
      </section>
    </>
  );
}

function NewOrderCard({ business, storeId, onPlaced }: { business: string | null; storeId: string; onPlaced: (order: PurchaseOrder) => Promise<void> }) {
  const suppliers = useApiData<Supplier[]>(business ? `${business}/suppliers` : null);
  const [supplierId, setSupplierId] = useState('');
  const [expected, setExpected] = useState('');
  const [notes, setNotes] = useState('');
  const [lines, setLines] = useState<DraftOrderLine[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [placed, setPlaced] = useState<string | null>(null);

  function add(product: ProductDetail) {
    const packs = product.variants
      .filter((v) => v.isActive)
      .flatMap((v) => v.units.map((u) => ({ id: u.id, label: `${product.variants.length > 1 ? `${v.name} - ` : ''}${u.unitCode}` })));
    setLines((current) => [...current, { key: newKey(), description: product.name, packs, variantUnitId: packs[0]?.id ?? '', quantity: '', rate: '' }]);
  }

  function update(key: string, change: Partial<DraftOrderLine>) {
    setLines((current) => current.map((l) => (l.key === key ? { ...l, ...change } : l)));
  }

  async function place() {
    setBusy(true);
    setError(null);
    setPlaced(null);
    try {
      if (!supplierId) throw new Error('Choose the supplier.');
      if (lines.length === 0) throw new Error('Add the items to order.');
      const body = {
        storeId,
        supplierId,
        expectedDate: expected || null,
        notes: notes.trim() || null,
        lines: lines.map((l) => {
          const quantity = parseNumber(l.quantity);
          const rate = parseNumber(l.rate);
          if (quantity === null || Number.isNaN(quantity)) throw new Error(`Enter the quantity of ${l.description}.`);
          if (rate !== null && Number.isNaN(rate)) throw new Error(`Check the rate of ${l.description}.`);
          return { variantUnitId: l.variantUnitId, quantity, rate };
        }),
      };
      const order = await api.post<PurchaseOrder>(`${business}/purchase-orders`, body);
      setPlaced(order.number);
      setLines([]);
      setNotes('');
      setExpected('');
      await onPlaced(order);
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="sb-card" aria-labelledby="new-order-heading">
      <header className="sb-card__header">
        <h2 id="new-order-heading">New purchase order</h2>
      </header>
      {placed ? <Notice tone="success">Placed order {placed}.</Notice> : null}
      <div className="sb-form" data-testid="order-form">
        <div className="sb-form-row">
          <SupplierSelect suppliers={suppliers.data ?? []} value={supplierId} onChange={setSupplierId} />
          <label className="sb-field">
            <span className="sb-field__label">Expected by (optional)</span>
            <input className="sb-input" type="date" value={expected} onChange={(event) => setExpected(event.target.value)} />
          </label>
        </div>
        <ItemPicker business={business} onPick={add} />
        {lines.length > 0 ? (
          <table className="sb-table">
            <thead>
              <tr>
                <th>Item</th>
                <th>Pack</th>
                <th>Quantity</th>
                <th>Agreed rate (optional)</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {lines.map((l) => (
                <tr key={l.key}>
                  <td>{l.description}</td>
                  <td>
                    <select className="sb-input" aria-label={`Pack of ${l.description}`} value={l.variantUnitId} onChange={(event) => update(l.key, { variantUnitId: event.target.value })}>
                      {l.packs.map((p) => (
                        <option key={p.id} value={p.id}>{p.label}</option>
                      ))}
                    </select>
                  </td>
                  <td>
                    <input className="sb-input" inputMode="decimal" aria-label={`Order quantity of ${l.description}`} value={l.quantity} onChange={(e) => update(l.key, { quantity: e.target.value })} />
                  </td>
                  <td>
                    <input className="sb-input" inputMode="decimal" aria-label={`Agreed rate of ${l.description}`} value={l.rate} onChange={(e) => update(l.key, { rate: e.target.value })} />
                  </td>
                  <td>
                    <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setLines((c) => c.filter((x) => x.key !== l.key))}>
                      Remove
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : null}
        <label className="sb-field">
          <span className="sb-field__label">Notes (optional)</span>
          <input className="sb-input" value={notes} maxLength={500} onChange={(event) => setNotes(event.target.value)} />
        </label>
        <ErrorText error={error ?? suppliers.error} />
        <button className="sb-button" type="button" disabled={busy} onClick={() => void place()}>
          {busy ? 'Please wait...' : 'Place order'}
        </button>
      </div>
    </section>
  );
}
