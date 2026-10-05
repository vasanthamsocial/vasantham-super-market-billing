'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { InvoiceKindLabels, type Invoice, type InvoiceSummary } from '../types';
import { ErrorText } from '../ui';
import { StoreSelect, useStoreChoice } from '../stock/StockPanel';
import { InvoiceReceipt } from './InvoiceReceipt';
import { OfflineBillsCard } from './OfflineBillsCard';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

function todayLocal(): string {
  const now = new Date();
  return new Date(now.getTime() - now.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
}

/** Issued invoices of a store by day, with reprint (receipt) and PDF download. */
export function InvoicesPanel() {
  const { membership } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}/sales/invoices` : null;
  const { stores, storeId, setStoreId, error: storesError } = useStoreChoice();
  const [date, setDate] = useState(todayLocal);
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const invoices = useApiData<InvoiceSummary[]>(
    base && storeId ? `${base}?storeId=${storeId}${query ? `&search=${encodeURIComponent(query)}` : date ? `&date=${date}` : ''}` : null,
  );
  const [opened, setOpened] = useState<Invoice | null>(null);
  const [error, setError] = useState<unknown>(null);
  const total = (invoices.data ?? []).reduce((sum, i) => sum + i.grandTotal, 0);

  return (
    <>
      <OfflineBillsCard />
      <section className="sb-card" aria-labelledby="invoices-heading">
        <header className="sb-card__header">
          <h2 id="invoices-heading">Sales invoices</h2>
          {invoices.data ? <p className="sb-muted" data-testid="invoices-total">{invoices.data.length} bills, Rs. {money.format(total)}</p> : null}
        </header>
        <ErrorText error={storesError ?? invoices.error ?? error} />
        <div className="sb-inline-form">
          <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
          <label className="sb-field">
            <span className="sb-field__label">Date</span>
            <input className="sb-input" type="date" value={date} onChange={(e) => { setDate(e.target.value); setQuery(''); }} />
          </label>
          <form
            className="sb-inline-form"
            role="search"
            onSubmit={(e) => {
              e.preventDefault();
              setQuery(search.trim());
            }}
          >
            <input
              className="sb-input"
              aria-label="Find an invoice by number or customer"
              placeholder="Invoice number or customer"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
            <button className="sb-button" type="submit">Find</button>
          </form>
        </div>
        <table className="sb-table" data-testid="invoices-table">
          <thead>
            <tr>
              <th>Number</th>
              <th>Type</th>
              <th>Time</th>
              <th>Counter</th>
              <th>Cashier</th>
              <th>Customer</th>
              <th className="sb-num">Total (Rs.)</th>
            </tr>
          </thead>
          <tbody>
            {(invoices.data ?? []).map((i) => (
              <tr key={i.id}>
                <td>
                  <button
                    type="button"
                    className="sb-link"
                    onClick={async () => {
                      setError(null);
                      try {
                        setOpened(await api.get<Invoice>(`${base}/${i.id}`));
                      } catch (caught) {
                        setError(caught);
                      }
                    }}
                  >
                    {i.number}
                  </button>
                </td>
                <td>{InvoiceKindLabels[i.kind] ?? i.kind}</td>
                <td>{new Date(i.issuedAtUtc).toLocaleString('en-IN', { dateStyle: 'short', timeStyle: 'short' })}</td>
                <td>{i.counterCode}</td>
                <td>{i.cashier}</td>
                <td>{i.buyerName ?? '-'}</td>
                <td className="sb-num">{money.format(i.grandTotal)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {invoices.data && invoices.data.length === 0 ? <p className="sb-muted">No invoices found.</p> : null}
      </section>
      {opened && base ? (
        <section className="sb-card" aria-labelledby="invoice-view-heading" data-testid="invoice-view">
          <header className="sb-card__header">
            <h2 id="invoice-view-heading">{InvoiceKindLabels[opened.kind] ?? opened.kind} {opened.number}</h2>
            <div className="sb-actions">
              <button type="button" className="sb-button" onClick={() => window.print()}>Reprint receipt</button>
              <a className="sb-button sb-button--secondary" href={`${base}/${opened.id}/pdf`} target="_blank" rel="noopener" data-testid="invoice-pdf">
                PDF invoice
              </a>
            </div>
          </header>
          <InvoiceReceipt invoice={opened} />
        </section>
      ) : null}
    </>
  );
}
