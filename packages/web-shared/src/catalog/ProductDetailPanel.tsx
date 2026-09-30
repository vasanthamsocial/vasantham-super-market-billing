'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  CatalogPermission,
  RateTypeLabels,
  type PriceQuote,
  type PriceRuleInfo,
  type ProductDetail,
  type UnitInfo,
  type VariantInfo,
} from '../types';
import { ActionForm, ErrorText, Field, formatDateTime, Notice, optional, text } from '../ui';

const money = (value: number | null | undefined) => (value == null ? '-' : `Rs. ${value.toFixed(2)}`);

export function ProductDetailPanel({ productId }: { productId: string }) {
  const { membership } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const product = useApiData<ProductDetail>(base ? `${base}/catalog/products/${productId}` : null);
  const units = useApiData<UnitInfo[]>(base ? `${base}/catalog/units` : null);

  if (product.error) return <ErrorText error={product.error} />;
  const p = product.data;
  if (!p || !base) return <p className="sb-muted">Loading...</p>;

  return (
    <div className="sb-stack">
      <section className="sb-card" aria-labelledby="product-heading">
        <header className="sb-card__header">
          <h1 id="product-heading" data-testid="product-name">{p.name}</h1>
          <span className="sb-muted">{p.code}</span>
        </header>
        <dl className="sb-status-grid">
          <dt>HSN/SAC</dt>
          <dd>{p.hsnSac}</dd>
          <dt>GST</dt>
          <dd>{p.supplyType === 'TAXABLE' ? `${p.gstRatePercent}%${p.cessRatePercent ? ` + cess ${p.cessRatePercent}%` : ''}` : p.supplyType.replace('_', ' ').toLowerCase()}</dd>
          <dt>Stock unit</dt>
          <dd>{p.baseUnitCode}</dd>
          <dt>Tracking</dt>
          <dd>{[p.isWeighed && 'sold by weight', p.tracksBatches && 'batches', p.tracksExpiry && 'expiry'].filter(Boolean).join(', ') || 'none'}</dd>
        </dl>
      </section>
      {p.variants.map((variant) => (
        <VariantCard key={variant.id} base={base} variant={variant} units={units.data ?? []} onChanged={product.reload} />
      ))}
    </div>
  );
}

function VariantCard({ base, variant, units, onChanged }: { base: string; variant: VariantInfo; units: UnitInfo[]; onChanged: () => Promise<void> }) {
  const { hasPermission } = useAuth();
  const canManage = hasPermission(CatalogPermission.Manage);
  const canPrice = hasPermission(CatalogPermission.PricesManage);
  const prices = useApiData<PriceRuleInfo[]>(`${base}/prices/variants/${variant.id}?includeClosed=true`);
  const [message, setMessage] = useState<string | null>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  const packLabel = (id: string) => {
    const pack = variant.units.find((u) => u.id === id);
    return pack ? (pack.isBase ? pack.unitCode : `${pack.unitCode} (${pack.factorToBase})`) : '?';
  };
  const packOptions = variant.units.map((u) => (
    <option key={u.id} value={u.id}>{packLabel(u.id)}</option>
  ));

  return (
    <section className="sb-card" aria-label={`Variant ${variant.name}`} data-testid="variant-card">
      <header className="sb-card__header">
        <h2>{variant.name}</h2>
        <span className="sb-muted">SKU {variant.code}</span>
      </header>
      {message ? <Notice tone="success">{message}</Notice> : null}
      <ErrorText error={actionError} />

      <h3>Packs, barcodes and MRPs</h3>
      <table className="sb-table" data-testid="packs-table">
        <thead>
          <tr>
            <th>Pack</th>
            <th>Barcodes</th>
            <th>MRPs</th>
          </tr>
        </thead>
        <tbody>
          {variant.units.map((pack) => (
            <tr key={pack.id}>
              <td>{pack.isBase ? `${pack.unitCode} (stock unit)` : `${pack.unitCode} = ${pack.factorToBase} x stock unit`}</td>
              <td>{variant.barcodes.filter((b) => b.variantUnitId === pack.id && b.isActive).map((b) => b.code).join(', ') || '-'}</td>
              <td>{variant.mrps.filter((m) => m.variantUnitId === pack.id && m.isActive).map((m) => money(m.mrp)).join(', ') || '-'}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {canManage ? (
        <div className="sb-form-row">
          <details className="sb-details">
            <summary>Add a pack size</summary>
            <ActionForm submitLabel="Add pack" onSubmit={async (data) => {
              await api.post(`${base}/catalog/variants/${variant.id}/units`, { unitId: text(data, 'unitId'), factorToBase: Number(text(data, 'factor')) });
              setMessage('Pack added.');
              await onChanged();
            }}>
              <label className="sb-field">
                <span className="sb-field__label">Unit</span>
                <select className="sb-input" name="unitId">{units.map((u) => <option key={u.id} value={u.id}>{u.code}</option>)}</select>
              </label>
              <Field label="Contains how many stock units" name="factor" inputMode="decimal" required />
            </ActionForm>
          </details>
          <details className="sb-details">
            <summary>Add a barcode</summary>
            <ActionForm testId="add-barcode-form" submitLabel="Add barcode" onSubmit={async (data) => {
              await api.post(`${base}/catalog/variants/${variant.id}/barcodes`, { variantUnitId: text(data, 'variantUnitId'), code: text(data, 'code') });
              setMessage('Barcode added.');
              await onChanged();
            }}>
              <label className="sb-field">
                <span className="sb-field__label">Pack</span>
                <select className="sb-input" name="variantUnitId">{packOptions}</select>
              </label>
              <Field label="Barcode" name="code" required />
            </ActionForm>
          </details>
          <details className="sb-details">
            <summary>Add an MRP</summary>
            <ActionForm submitLabel="Add MRP" onSubmit={async (data) => {
              await api.post(`${base}/catalog/variants/${variant.id}/mrps`, { variantUnitId: text(data, 'variantUnitId'), mrp: Number(text(data, 'mrp')), effectiveFrom: null });
              setMessage('MRP added. Older MRPs stay valid for stock still carrying them.');
              await onChanged();
            }}>
              <label className="sb-field">
                <span className="sb-field__label">Pack</span>
                <select className="sb-input" name="variantUnitId">{packOptions}</select>
              </label>
              <Field label="MRP (Rs.)" name="mrp" inputMode="decimal" required />
            </ActionForm>
          </details>
        </div>
      ) : null}

      <h3>Prices</h3>
      <ErrorText error={prices.error} />
      <table className="sb-table" data-testid="prices-table">
        <thead>
          <tr>
            <th>Type</th>
            <th>Pack</th>
            <th>Price</th>
            <th>Conditions</th>
            <th>Valid</th>
            <th>Status</th>
            {canPrice ? <th /> : null}
          </tr>
        </thead>
        <tbody>
          {(prices.data ?? []).map((rule) => (
            <tr key={rule.id} className={rule.status === 'ACTIVE' || rule.status === 'PENDING_APPROVAL' ? undefined : 'sb-row--closed'}>
              <td>{RateTypeLabels[rule.rateType] ?? rule.rateType}</td>
              <td>{rule.unitCode}</td>
              <td>{money(rule.price)} {rule.taxInclusive ? 'incl. tax' : '+ tax'}</td>
              <td>
                {[
                  rule.channel !== 'ANY' && rule.channel.toLowerCase(),
                  rule.membersOnly && 'members',
                  rule.minQuantity > 0 && `from ${rule.minQuantity}`,
                  rule.maxQuantity != null && `up to ${rule.maxQuantity}`,
                  rule.mrp != null && `MRP ${money(rule.mrp)} stock`,
                ].filter(Boolean).join(', ') || '-'}
              </td>
              <td>{formatDateTime(rule.validFromUtc)}{rule.validToUtc ? ` - ${formatDateTime(rule.validToUtc)}` : ''}</td>
              <td>{rule.status.replace('_', ' ').toLowerCase()}</td>
              {canPrice ? (
                <td>
                  {rule.status === 'ACTIVE' || rule.status === 'PENDING_APPROVAL' ? (
                    <button type="button" className="sb-button sb-button--small sb-button--secondary" onClick={async () => {
                      setActionError(null);
                      try {
                        await api.post(`${base}/prices/${rule.id}/retire`);
                        await prices.reload();
                      } catch (error) {
                        setActionError(error);
                      }
                    }}>
                      Retire
                    </button>
                  ) : null}
                </td>
              ) : null}
            </tr>
          ))}
        </tbody>
      </table>

      {canPrice ? (
        <details className="sb-details">
          <summary>Add a price</summary>
          <p className="sb-muted">Prices are never edited: to change one, add the new price and retire the old one. Past bills keep the price they used.</p>
          <ActionForm testId="add-price-form" submitLabel="Add price" onSubmit={async (data) => {
            const rateType = text(data, 'rateType');
            const validTo = optional(data, 'validTo');
            const result = await api.post<{ message: string }>(`${base}/prices/variants/${variant.id}`, {
              variantUnitId: text(data, 'variantUnitId'),
              rateType,
              channel: text(data, 'channel'),
              price: Number(text(data, 'price')),
              taxInclusive: data.get('taxInclusive') === 'on',
              mrp: optional(data, 'mrp') ? Number(text(data, 'mrp')) : null,
              storeId: null,
              customerGroupId: null,
              membersOnly: rateType === 'MEMBER',
              minQuantity: Number(text(data, 'minQuantity') || 0),
              maxQuantity: optional(data, 'maxQuantity') ? Number(text(data, 'maxQuantity')) : null,
              validFromUtc: null,
              validToUtc: validTo ? new Date(`${validTo}T23:59:59`).toISOString() : null,
              priority: null,
              note: optional(data, 'note'),
            });
            setMessage(result.message);
            await prices.reload();
          }}>
            <label className="sb-field">
              <span className="sb-field__label">Pack</span>
              <select className="sb-input" name="variantUnitId">{packOptions}</select>
            </label>
            <label className="sb-field">
              <span className="sb-field__label">Price type</span>
              <select className="sb-input" name="rateType" defaultValue="STANDARD">
                {['STANDARD', 'MEMBER', 'QUANTITY_SLAB', 'PROMOTIONAL', 'MINIMUM'].map((t) => <option key={t} value={t}>{RateTypeLabels[t]}</option>)}
              </select>
            </label>
            <label className="sb-field">
              <span className="sb-field__label">Channel</span>
              <select className="sb-input" name="channel" defaultValue="RETAIL">
                <option value="RETAIL">Retail</option>
                <option value="WHOLESALE">Wholesale</option>
                <option value="ANY">Both</option>
              </select>
            </label>
            <Field label="Price (Rs.)" name="price" inputMode="decimal" required />
            <label className="sb-check"><input type="checkbox" name="taxInclusive" defaultChecked /> Price includes GST</label>
            <Field label="Only for stock with MRP (Rs.)" name="mrp" inputMode="decimal" />
            <Field label="From quantity" name="minQuantity" inputMode="decimal" defaultValue="0" />
            <Field label="Up to quantity" name="maxQuantity" inputMode="decimal" />
            <Field label="Valid until (promotions)" name="validTo" type="date" />
            <Field label="Note" name="note" />
          </ActionForm>
        </details>
      ) : null}

      <PriceChecker base={base} variant={variant} packLabel={packLabel} />
    </section>
  );
}

function PriceChecker({ base, variant, packLabel }: { base: string; variant: VariantInfo; packLabel: (id: string) => string }) {
  const [quote, setQuote] = useState<PriceQuote | null>(null);
  return (
    <details className="sb-details">
      <summary>Check the price a bill would use</summary>
      <ActionForm testId="price-check-form" submitLabel="Check price" onSubmit={async (data) => {
        const params = new URLSearchParams({
          variantUnitId: text(data, 'variantUnitId'),
          quantity: text(data, 'quantity') || '1',
          channel: text(data, 'channel'),
          member: String(data.get('member') === 'on'),
        });
        if (optional(data, 'mrp')) params.set('mrp', text(data, 'mrp'));
        setQuote(await api.get<PriceQuote>(`${base}/prices/quote?${params.toString()}`));
      }}>
        <label className="sb-field">
          <span className="sb-field__label">Pack</span>
          <select className="sb-input" name="variantUnitId">
            {variant.units.map((u) => <option key={u.id} value={u.id}>{packLabel(u.id)}</option>)}
          </select>
        </label>
        <Field label="Quantity" name="quantity" inputMode="decimal" defaultValue="1" />
        <label className="sb-field">
          <span className="sb-field__label">Channel</span>
          <select className="sb-input" name="channel" defaultValue="RETAIL">
            <option value="RETAIL">Retail</option>
            <option value="WHOLESALE">Wholesale</option>
          </select>
        </label>
        <Field label="MRP on the pack (Rs.)" name="mrp" inputMode="decimal" />
        <label className="sb-check"><input type="checkbox" name="member" /> Customer is a member</label>
      </ActionForm>
      {quote ? (
        <div data-testid="price-quote" className="sb-quote">
          {quote.unitPrice == null ? (
            <Notice tone="warning">No price applies. Add a price first.</Notice>
          ) : (
            <p>
              <strong>{money(quote.unitPriceInclusive)}</strong> incl. tax ({RateTypeLabels[quote.rateType ?? ''] ?? quote.rateType} price, GST {quote.taxRatePercent}%)
            </p>
          )}
          {quote.belowMinimum ? <Notice tone="warning">Below the minimum selling price of {money(quote.minimumPriceInclusive)}. Billing will need a manager.</Notice> : null}
          {quote.aboveMrp ? <Notice tone="warning">Above the MRP on the pack. Billing will refuse it.</Notice> : null}
        </div>
      ) : null}
    </details>
  );
}
