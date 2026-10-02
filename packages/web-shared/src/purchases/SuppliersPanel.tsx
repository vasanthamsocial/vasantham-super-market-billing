'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { AccountPermission, PurchasePermission, type PurchaseSettings, type Supplier } from '../types';
import { ActionForm, ErrorText, Field, Notice, optional, text } from '../ui';
import { moneyFormat, useStoreChoice } from '../stock/StockPanel';
import { AccountView } from '../accounts/AccountView';
import { openingFromForm, partyContactFromForm, PartyContactFields, SupplierPaymentForm } from '../accounts/PartyForms';

/** Suppliers with what is owed to them, each supplier's account and payments, and (for approvers) the purchase checks. */
export function SuppliersPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const { stores } = useStoreChoice();
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const suppliers = useApiData<Supplier[]>(business ? `${business}/suppliers${query ? `?search=${encodeURIComponent(query)}` : ''}` : null);
  const [editing, setEditing] = useState<Supplier | null>(null);
  const [opened, setOpened] = useState<Supplier | null>(null);
  const canManage = hasPermission(PurchasePermission.Suppliers);
  const canPay = hasPermission(AccountPermission.Payables);
  const canEnterBalances = hasPermission(AccountPermission.Adjust);

  return (
    <>
      <section className="sb-card" aria-labelledby="suppliers-heading">
        <header className="sb-card__header">
          <h2 id="suppliers-heading">Suppliers</h2>
        </header>
        <form
          className="sb-inline-form"
          role="search"
          onSubmit={(event) => {
            event.preventDefault();
            setQuery(search.trim());
          }}
        >
          <input className="sb-input" aria-label="Search suppliers" placeholder="Name, code or GSTIN" value={search} onChange={(e) => setSearch(e.target.value)} />
          <button className="sb-button sb-button--secondary" type="submit">Search</button>
        </form>
        <ErrorText error={suppliers.error} />
        <table className="sb-table" data-testid="suppliers-table">
          <thead>
            <tr>
              <th>Code</th>
              <th>Name</th>
              <th>GSTIN</th>
              <th>Credit</th>
              <th className="sb-num">Owed</th>
              <th className="sb-num">Overdue</th>
              <th>Status</th>
              {canManage ? <th /> : null}
            </tr>
          </thead>
          <tbody>
            {(suppliers.data ?? []).map((s) => (
              <tr key={s.id}>
                <td>{s.code}</td>
                <td>
                  <button type="button" className="sb-link" onClick={() => setOpened(s)}>
                    {s.name}
                  </button>
                  {s.tradeName ? <span className="sb-muted"> ({s.tradeName})</span> : null}
                </td>
                <td>{s.gstin ?? <span className="sb-muted">Unregistered</span>}</td>
                <td>{s.creditPeriodDays > 0 ? `${s.creditPeriodDays} days` : 'On receipt'}</td>
                <td className="sb-num">{moneyFormat.format(s.balance)}</td>
                <td className={`sb-num${s.overdue > 0 ? ' sb-error' : ''}`}>{s.overdue > 0 ? moneyFormat.format(s.overdue) : '-'}</td>
                <td>{s.isActive ? 'Active' : 'Switched off'}</td>
                {canManage ? (
                  <td>
                    <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setEditing(s)}>
                      Edit
                    </button>
                  </td>
                ) : null}
              </tr>
            ))}
          </tbody>
        </table>
        {suppliers.data && suppliers.data.length === 0 ? <p className="sb-muted">No suppliers yet.</p> : null}
      </section>

      {opened ? (
        <AccountView
          key={opened.id}
          business={business}
          partyType="SUPPLIER"
          partyId={opened.id}
          title={`Account of ${opened.name}`}
          onChanged={() => void suppliers.reload()}
          actions={(open, reload) =>
            canPay ? <SupplierPaymentForm business={business} supplierId={opened.id} stores={stores} open={open} onPaid={reload} /> : null
          }
        />
      ) : null}

      {canManage && editing ? (
        <section className="sb-card" aria-labelledby="edit-supplier-heading">
          <header className="sb-card__header">
            <h2 id="edit-supplier-heading">Edit {editing.code}</h2>
          </header>
          <ActionForm
            key={editing.id}
            submitLabel="Save supplier"
            onSubmit={async (data) => {
              await api.put(`${business}/suppliers/${editing.id}`, {
                name: text(data, 'name'),
                gstin: optional(data, 'gstin'),
                stateCode: text(data, 'stateCode'),
                isActive: data.get('isActive') === 'on',
                rowVersion: editing.rowVersion,
                ...partyContactFromForm(data),
              });
              setEditing(null);
              await suppliers.reload();
            }}
          >
            <div className="sb-form-row">
              <Field label="Legal name" name="name" defaultValue={editing.name} required />
              <Field label="GSTIN" name="gstin" defaultValue={editing.gstin ?? ''} hint="Leave empty for an unregistered supplier" />
              <Field label="GST state code" name="stateCode" defaultValue={editing.stateCode} inputMode="numeric" required />
            </div>
            <PartyContactFields party={editing} />
            <label className="sb-check">
              <input type="checkbox" name="isActive" defaultChecked={editing.isActive} /> Active (receipts can be made from this supplier)
            </label>
          </ActionForm>
        </section>
      ) : null}

      {canManage ? (
        <section className="sb-card" aria-labelledby="add-supplier-heading">
          <header className="sb-card__header">
            <h2 id="add-supplier-heading">Add a supplier</h2>
          </header>
          <ActionForm
            submitLabel="Add supplier"
            testId="add-supplier-form"
            onSubmit={async (data) => {
              await api.post(`${business}/suppliers`, {
                code: text(data, 'code'),
                name: text(data, 'name'),
                gstin: optional(data, 'gstin'),
                stateCode: text(data, 'stateCode'),
                ...partyContactFromForm(data),
                ...(canEnterBalances ? openingFromForm(data) : {}),
              });
              await suppliers.reload();
            }}
          >
            <div className="sb-form-row">
              <Field label="Code" name="code" required />
              <Field label="Legal name" name="name" required />
              <Field label="GSTIN" name="gstin" hint="Leave empty for an unregistered supplier" />
              <Field label="GST state code" name="stateCode" inputMode="numeric" required hint="For example 33 for Tamil Nadu" />
            </div>
            <PartyContactFields />
            {canEnterBalances ? (
              <div className="sb-form-row">
                <Field label="Opening balance (Rs., optional)" name="openingBalance" inputMode="decimal" hint="Owed to the supplier when you start; negative for an advance" />
                <Field label="Opening balance as of" name="openingBalanceDate" type="date" />
              </div>
            ) : null}
          </ActionForm>
        </section>
      ) : null}

      <PurchaseSettingsCard business={business} canChange={hasPermission(PurchasePermission.Approve)} />
    </>
  );
}

function PurchaseSettingsCard({ business, canChange }: { business: string | null; canChange: boolean }) {
  const settings = useApiData<PurchaseSettings>(business ? `${business}/purchase-settings` : null);
  const [saved, setSaved] = useState(false);
  const s = settings.data;

  return (
    <section className="sb-card" aria-labelledby="purchase-settings-heading">
      <header className="sb-card__header">
        <h2 id="purchase-settings-heading">Purchase checks</h2>
      </header>
      <ErrorText error={settings.error} />
      {saved ? <Notice tone="success">Purchase checks saved.</Notice> : null}
      {s && canChange ? (
        <ActionForm
          key={s.rowVersion}
          submitLabel="Save purchase checks"
          onSubmit={async (data) => {
            setSaved(false);
            await api.put(`${business}/purchase-settings`, {
              costReasonThresholdPercent: Number(text(data, 'reason')),
              costApprovalThresholdPercent: Number(text(data, 'approval')),
              allowLossLeader: data.get('lossLeader') === 'on',
              rowVersion: s.rowVersion,
            });
            await settings.reload();
            setSaved(true);
          }}
        >
          <div className="sb-form-row">
            <Field label="Cost change needing a reason (%)" name="reason" inputMode="decimal" defaultValue={String(s.costReasonThresholdPercent)} />
            <Field label="Cost change needing approval (%)" name="approval" inputMode="decimal" defaultValue={String(s.costApprovalThresholdPercent)} />
          </div>
          <label className="sb-check">
            <input type="checkbox" name="lossLeader" defaultChecked={s.allowLossLeader} /> Allow selling below cost as a loss-leader (with a reason and a manager&apos;s approval)
          </label>
        </ActionForm>
      ) : s ? (
        <p className="sb-muted">
          A cost change above {s.costReasonThresholdPercent}% needs a reason, above {s.costApprovalThresholdPercent}% a manager&apos;s approval. Selling below cost is{' '}
          {s.allowLossLeader ? 'allowed as a loss-leader with approval' : 'not allowed'}.
        </p>
      ) : null}
    </section>
  );
}
