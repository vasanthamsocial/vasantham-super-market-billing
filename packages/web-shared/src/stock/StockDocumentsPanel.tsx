'use client';

import { useState } from 'react';
import { api, ApiError } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  MovementTypeLabels,
  StockDocumentTypeLabels,
  StockPermission,
  type ProductDetail,
  type ProductSummary,
  type StockDocument,
  type StockDocumentSummary,
} from '../types';
import { ErrorText, formatDateTime, Notice } from '../ui';
import { moneyFormat, quantityFormat, StoreSelect, useStoreChoice } from './StockPanel';

interface DraftLine {
  key: string;
  product: ProductDetail;
  variantId: string;
  variantUnitId: string;
  quantity: string;
  direction: 'IN' | 'OUT';
  unitCost: string;
  batchNumber: string;
  expiresOn: string;
}

const typePermission: Record<string, string> = {
  OPENING: StockPermission.Adjust,
  ADJUSTMENT: StockPermission.Adjust,
  DAMAGE: StockPermission.Adjust,
  WASTAGE: StockPermission.Adjust,
  TRANSFER: StockPermission.Transfer,
  COUNT: StockPermission.Count,
};

function newKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
}

/** Posts stock documents (opening, adjustments, damage, wastage, transfers, counts) and lists recent ones. */
export function StockDocumentsPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const { stores, storeId, setStoreId, error: storesError } = useStoreChoice();
  const allowedTypes = Object.keys(StockDocumentTypeLabels).filter((t) => hasPermission(typePermission[t] ?? StockPermission.Settings));
  const [type, setType] = useState('ADJUSTMENT');
  const effectiveType = allowedTypes.includes(type) ? type : (allowedTypes[0] ?? '');
  const [targetStoreId, setTargetStoreId] = useState('');
  const [reason, setReason] = useState('');
  const [note, setNote] = useState('');
  const [override, setOverride] = useState(false);
  const [lines, setLines] = useState<DraftLine[]>([]);
  // One key per document: a retry after a lost response returns the same document instead of posting twice.
  const [idempotencyKey, setIdempotencyKey] = useState(newKey);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [posted, setPosted] = useState<StockDocument | null>(null);

  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const results = useApiData<ProductSummary[]>(business && query ? `${business}/catalog/products?take=10&search=${encodeURIComponent(query)}` : null);

  const targets = stores.filter((s) => s.id !== storeId);
  const target = targetStoreId && targets.some((s) => s.id === targetStoreId) ? targetStoreId : (targets[0]?.id ?? '');
  const incoming = (line: DraftLine) => effectiveType === 'OPENING' || (effectiveType === 'ADJUSTMENT' && line.direction === 'IN') || effectiveType === 'COUNT';
  const storeName = (id: string | null) => stores.find((s) => s.id === id)?.name ?? '-';

  function update(key: string, change: Partial<DraftLine>) {
    setLines((current) => current.map((l) => (l.key === key ? { ...l, ...change } : l)));
  }

  async function addProduct(productId: string) {
    if (!business) return;
    setError(null);
    try {
      const product = await api.get<ProductDetail>(`${business}/catalog/products/${productId}`);
      const variant = product.variants.find((v) => v.isActive) ?? product.variants[0];
      if (!variant) throw new Error(`${product.name} has no variants.`);
      const unit = variant.units.find((u) => u.isBase) ?? variant.units[0];
      if (!unit) throw new Error(`${product.name} has no packs.`);
      setLines((current) => [
        ...current,
        { key: newKey(), product, variantId: variant.id, variantUnitId: unit.id, quantity: '', direction: 'OUT', unitCost: '', batchNumber: '', expiresOn: '' },
      ]);
      setSearch('');
      setQuery('');
    } catch (caught) {
      setError(caught);
    }
  }

  async function post() {
    if (!business) return;
    setBusy(true);
    setError(null);
    setPosted(null);
    try {
      if (lines.length === 0) throw new Error('Add at least one item.');
      const body = {
        type: effectiveType,
        storeId,
        targetStoreId: effectiveType === 'TRANSFER' ? target : null,
        reason: reason.trim(),
        note: note.trim() || null,
        idempotencyKey,
        negativeStockOverride: override,
        lines: lines.map((l) => {
          const quantity = Number(l.quantity);
          if (l.quantity.trim() === '' || !Number.isFinite(quantity)) throw new Error(`Enter the quantity for ${l.product.name}.`);
          return {
            variantId: l.variantId,
            variantUnitId: l.variantUnitId,
            quantity,
            direction: effectiveType === 'ADJUSTMENT' ? l.direction : null,
            unitCost: incoming(l) && l.unitCost.trim() !== '' ? Number(l.unitCost) : null,
            batchNumber: incoming(l) && l.batchNumber.trim() !== '' ? l.batchNumber.trim() : null,
            expiresOn: incoming(l) && l.expiresOn ? l.expiresOn : null,
            manufacturedOn: null,
            batchId: null,
          };
        }),
      };
      const document = await api.post<StockDocument>(`${business}/stock/documents`, body);
      setPosted(document);
      setLines([]);
      setReason('');
      setNote('');
      setOverride(false);
      setIdempotencyKey(newKey());
    } catch (caught) {
      // The server answered and refused, so nothing was posted: corrections go out under a new key. Without an
      // answer (network failure) the key is kept, so pressing Post again cannot post the document twice.
      if (caught instanceof ApiError && caught.status !== 0) setIdempotencyKey(newKey());
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  if (allowedTypes.length === 0) {
    return <RecentStockDocuments business={business} storeId={storeId} storeName={storeName} refreshKey={posted?.id ?? ''} />;
  }

  return (
    <>
      <section className="sb-card" aria-labelledby="post-stock-heading">
        <header className="sb-card__header">
          <h2 id="post-stock-heading">Post a stock document</h2>
        </header>
        <ErrorText error={storesError} />
        {posted ? (
          <Notice tone="success">
            Posted {posted.number}: {posted.movements.length} movement{posted.movements.length === 1 ? '' : 's'}.
          </Notice>
        ) : null}
        <div className="sb-form" data-testid="stock-document-form">
          <div className="sb-form-row">
            <label className="sb-field">
              <span className="sb-field__label">Document type</span>
              <select className="sb-input" value={effectiveType} onChange={(event) => setType(event.target.value)}>
                {allowedTypes.map((t) => (
                  <option key={t} value={t}>{StockDocumentTypeLabels[t]}</option>
                ))}
              </select>
            </label>
            <StoreSelect stores={stores} value={storeId} onChange={setStoreId} label={effectiveType === 'TRANSFER' ? 'From store' : 'Store'} />
            {effectiveType === 'TRANSFER' ? (
              targets.length > 0 ? (
                <StoreSelect stores={targets} value={target} onChange={setTargetStoreId} label="To store" />
              ) : (
                <p className="sb-muted">Add a second store to transfer stock.</p>
              )
            ) : null}
          </div>
          {effectiveType === 'COUNT' ? <Notice>Enter the quantity you counted. The difference from the books is posted as a gain or loss.</Notice> : null}

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
              aria-label="Find an item to add"
              placeholder="Item name, code or barcode"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
            />
            <button className="sb-button sb-button--secondary" type="submit">Find item</button>
          </form>
          {query && results.data ? (
            <ul className="sb-plain-list" aria-label="Matching items">
              {results.data.length === 0 ? <li className="sb-muted">No matching items.</li> : null}
              {results.data.map((p) => (
                <li key={p.id}>
                  <button type="button" className="sb-link" onClick={() => void addProduct(p.id)}>
                    Add {p.code} - {p.name}
                  </button>
                </li>
              ))}
            </ul>
          ) : null}

          {lines.length > 0 ? (
            <table className="sb-table" data-testid="stock-lines">
              <thead>
                <tr>
                  <th>Item</th>
                  <th>Pack</th>
                  {effectiveType === 'ADJUSTMENT' ? <th>In / out</th> : null}
                  <th>{effectiveType === 'COUNT' ? 'Counted' : 'Quantity'}</th>
                  <th>Cost per pack (Rs.)</th>
                  <th>Batch / expiry</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {lines.map((l) => {
                  const variant = l.product.variants.find((v) => v.id === l.variantId) ?? l.product.variants[0];
                  return (
                    <tr key={l.key}>
                      <td>
                        {l.product.name}
                        {l.product.variants.length > 1 ? (
                          <select
                            className="sb-input"
                            aria-label={`Variant of ${l.product.name}`}
                            value={l.variantId}
                            onChange={(event) => {
                              const v = l.product.variants.find((x) => x.id === event.target.value)!;
                              update(l.key, { variantId: v.id, variantUnitId: (v.units.find((u) => u.isBase) ?? v.units[0])?.id ?? '' });
                            }}
                          >
                            {l.product.variants.map((v) => (
                              <option key={v.id} value={v.id}>{v.name}</option>
                            ))}
                          </select>
                        ) : null}
                      </td>
                      <td>
                        <select
                          className="sb-input"
                          aria-label={`Pack of ${l.product.name}`}
                          value={l.variantUnitId}
                          onChange={(event) => update(l.key, { variantUnitId: event.target.value })}
                        >
                          {(variant?.units ?? []).map((u) => (
                            <option key={u.id} value={u.id}>{u.unitCode}{u.isBase ? '' : ` (${u.factorToBase} ${l.product.baseUnitCode})`}</option>
                          ))}
                        </select>
                      </td>
                      {effectiveType === 'ADJUSTMENT' ? (
                        <td>
                          <select
                            className="sb-input"
                            aria-label={`Direction for ${l.product.name}`}
                            value={l.direction}
                            onChange={(event) => update(l.key, { direction: event.target.value as 'IN' | 'OUT' })}
                          >
                            <option value="OUT">Out</option>
                            <option value="IN">In</option>
                          </select>
                        </td>
                      ) : null}
                      <td>
                        <input
                          className="sb-input"
                          inputMode="decimal"
                          aria-label={`Quantity of ${l.product.name}`}
                          value={l.quantity}
                          onChange={(event) => update(l.key, { quantity: event.target.value })}
                        />
                      </td>
                      <td>
                        {incoming(l) ? (
                          <input
                            className="sb-input"
                            inputMode="decimal"
                            aria-label={`Cost of ${l.product.name}`}
                            placeholder={effectiveType === 'OPENING' ? 'Required' : 'Current cost'}
                            value={l.unitCost}
                            onChange={(event) => update(l.key, { unitCost: event.target.value })}
                          />
                        ) : (
                          <span className="sb-muted">At stock cost</span>
                        )}
                      </td>
                      <td>
                        {incoming(l) && l.product.tracksBatches ? (
                          <>
                            <input
                              className="sb-input"
                              aria-label={`Batch of ${l.product.name}`}
                              placeholder="Batch number"
                              value={l.batchNumber}
                              onChange={(event) => update(l.key, { batchNumber: event.target.value })}
                            />
                            {l.product.tracksExpiry ? (
                              <input
                                className="sb-input"
                                type="date"
                                aria-label={`Expiry of ${l.product.name}`}
                                value={l.expiresOn}
                                onChange={(event) => update(l.key, { expiresOn: event.target.value })}
                              />
                            ) : null}
                          </>
                        ) : (
                          <span className="sb-muted">{l.product.tracksBatches ? (effectiveType === 'TRANSFER' ? 'Batches move as stocked' : 'Valuation order') : '-'}</span>
                        )}
                      </td>
                      <td>
                        <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setLines((c) => c.filter((x) => x.key !== l.key))}>
                          Remove
                        </button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          ) : (
            <p className="sb-muted">No items added yet.</p>
          )}

          <div className="sb-form-row">
            <div className="sb-field">
              <label className="sb-field__label" htmlFor="stock-reason">Reason</label>
              <input id="stock-reason" className="sb-input" value={reason} maxLength={200} onChange={(event) => setReason(event.target.value)} />
            </div>
            <div className="sb-field">
              <label className="sb-field__label" htmlFor="stock-note">Note (optional)</label>
              <input id="stock-note" className="sb-input" value={note} onChange={(event) => setNote(event.target.value)} />
            </div>
          </div>
          {hasPermission(StockPermission.NegativeOverride) && effectiveType !== 'OPENING' ? (
            <label className="sb-check">
              <input type="checkbox" checked={override} onChange={(event) => setOverride(event.target.checked)} /> Confirm taking stock below zero (where the rule allows an override)
            </label>
          ) : null}
          <ErrorText error={error} />
          <button className="sb-button" type="button" disabled={busy} onClick={() => void post()}>
            {busy ? 'Please wait...' : 'Post document'}
          </button>
        </div>
      </section>
      <RecentStockDocuments business={business} storeId={storeId} storeName={storeName} refreshKey={posted?.id ?? ''} />
    </>
  );
}

function RecentStockDocuments({
  business,
  storeId,
  storeName,
  refreshKey,
}: {
  business: string | null;
  storeId: string;
  storeName: (id: string | null) => string;
  refreshKey: string;
}) {
  const documents = useApiData<StockDocumentSummary[]>(business && storeId ? `${business}/stock/documents?storeId=${storeId}&r=${refreshKey}` : null);
  const [opened, setOpened] = useState<StockDocument | null>(null);
  const [error, setError] = useState<unknown>(null);

  return (
    <section className="sb-card" aria-labelledby="stock-docs-heading">
      <header className="sb-card__header">
        <h2 id="stock-docs-heading">Recent stock documents</h2>
      </header>
      <ErrorText error={documents.error ?? error} />
      <table className="sb-table" data-testid="stock-documents-table">
        <thead>
          <tr>
            <th>Number</th>
            <th>Type</th>
            <th>Date</th>
            <th>Reason</th>
            <th>By</th>
          </tr>
        </thead>
        <tbody>
          {(documents.data ?? []).map((d) => (
            <tr key={d.id}>
              <td>
                <button
                  type="button"
                  className="sb-link"
                  onClick={async () => {
                    setError(null);
                    try {
                      setOpened(await api.get<StockDocument>(`${business}/stock/documents/${d.id}`));
                    } catch (caught) {
                      setError(caught);
                    }
                  }}
                >
                  {d.number}
                </button>
              </td>
              <td>{StockDocumentTypeLabels[d.type] ?? d.type}{d.targetStoreId ? ` (${storeName(d.storeId)} to ${storeName(d.targetStoreId)})` : ''}</td>
              <td>{d.businessDate}</td>
              <td>{d.reason}</td>
              <td>{d.postedBy}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {documents.data && documents.data.length === 0 ? <p className="sb-muted">No stock documents in this store yet.</p> : null}
      {opened ? <DocumentView document={opened} storeName={storeName} /> : null}
    </section>
  );
}

function DocumentView({ document, storeName }: { document: StockDocument; storeName: (id: string | null) => string }) {
  return (
    <section aria-labelledby="stock-doc-view-heading" data-testid="stock-document-view">
      <h3 id="stock-doc-view-heading">
        {document.number}: {StockDocumentTypeLabels[document.type] ?? document.type}
      </h3>
      <p className="sb-muted">
        {document.reason}{document.note ? ` - ${document.note}` : ''}. Posted by {document.postedBy} on {formatDateTime(document.postedAtUtc)}
        {document.negativeStockOverride ? ' with a below-zero override' : ''}.
      </p>
      <table className="sb-table">
        <thead>
          <tr>
            <th>Store</th>
            <th>Item</th>
            <th>Movement</th>
            <th>Batch</th>
            <th className="sb-num">Quantity</th>
            <th className="sb-num">Cost</th>
            <th className="sb-num">Value (Rs.)</th>
          </tr>
        </thead>
        <tbody>
          {document.movements.map((m) => (
            <tr key={m.sequence}>
              <td>{storeName(m.storeId)}</td>
              <td>{m.variantName}</td>
              <td>{MovementTypeLabels[m.movementType] ?? m.movementType}</td>
              <td>{m.batchNumber ?? '-'}</td>
              <td className="sb-num">{quantityFormat.format(m.quantity)}</td>
              <td className="sb-num">{moneyFormat.format(m.unitCost)}</td>
              <td className="sb-num">{moneyFormat.format(m.value)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {document.movements.length === 0 ? <p className="sb-muted">No difference: nothing was posted.</p> : null}
    </section>
  );
}
