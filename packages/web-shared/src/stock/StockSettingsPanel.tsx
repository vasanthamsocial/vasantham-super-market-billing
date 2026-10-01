'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  NegativeStockModeLabels,
  StockPermission,
  ValuationMethodLabels,
  type InventorySettings,
  type NegativeStockRule,
  type ProductDetail,
  type ProductSummary,
  type SetNegativeStockRuleResult,
} from '../types';
import { ActionForm, ErrorText, Field, formatDateTime, Notice, optional, text } from '../ui';
import { quantityFormat, useStoreChoice } from './StockPanel';

/** Finds a product by its exact code (as typed by the user). */
async function productByCode(business: string, code: string): Promise<ProductSummary> {
  const matches = await api.get<ProductSummary[]>(`${business}/catalog/products?take=20&search=${encodeURIComponent(code)}`);
  const product = matches.find((p) => p.code.toUpperCase() === code.toUpperCase());
  if (!product) throw new Error(`No product with code ${code}.`);
  return product;
}

/** Valuation method, negative-stock rules and reorder levels. */
export function StockSettingsPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const settings = useApiData<InventorySettings>(business ? `${business}/stock/settings` : null);
  const rules = useApiData<NegativeStockRule[]>(business ? `${business}/stock/negative-rules` : null);
  const { stores } = useStoreChoice();
  const canManage = hasPermission(StockPermission.Settings);
  const [ruleResult, setRuleResult] = useState<SetNegativeStockRuleResult | null>(null);
  const [reorderSaved, setReorderSaved] = useState('');
  const [mode, setMode] = useState('DISABLED');
  const storeName = (id: string | null) => (id ? (stores.find((s) => s.id === id)?.name ?? 'store') : 'All stores');

  return (
    <>
      <section className="sb-card" aria-labelledby="valuation-heading">
        <header className="sb-card__header">
          <h2 id="valuation-heading">Stock valuation</h2>
        </header>
        <ErrorText error={settings.error} />
        {settings.data ? (
          settings.data.valuationLocked || !canManage ? (
            <p data-testid="valuation-method">
              {ValuationMethodLabels[settings.data.valuationMethod] ?? settings.data.valuationMethod}
              {settings.data.valuationLocked ? <span className="sb-muted"> - fixed now that stock has moved.</span> : null}
            </p>
          ) : (
            <ActionForm
              testId="valuation-form"
              submitLabel="Save valuation method"
              onSubmit={async (data) => {
                await api.put(`${business}/stock/settings`, { valuationMethod: text(data, 'valuationMethod') });
                await settings.reload();
              }}
            >
              <label className="sb-field">
                <span className="sb-field__label">Valuation method</span>
                <select className="sb-input" name="valuationMethod" defaultValue={settings.data.valuationMethod}>
                  {Object.entries(ValuationMethodLabels).map(([value, label]) => (
                    <option key={value} value={value}>{label}</option>
                  ))}
                </select>
              </label>
              <Notice tone="warning">Choose before entering any stock: the method cannot change once stock has moved.</Notice>
            </ActionForm>
          )
        ) : null}
      </section>

      <section className="sb-card" aria-labelledby="negative-heading">
        <header className="sb-card__header">
          <h2 id="negative-heading">Negative stock</h2>
        </header>
        <p className="sb-muted">
          The most specific rule applies: item in a store, then item, then store, then the whole business. Without a rule, stock cannot go below zero.
        </p>
        <ErrorText error={rules.error} />
        <table className="sb-table" data-testid="negative-rules-table">
          <thead>
            <tr>
              <th>Applies to</th>
              <th>Rule</th>
              <th>Reason</th>
              <th>Set by</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {(rules.data ?? []).map((r) => (
              <tr key={r.id} className={r.isActive ? undefined : 'sb-row--closed'}>
                <td>{storeName(r.storeId)}{r.productId ? ', one item' : ''}</td>
                <td>{NegativeStockModeLabels[r.mode] ?? r.mode}{r.limitQuantity ? ` (${quantityFormat.format(r.limitQuantity)})` : ''}</td>
                <td>{r.reason}</td>
                <td>{r.createdBy}, {formatDateTime(r.createdAtUtc)}</td>
                <td>{r.isActive ? 'In force' : r.supersededAtUtc ? 'Replaced or rejected' : 'Waiting for approval'}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {rules.data && rules.data.length === 0 ? <p className="sb-muted">No rules: negative stock is not allowed anywhere.</p> : null}
        {ruleResult ? <Notice tone={ruleResult.outcome === 'active' ? 'success' : 'info'}>{ruleResult.message}</Notice> : null}
        {canManage && business ? (
          <details className="sb-details">
            <summary>Change a rule</summary>
            <ActionForm
              testId="negative-rule-form"
              submitLabel="Save rule"
              onSubmit={async (data) => {
                const productCode = optional(data, 'productCode');
                const product = productCode ? await productByCode(business, productCode) : null;
                const result = await api.post<SetNegativeStockRuleResult>(`${business}/stock/negative-rules`, {
                  storeId: optional(data, 'storeId'),
                  productId: product?.id ?? null,
                  mode,
                  limitQuantity: mode === 'ENABLED_WITH_LIMIT' ? Number(text(data, 'limitQuantity')) : null,
                  reason: text(data, 'reason'),
                });
                setRuleResult(result);
                await rules.reload();
              }}
            >
              <label className="sb-field">
                <span className="sb-field__label">Store</span>
                <select className="sb-input" name="storeId" defaultValue="">
                  <option value="">All stores</option>
                  {stores.map((s) => (
                    <option key={s.id} value={s.id}>{s.code} - {s.name}</option>
                  ))}
                </select>
              </label>
              <Field label="Item code" name="productCode" hint="Leave empty for all items." />
              <label className="sb-field">
                <span className="sb-field__label">Rule</span>
                <select className="sb-input" value={mode} onChange={(event) => setMode(event.target.value)}>
                  {Object.entries(NegativeStockModeLabels).map(([value, label]) => (
                    <option key={value} value={value}>{label}</option>
                  ))}
                </select>
              </label>
              {mode === 'ENABLED_WITH_LIMIT' ? <Field label="How far below zero (stock units)" name="limitQuantity" inputMode="decimal" required /> : null}
              <Field label="Reason" name="reason" required />
              <Notice>Allowing more negative stock than now needs approval by another manager. Tightening applies at once.</Notice>
            </ActionForm>
          </details>
        ) : null}
      </section>

      {hasPermission(StockPermission.Adjust) && business ? (
        <section className="sb-card" aria-labelledby="reorder-heading">
          <header className="sb-card__header">
            <h2 id="reorder-heading">Reorder levels</h2>
          </header>
          {reorderSaved ? <Notice tone="success">{reorderSaved}</Notice> : null}
          <ActionForm
            testId="reorder-form"
            submitLabel="Save reorder level"
            onSubmit={async (data) => {
              const summary = await productByCode(business, text(data, 'productCode'));
              const product = await api.get<ProductDetail>(`${business}/catalog/products/${summary.id}`);
              const variant = product.variants.find((v) => v.isActive) ?? product.variants[0];
              if (!variant) throw new Error(`${product.name} has no variants.`);
              await api.put(`${business}/stock/reorder-levels`, {
                storeId: text(data, 'storeId'),
                variantId: variant.id,
                minimumQuantity: Number(text(data, 'minimumQuantity') || 0),
                reorderQuantity: Number(text(data, 'reorderQuantity') || 0),
              });
              setReorderSaved(`Reorder level saved for ${product.name}.`);
            }}
          >
            <label className="sb-field">
              <span className="sb-field__label">Store</span>
              <select className="sb-input" name="storeId">
                {stores.map((s) => (
                  <option key={s.id} value={s.id}>{s.code} - {s.name}</option>
                ))}
              </select>
            </label>
            <Field label="Item code" name="productCode" required />
            <Field label="Minimum quantity" name="minimumQuantity" inputMode="decimal" hint="Shown as low at or below this." required />
            <Field label="Reorder quantity" name="reorderQuantity" inputMode="decimal" required />
          </ActionForm>
        </section>
      ) : null}
    </>
  );
}
