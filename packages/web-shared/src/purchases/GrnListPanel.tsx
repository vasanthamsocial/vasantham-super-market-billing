'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { GrnStatusLabels, PurchaseClassificationLabels, PurchasePermission, type Grn, type GrnSummary } from '../types';
import { ErrorText, formatDateTime } from '../ui';
import { moneyFormat, quantityFormat, StoreSelect, useStoreChoice } from '../stock/StockPanel';
import { GrnTotals } from './GrnEntryPanel';
import { GrnAttachments } from './PurchaseShared';
import { PurchaseReturnForm, PurchaseReturnsList } from './PurchaseReturnForm';

/** Goods receipts of a store, each with its lines, costs and attached supplier invoice. */
export function GrnListPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const { stores, storeId, setStoreId, error: storesError } = useStoreChoice();
  const [status, setStatus] = useState('');
  const receipts = useApiData<GrnSummary[]>(business && storeId ? `${business}/grns?storeId=${storeId}${status ? `&status=${status}` : ''}` : null);
  const [opened, setOpened] = useState<Grn | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [returns, setReturns] = useState(0);

  return (
    <>
      <section className="sb-card" aria-labelledby="grn-list-heading">
        <header className="sb-card__header">
          <h2 id="grn-list-heading">Goods receipts</h2>
        </header>
        <ErrorText error={storesError ?? receipts.error ?? error} />
        <div className="sb-inline-form">
          <StoreSelect stores={stores} value={storeId} onChange={(id) => { setStoreId(id); setOpened(null); }} />
          <label className="sb-field">
            <span className="sb-field__label">Status</span>
            <select className="sb-input" value={status} onChange={(event) => setStatus(event.target.value)}>
              <option value="">All</option>
              {Object.entries(GrnStatusLabels).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
          </label>
        </div>
        <table className="sb-table" data-testid="grns-table">
          <thead>
            <tr>
              <th>Number</th>
              <th>Date</th>
              <th>Supplier</th>
              <th>Invoice</th>
              <th>Document</th>
              <th>Status</th>
              <th className="sb-num">Invoice total</th>
              <th className="sb-num">Landed cost</th>
            </tr>
          </thead>
          <tbody>
            {(receipts.data ?? []).map((g) => (
              <tr key={g.id}>
                <td>
                  <button
                    type="button"
                    className="sb-link"
                    onClick={async () => {
                      setError(null);
                      try {
                        setOpened(await api.get<Grn>(`${business}/grns/${g.id}`));
                      } catch (caught) {
                        setError(caught);
                      }
                    }}
                  >
                    {g.number}
                  </button>
                </td>
                <td>{g.businessDate}</td>
                <td>{g.supplierName}</td>
                <td>{g.supplierInvoiceNumber} ({g.supplierInvoiceDate})</td>
                <td>{PurchaseClassificationLabels[g.classification] ?? g.classification}</td>
                <td>{GrnStatusLabels[g.status] ?? g.status}</td>
                <td className="sb-num">{moneyFormat.format(g.invoiceTotal)}</td>
                <td className="sb-num">{moneyFormat.format(g.landedTotal)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {receipts.data && receipts.data.length === 0 ? <p className="sb-muted">No goods receipts in this store yet.</p> : null}
        {opened ? (
          <GrnView business={business} grn={opened} canManage={hasPermission(PurchasePermission.Manage)} onReturned={() => setReturns((r) => r + 1)} />
        ) : null}
      </section>
      <PurchaseReturnsList business={business} storeId={storeId} refreshKey={String(returns)} />
    </>
  );
}

function GrnView({ business, grn, canManage, onReturned }: { business: string | null; grn: Grn; canManage: boolean; onReturned: () => void }) {
  return (
    <section aria-labelledby="grn-view-heading" data-testid="grn-view">
      <h3 id="grn-view-heading">
        {grn.number}: {grn.supplierName} invoice {grn.supplierInvoiceNumber}
      </h3>
      <p className="sb-muted">
        {PurchaseClassificationLabels[grn.classification] ?? grn.classification}
        {grn.isInterState ? ', inter-state' : ''}, GST {grn.taxRecoverable ? 'recoverable' : 'part of cost'}. {GrnStatusLabels[grn.status] ?? grn.status}
        {grn.purchaseOrderNumber ? `, against order ${grn.purchaseOrderNumber}` : ''}. Received by {grn.receivedBy} on {formatDateTime(grn.receivedAtUtc)}
        {grn.postedAtUtc ? `, posted ${formatDateTime(grn.postedAtUtc)}` : ''}.
      </p>
      <table className="sb-table">
        <thead>
          <tr>
            <th>Item</th>
            <th className="sb-num">Qty (+free)</th>
            <th className="sb-num">Rate</th>
            <th className="sb-num">GST %</th>
            <th className="sb-num">Taxable</th>
            <th className="sb-num">Tax</th>
            <th className="sb-num">Expenses</th>
            <th className="sb-num">Landed</th>
            <th className="sb-num">Per unit</th>
            <th>Batch</th>
            <th>Notes</th>
          </tr>
        </thead>
        <tbody>
          {grn.lines.map((l) => (
            <tr key={l.lineNumber}>
              <td>{l.description} ({l.unitCode})</td>
              <td className="sb-num">
                {quantityFormat.format(l.quantity)}
                {l.freeQuantity > 0 ? ` +${quantityFormat.format(l.freeQuantity)}` : ''}
              </td>
              <td className="sb-num">{moneyFormat.format(l.rate)}</td>
              <td className="sb-num">{l.gstRatePercent}</td>
              <td className="sb-num">{moneyFormat.format(l.taxable)}</td>
              <td className="sb-num">{moneyFormat.format(l.cgst + l.sgst + l.igst + l.cess)}</td>
              <td className="sb-num">{moneyFormat.format(l.expenseShare)}</td>
              <td className="sb-num">{moneyFormat.format(l.landedTotal)}</td>
              <td className="sb-num">{l.landedUnitCost.toFixed(4)}</td>
              <td>{l.batchNumber ? `${l.batchNumber}${l.expiresOn ? ` (exp. ${l.expiresOn})` : ''}` : '-'}</td>
              <td>
                {[
                  l.costChange ? `cost ${l.costChange.percentChange > 0 ? '+' : ''}${l.costChange.percentChange.toFixed(2)}%` : null,
                  l.costChangeReason,
                  l.lossLeaderReason ? `loss-leader: ${l.lossLeaderReason}` : null,
                  l.updateSellingPrice && l.sellingPrice !== null ? `new price Rs. ${moneyFormat.format(l.sellingPrice)}` : null,
                ]
                  .filter(Boolean)
                  .join('; ') || '-'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <GrnTotals grn={grn} />
      <GrnAttachments key={grn.id} business={business} grnId={grn.id} canUpload={canManage} />
      {canManage ? <PurchaseReturnForm key={`return-${grn.id}`} business={business} grnId={grn.id} onReturned={onReturned} /> : null}
    </section>
  );
}
