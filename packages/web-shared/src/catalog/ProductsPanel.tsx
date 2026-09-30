'use client';

import Link from 'next/link';
import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { CatalogPermission, type ProductDetail, type ProductSummary, type UnitInfo } from '../types';
import { ActionForm, ErrorText, Field, Notice, optional, text } from '../ui';

/** Product search and creation. `detailHref` builds the link to a product page in the host app. */
export function ProductsPanel({ detailHref }: { detailHref: (productId: string) => string }) {
  const { membership, hasPermission } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/catalog` : null;
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const products = useApiData<ProductSummary[]>(base ? `${base}/products?take=100${query ? `&search=${encodeURIComponent(query)}` : ''}` : null);
  const units = useApiData<UnitInfo[]>(base ? `${base}/units` : null);
  const [created, setCreated] = useState<ProductDetail | null>(null);
  const [baseUnitId, setBaseUnitId] = useState('');
  const defaultUnitId = units.data?.find((u) => u.code === 'PCS')?.id ?? units.data?.[0]?.id ?? '';
  const selectedUnitId = baseUnitId || defaultUnitId;

  return (
    <section className="sb-card" aria-labelledby="products-heading">
      <header className="sb-card__header">
        <h2 id="products-heading">Products</h2>
      </header>
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
          aria-label="Search products by name, code or barcode"
          placeholder="Name, code or scan a barcode"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
        <button className="sb-button" type="submit">Search</button>
      </form>
      <ErrorText error={products.error} />
      {created ? (
        <Notice tone="success">
          {created.name} added. <Link href={detailHref(created.id)}>Open it to add prices and packs</Link>.
        </Notice>
      ) : null}
      <table className="sb-table" data-testid="products-table">
        <thead>
          <tr>
            <th>Code</th>
            <th>Name</th>
            <th>Category / brand</th>
            <th>HSN</th>
            <th>GST</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {(products.data ?? []).map((p) => (
            <tr key={p.id}>
              <td>{p.code}</td>
              <td>
                <Link href={detailHref(p.id)}>{p.name}</Link>
              </td>
              <td>{[p.categoryName, p.brandName].filter(Boolean).join(' / ') || '-'}</td>
              <td>{p.hsnSac}</td>
              <td>{p.supplyType === 'TAXABLE' ? `${p.gstRatePercent}%` : p.supplyType.replace('_', ' ').toLowerCase()}</td>
              <td>{p.isActive ? 'Active' : 'Inactive'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {products.data && products.data.length === 0 ? <p className="sb-muted">No products found.</p> : null}

      {hasPermission(CatalogPermission.Manage) && base ? (
        <details className="sb-details">
          <summary>Add a product</summary>
          <ActionForm
            testId="create-product-form"
            submitLabel="Add product"
            onSubmit={async (data) => {
              const supplyType = text(data, 'supplyType');
              if (!selectedUnitId) throw new Error('Choose the stock unit.');
              const product = await api.post<ProductDetail>(`${base}/products`, {
                code: text(data, 'code'),
                name: text(data, 'name'),
                printName: optional(data, 'printName'),
                categoryId: null,
                brandId: null,
                baseUnitId: selectedUnitId,
                hsnSac: text(data, 'hsnSac'),
                supplyType,
                gstRatePercent: supplyType === 'TAXABLE' ? Number(text(data, 'gstRatePercent') || 0) : 0,
                cessRatePercent: supplyType === 'TAXABLE' ? Number(text(data, 'cessRatePercent') || 0) : 0,
                isWeighed: data.get('isWeighed') === 'on',
                tracksBatches: data.get('tracksBatches') === 'on',
                tracksExpiry: data.get('tracksExpiry') === 'on',
                tracksSerials: false,
                variant: { code: null, name: null, barcode: optional(data, 'barcode'), mrp: optional(data, 'mrp') ? Number(text(data, 'mrp')) : null },
              });
              setCreated(product);
              await products.reload();
            }}
          >
            <Field label="Product code (SKU)" name="code" required />
            <Field label="Product name" name="name" required />
            <Field label="Receipt name" name="printName" hint="Optional, up to 40 characters." />
            <label className="sb-field">
              <span className="sb-field__label">Stock unit</span>
              <select className="sb-input" name="baseUnitId" required value={selectedUnitId} onChange={(event) => setBaseUnitId(event.target.value)}>
                {(units.data ?? []).map((u) => (
                  <option key={u.id} value={u.id}>{u.code} - {u.name}</option>
                ))}
              </select>
            </label>
            <Field label="HSN/SAC" name="hsnSac" inputMode="numeric" hint="4, 6 or 8 digits." required />
            <label className="sb-field">
              <span className="sb-field__label">GST treatment</span>
              <select className="sb-input" name="supplyType" defaultValue="TAXABLE">
                <option value="TAXABLE">Taxable</option>
                <option value="EXEMPT">Exempt</option>
                <option value="NIL_RATED">Nil-rated</option>
                <option value="NON_GST">Non-GST</option>
              </select>
            </label>
            <Field label="GST rate %" name="gstRatePercent" inputMode="decimal" defaultValue="5" />
            <Field label="Cess %" name="cessRatePercent" inputMode="decimal" defaultValue="0" />
            <Field label="Barcode" name="barcode" hint="Scan it; the check digit is verified." />
            <Field label="MRP (Rs.)" name="mrp" inputMode="decimal" />
            <label className="sb-check"><input type="checkbox" name="isWeighed" /> Sold by weight</label>
            <label className="sb-check"><input type="checkbox" name="tracksBatches" /> Track batches</label>
            <label className="sb-check"><input type="checkbox" name="tracksExpiry" /> Track expiry dates</label>
          </ActionForm>
        </details>
      ) : null}
    </section>
  );
}
