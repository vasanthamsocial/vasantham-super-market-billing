'use client';

import { InvoiceKindLabels, PaymentMethodLabels, type Invoice } from '../types';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const quantity = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });

/**
 * The customer's receipt, laid out for an 80 mm receipt printer (it prints alone on its own narrow page).
 * Everything comes from the invoice as issued, so a reprint matches the original.
 */
export function InvoiceReceipt({ invoice }: { invoice: Invoice }) {
  const tax = invoice.taxMode === 'GST_REGULAR';
  return (
    <div className="sb-receipt sb-print-area" data-testid="receipt">
      <div className="sb-receipt__center">
        <strong className="sb-receipt__seller">{invoice.sellerName}</strong>
        <div>{invoice.sellerAddress}</div>
        {invoice.sellerGstin ? <div>GSTIN: {invoice.sellerGstin}</div> : null}
        <div className="sb-receipt__title">{(InvoiceKindLabels[invoice.kind] ?? invoice.kind).toUpperCase()}</div>
      </div>
      <div className="sb-receipt__row">
        <span>No. <strong data-testid="receipt-number">{invoice.number}</strong></span>
        <span>{new Date(invoice.issuedAtUtc).toLocaleString('en-IN', { dateStyle: 'short', timeStyle: 'short' })}</span>
      </div>
      <div className="sb-receipt__row">
        <span>Counter {invoice.counterCode}</span>
        <span>{invoice.cashier}</span>
      </div>
      {invoice.buyerName || invoice.buyerGstin ? (
        <div className="sb-receipt__block">
          <div>To: {invoice.buyerName}</div>
          {invoice.buyerGstin ? <div>GSTIN: {invoice.buyerGstin}</div> : null}
          {invoice.isInterState ? <div>Place of supply: {invoice.placeOfSupplyStateCode}</div> : null}
        </div>
      ) : null}
      <hr />
      {invoice.lines.map((l) => (
        <div key={l.lineNumber} className="sb-receipt__item">
          <div>{l.description}{l.mrp ? ` (MRP ${money.format(l.mrp)})` : ''}</div>
          <div className="sb-receipt__row">
            <span>{quantity.format(l.quantity)} {l.unitCode} x {money.format(l.unitPrice)}{tax && l.supplyType === 'TAXABLE' ? ` @${l.gstRatePercent}%` : ''}</span>
            <span>{money.format(l.total)}</span>
          </div>
          {l.itemDiscount + l.billDiscount > 0 ? <div className="sb-receipt__note">Discount {money.format(l.itemDiscount + l.billDiscount)}</div> : null}
        </div>
      ))}
      <hr />
      <Row label="Items" value={String(invoice.lines.length)} />
      {invoice.discountTotal > 0 ? <Row label="You saved" value={money.format(invoice.discountTotal)} /> : null}
      {tax ? (
        <>
          <Row label="Taxable value" value={money.format(invoice.taxableTotal)} />
          {invoice.isInterState ? (
            <Row label="IGST" value={money.format(invoice.igstTotal)} />
          ) : (
            <>
              <Row label="CGST" value={money.format(invoice.cgstTotal)} />
              <Row label="SGST" value={money.format(invoice.sgstTotal)} />
            </>
          )}
          {invoice.cessTotal > 0 ? <Row label="Cess" value={money.format(invoice.cessTotal)} /> : null}
        </>
      ) : null}
      {invoice.roundOff !== 0 ? <Row label="Round off" value={money.format(invoice.roundOff)} /> : null}
      <div className="sb-receipt__row sb-receipt__total">
        <span>TOTAL Rs.</span>
        <span data-testid="receipt-total">{money.format(invoice.grandTotal)}</span>
      </div>
      {invoice.payments.map((p, i) => (
        <Row key={i} label={`${PaymentMethodLabels[p.method] ?? p.method}${p.reference ? ` (${p.reference})` : ''}`} value={money.format(p.amount)} />
      ))}
      {invoice.changeDue > 0 ? <Row label="Change" value={money.format(invoice.changeDue)} /> : null}
      {invoice.declaration ? <p className="sb-receipt__note">{invoice.declaration}</p> : null}
      {invoice.taxMode === 'NOT_GST_REGISTERED' ? <p className="sb-receipt__note">Seller not registered under GST. No GST charged.</p> : null}
      <p className="sb-receipt__center">Thank you. Please visit again.</p>
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="sb-receipt__row">
      <span>{label}</span>
      <span>{value}</span>
    </div>
  );
}
