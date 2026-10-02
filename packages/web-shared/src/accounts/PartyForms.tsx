'use client';

import { useState } from 'react';
import { api, ApiError } from '../api';
import { PartyPaymentMethodLabels, type OpenItems, type PartyContact, type Store, type SupplierPayment } from '../types';
import { ErrorText, Field, Notice, optional, text } from '../ui';
import { moneyFormat, StoreSelect } from '../stock/StockPanel';
import { newKey, parseNumber } from '../purchases/PurchaseShared';

/** The contact, messaging-consent and credit-period fields suppliers and debtors share. */
export function PartyContactFields({ party }: { party?: PartyContact & { address?: string | null; phone?: string | null } }) {
  return (
    <>
      <div className="sb-form-row">
        <Field label="Trade name (optional)" name="tradeName" defaultValue={party?.tradeName ?? ''} />
        <Field label="Contact person" name="contactPerson" defaultValue={party?.contactPerson ?? ''} />
        <Field label="Phone" name="phone" defaultValue={party?.phone ?? ''} />
        <Field label="Email" name="email" type="email" defaultValue={party?.email ?? ''} />
      </div>
      <div className="sb-form-row">
        <Field label="Address" name="address" defaultValue={party?.address ?? ''} />
        <Field label="Credit period (days)" name="creditPeriodDays" inputMode="numeric" defaultValue={String(party?.creditPeriodDays ?? 0)} />
      </div>
      <div className="sb-form-row">
        <Field label="WhatsApp number" name="whatsAppNumber" inputMode="tel" defaultValue={party?.whatsAppNumber ?? ''} />
        <Field label="SMS number" name="smsNumber" inputMode="tel" defaultValue={party?.smsNumber ?? ''} />
      </div>
      <label className="sb-check">
        <input type="checkbox" name="whatsAppConsent" defaultChecked={party?.whatsAppConsent ?? false} /> Agreed to receive WhatsApp messages
      </label>
      <label className="sb-check">
        <input type="checkbox" name="smsConsent" defaultChecked={party?.smsConsent ?? false} /> Agreed to receive SMS
      </label>
    </>
  );
}

export function partyContactFromForm(data: FormData) {
  return {
    tradeName: optional(data, 'tradeName'),
    contactPerson: optional(data, 'contactPerson'),
    phone: optional(data, 'phone'),
    email: optional(data, 'email'),
    address: optional(data, 'address'),
    whatsAppNumber: optional(data, 'whatsAppNumber'),
    smsNumber: optional(data, 'smsNumber'),
    whatsAppConsent: data.get('whatsAppConsent') === 'on',
    smsConsent: data.get('smsConsent') === 'on',
    creditPeriodDays: Number(text(data, 'creditPeriodDays') || '0'),
  };
}

/** The optional opening balance on a new supplier or debtor (needs the right to enter balances). */
export function openingFromForm(data: FormData) {
  const amount = parseNumber(text(data, 'openingBalance'));
  if (amount !== null && Number.isNaN(amount)) throw new Error('The opening balance is not a number.');
  return { openingBalance: amount, openingBalanceDate: optional(data, 'openingBalanceDate') };
}

/** Pays a supplier: the bills named (with amounts), or the oldest due first; the rest stays as an advance. */
export function SupplierPaymentForm({
  business,
  supplierId,
  stores,
  open,
  onPaid,
}: {
  business: string | null;
  supplierId: string;
  stores: Store[];
  open: OpenItems | null;
  onPaid: () => Promise<void>;
}) {
  const [storeId, setStoreId] = useState('');
  const [method, setMethod] = useState('BANK_TRANSFER');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [note, setNote] = useState('');
  const [chosen, setChosen] = useState<Record<string, string>>({});
  // One key per payment: a retry after a lost response returns the same payment instead of paying twice.
  const [idempotencyKey, setIdempotencyKey] = useState(newKey);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [paid, setPaid] = useState<SupplierPayment | null>(null);
  const store = storeId || stores[0]?.id || '';

  async function pay() {
    setBusy(true);
    setError(null);
    setPaid(null);
    try {
      const total = parseNumber(amount);
      if (total === null || Number.isNaN(total)) throw new Error('Enter the amount paid.');
      const allocations = Object.entries(chosen)
        .filter(([, v]) => v.trim() !== '')
        .map(([chargeEntryId, v]) => {
          const n = Number(v);
          if (Number.isNaN(n)) throw new Error('Check the amounts against the bills.');
          return { chargeEntryId, amount: n };
        });
      const payment = await api.post<SupplierPayment>(`${business}/supplier-payments`, {
        storeId: store,
        supplierId,
        method,
        amount: total,
        reference: reference.trim() || null,
        note: note.trim() || null,
        allocations,
        idempotencyKey,
      });
      setPaid(payment);
      setAmount('');
      setReference('');
      setNote('');
      setChosen({});
      setIdempotencyKey(newKey());
      await onPaid();
    } catch (caught) {
      if (caught instanceof ApiError && caught.status !== 0) setIdempotencyKey(newKey());
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <details data-testid="supplier-payment">
      <summary>Pay this supplier</summary>
      {paid ? (
        <Notice tone="success">
          Recorded {paid.number}: Rs. {moneyFormat.format(paid.amount)}
          {paid.appliedTo.length > 0 ? `, paying ${paid.appliedTo.map((a) => a.documentNumber ?? 'opening balance').join(', ')}` : ''}
          {paid.unapplied > 0 ? `; Rs. ${moneyFormat.format(paid.unapplied)} kept as an advance` : ''}.
        </Notice>
      ) : null}
      <div className="sb-form">
        <div className="sb-form-row">
          <StoreSelect stores={stores} value={store} onChange={setStoreId} label="Paid from store" />
          <label className="sb-field">
            <span className="sb-field__label">Method</span>
            <select className="sb-input" value={method} onChange={(e) => setMethod(e.target.value)}>
              {Object.entries(PartyPaymentMethodLabels).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
          </label>
          <label className="sb-field">
            <span className="sb-field__label">Amount (Rs.)</span>
            <input className="sb-input" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
          </label>
          <label className="sb-field">
            <span className="sb-field__label">{method === 'CHEQUE' ? 'Cheque number' : 'Reference (optional)'}</span>
            <input className="sb-input" value={reference} maxLength={40} onChange={(e) => setReference(e.target.value)} />
          </label>
        </div>
        {open && open.charges.length > 0 ? (
          <>
            <p className="sb-muted">Leave the bills empty to pay the oldest due first, or enter how much goes to each.</p>
            <table className="sb-table sb-table--compact">
              <tbody>
                {open.charges.map((c) => (
                  <tr key={c.entryId}>
                    <td>{c.documentNumber ?? 'Opening balance'} (due {c.dueDate})</td>
                    <td className="sb-num">Rs. {moneyFormat.format(c.remaining)} unpaid</td>
                    <td>
                      <input
                        className="sb-input"
                        inputMode="decimal"
                        aria-label={`Pay towards ${c.documentNumber ?? 'opening balance'}`}
                        value={chosen[c.entryId] ?? ''}
                        onChange={(e) => setChosen((current) => ({ ...current, [c.entryId]: e.target.value }))}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </>
        ) : null}
        <label className="sb-field">
          <span className="sb-field__label">Note (optional)</span>
          <input className="sb-input" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} />
        </label>
        <ErrorText error={error} />
        <button className="sb-button" type="button" disabled={busy} onClick={() => void pay()}>
          {busy ? 'Please wait...' : 'Record payment'}
        </button>
      </div>
    </details>
  );
}
