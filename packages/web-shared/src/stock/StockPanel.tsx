'use client';

import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { MovementTypeLabels, type BatchStock, type StockLedgerEntry, type StockOnHand, type StockValuation, type Store } from '../types';
import { ErrorText, formatDateTime } from '../ui';

export const quantityFormat = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });
export const moneyFormat = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** Active stores of the business, and the one currently chosen (the first by default). */
export function useStoreChoice() {
  const { membership } = useAuth();
  const stores = useApiData<Store[]>(membership ? `/api/v1/businesses/${membership.businessId}/stores` : null);
  const active = (stores.data ?? []).filter((s) => s.isActive);
  const [chosen, setChosen] = useState('');
  const storeId = chosen || active[0]?.id || '';
  return { stores: active, storeId, setStoreId: setChosen, error: stores.error };
}

export function StoreSelect({ stores, value, onChange, label = 'Store' }: { stores: Store[]; value: string; onChange: (id: string) => void; label?: string }) {
  return (
    <label className="sb-field">
      <span className="sb-field__label">{label}</span>
      <select className="sb-input" value={value} onChange={(event) => onChange(event.target.value)}>
        {stores.map((s) => (
          <option key={s.id} value={s.id}>{s.code} - {s.name}</option>
        ))}
      </select>
    </label>
  );
}

/** Stock on hand in a store, with value, reorder status, item ledgers and batches nearing expiry. */
export function StockPanel() {
  const { membership } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/stock` : null;
  const { stores, storeId, setStoreId, error: storesError } = useStoreChoice();
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [lowOnly, setLowOnly] = useState(false);
  const [ledgerFor, setLedgerFor] = useState<StockOnHand | null>(null);

  const onHand = useApiData<StockOnHand[]>(
    base && storeId ? `${base}/on-hand?storeId=${storeId}&lowOnly=${lowOnly}${query ? `&search=${encodeURIComponent(query)}` : ''}` : null,
  );
  const valuation = useApiData<StockValuation[]>(base ? `${base}/valuation` : null);
  const expiring = useApiData<BatchStock[]>(base && storeId ? `${base}/batches?storeId=${storeId}&expiringWithinDays=30` : null);
  const ledger = useApiData<StockLedgerEntry[]>(base && storeId && ledgerFor ? `${base}/ledger?storeId=${storeId}&variantId=${ledgerFor.variantId}` : null);
  const storeValue = valuation.data?.find((v) => v.storeId === storeId);

  return (
    <section className="sb-card" aria-labelledby="stock-heading">
      <header className="sb-card__header">
        <h2 id="stock-heading">Stock on hand</h2>
        {storeValue ? (
          <p className="sb-muted" data-testid="stock-value">
            {storeValue.items} items, value Rs. {moneyFormat.format(storeValue.totalValue)} ({storeValue.valuationMethod.replace('_', ' ').toLowerCase()})
            {storeValue.negativeItems > 0 ? `, ${storeValue.negativeItems} below zero` : ''}
          </p>
        ) : null}
      </header>
      <ErrorText error={storesError} />
      <div className="sb-inline-form">
        <StoreSelect stores={stores} value={storeId} onChange={(id) => { setStoreId(id); setLedgerFor(null); }} />
        <form
          className="sb-inline-form"
          role="search"
          onSubmit={(event) => {
            event.preventDefault();
            setQuery(search.trim());
          }}
        >
          <input
            className="sb-input"
            aria-label="Search stock by item name or code"
            placeholder="Item name or code"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <button className="sb-button" type="submit">Search</button>
        </form>
        <label className="sb-check">
          <input type="checkbox" checked={lowOnly} onChange={(event) => setLowOnly(event.target.checked)} /> Low or negative only
        </label>
      </div>
      <ErrorText error={onHand.error} />
      <table className="sb-table" data-testid="stock-table">
        <thead>
          <tr>
            <th>Code</th>
            <th>Item</th>
            <th className="sb-num">Quantity</th>
            <th className="sb-num">Average cost</th>
            <th className="sb-num">Value (Rs.)</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {(onHand.data ?? []).map((s) => (
            <tr key={s.variantId}>
              <td>{s.variantCode}</td>
              <td>
                <button type="button" className="sb-link" onClick={() => setLedgerFor(s)}>{s.variantName}</button>
              </td>
              <td className="sb-num">{quantityFormat.format(s.quantity)} {s.unitCode}</td>
              <td className="sb-num">{moneyFormat.format(s.averageCost)}</td>
              <td className="sb-num">{moneyFormat.format(s.value)}</td>
              <td>{s.isNegative ? 'Below zero' : s.isLow ? `Low (reorder ${quantityFormat.format(s.reorderQuantity ?? 0)})` : 'OK'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {onHand.data && onHand.data.length === 0 ? <p className="sb-muted">No stock recorded in this store yet.</p> : null}

      {ledgerFor ? (
        <section aria-labelledby="ledger-heading" data-testid="stock-ledger">
          <h3 id="ledger-heading">Movements of {ledgerFor.variantName}</h3>
          <ErrorText error={ledger.error} />
          <table className="sb-table">
            <thead>
              <tr>
                <th>When</th>
                <th>Movement</th>
                <th>Document</th>
                <th>Batch</th>
                <th className="sb-num">Quantity</th>
                <th className="sb-num">Cost</th>
                <th className="sb-num">Balance</th>
                <th>By</th>
              </tr>
            </thead>
            <tbody>
              {(ledger.data ?? []).map((e) => (
                <tr key={e.sequence}>
                  <td>{formatDateTime(e.occurredAtUtc)}</td>
                  <td>{MovementTypeLabels[e.movementType] ?? e.movementType}</td>
                  <td>{e.documentNumber ?? '-'}</td>
                  <td>{e.batchNumber ?? '-'}</td>
                  <td className="sb-num">{quantityFormat.format(e.quantity)}</td>
                  <td className="sb-num">{moneyFormat.format(e.unitCost)}</td>
                  <td className="sb-num">{quantityFormat.format(e.balanceAfter)}</td>
                  <td>{e.postedBy}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ) : null}

      {expiring.data && expiring.data.length > 0 ? (
        <section aria-labelledby="expiry-heading">
          <h3 id="expiry-heading">Expired or expiring within 30 days</h3>
          <table className="sb-table" data-testid="expiry-table">
            <thead>
              <tr>
                <th>Item</th>
                <th>Batch</th>
                <th>Expires</th>
                <th className="sb-num">Quantity</th>
              </tr>
            </thead>
            <tbody>
              {expiring.data.map((b) => (
                <tr key={b.batchId}>
                  <td>{b.variantName}</td>
                  <td>{b.batchNumber}</td>
                  <td>{b.expiresOn} {b.isExpired ? '(expired)' : `(${b.daysToExpiry} days)`}</td>
                  <td className="sb-num">{quantityFormat.format(b.quantity)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ) : null}
    </section>
  );
}
