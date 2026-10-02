'use client';

import { useRef, useState } from 'react';
import { api, uploadFile } from '../api';
import { useApiData } from '../admin/useApiData';
import type { AttachmentInfo, ProductDetail, ProductSummary, Supplier } from '../types';
import { ErrorText, formatDateTime } from '../ui';

export function newKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
}

/** A number typed into a field, or null when empty; NaN when not a number. */
export function parseNumber(value: string): number | null {
  return value.trim() === '' ? null : Number(value);
}

export function SupplierSelect({ suppliers, value, onChange }: { suppliers: Supplier[]; value: string; onChange: (id: string) => void }) {
  return (
    <label className="sb-field">
      <span className="sb-field__label">Supplier</span>
      <select className="sb-input" value={value} onChange={(event) => onChange(event.target.value)}>
        <option value="">Choose a supplier</option>
        {suppliers.filter((s) => s.isActive).map((s) => (
          <option key={s.id} value={s.id}>{s.code} - {s.name}{s.gstin ? '' : ' (unregistered)'}</option>
        ))}
      </select>
    </label>
  );
}

/** Finds a catalogue item by name, code or barcode and hands back its full detail. */
export function ItemPicker({ business, onPick }: { business: string | null; onPick: (product: ProductDetail) => void }) {
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [error, setError] = useState<unknown>(null);
  const results = useApiData<ProductSummary[]>(business && query ? `${business}/catalog/products?take=10&search=${encodeURIComponent(query)}` : null);

  async function pick(id: string) {
    setError(null);
    try {
      onPick(await api.get<ProductDetail>(`${business}/catalog/products/${id}`));
      setSearch('');
      setQuery('');
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <div>
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
      <ErrorText error={results.error ?? error} />
      {query && results.data ? (
        <ul className="sb-plain-list" aria-label="Matching items">
          {results.data.length === 0 ? <li className="sb-muted">No matching items.</li> : null}
          {results.data.map((p) => (
            <li key={p.id}>
              <button type="button" className="sb-link" onClick={() => void pick(p.id)}>
                Add {p.code} - {p.name}
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

function sizeText(bytes: number): string {
  return bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** Files kept with a goods receipt (the scanned supplier invoice): PDF, JPEG or PNG up to 10 MB. Downloads only. */
export function GrnAttachments({ business, grnId, canUpload }: { business: string | null; grnId: string; canUpload: boolean }) {
  const path = business ? `${business}/grns/${grnId}/attachments` : null;
  const files = useApiData<AttachmentInfo[]>(path);
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function upload() {
    const file = input.current?.files?.[0];
    if (!path || !file) return;
    setBusy(true);
    setError(null);
    try {
      if (file.size > 10 * 1024 * 1024) throw new Error('The file is too large (at most 10 MB).');
      await uploadFile<AttachmentInfo>(path, file);
      if (input.current) input.current.value = '';
      await files.reload();
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-label="Attachments" data-testid="grn-attachments">
      <h3>Attachments</h3>
      <ErrorText error={files.error ?? error} />
      {(files.data ?? []).length === 0 ? <p className="sb-muted">No files attached.</p> : null}
      <ul className="sb-plain-list">
        {(files.data ?? []).map((f) => (
          <li key={f.id}>
            <a href={`${path}/${f.id}`} download={f.fileName} rel="noopener">
              {f.fileName}
            </a>{' '}
            <span className="sb-muted">
              ({sizeText(f.size)}, added by {f.uploadedBy} on {formatDateTime(f.uploadedAtUtc)}, SHA-256 {f.sha256.slice(0, 12)}...)
            </span>
          </li>
        ))}
      </ul>
      {canUpload ? (
        <div className="sb-inline-form">
          <input ref={input} className="sb-input" type="file" accept="application/pdf,image/jpeg,image/png,.pdf,.jpg,.jpeg,.png" aria-label="File to attach" />
          <button type="button" className="sb-button sb-button--secondary" disabled={busy} onClick={() => void upload()}>
            {busy ? 'Uploading...' : 'Attach file'}
          </button>
        </div>
      ) : null}
    </section>
  );
}
