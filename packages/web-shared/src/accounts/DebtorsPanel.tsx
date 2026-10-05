'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { AccountPermission, DebtorStatusLabels, type CustomerGroupInfo, type Debtor } from '../types';
import { ActionForm, ErrorText, Field, optional, text } from '../ui';
import { moneyFormat, useStoreChoice } from '../stock/StockPanel';
import { AccountView } from './AccountView';
import { DebtorReceiptForm, openingFromForm, partyContactFromForm, PartyContactFields } from './PartyForms';
import { PromisesCard } from '../collections/CollectorHome';
import { ReceiptsCard } from '../collections/CustodyCards';
import { MessageLog } from '../messaging/MessagingPanel';
import { DeliveryPreferenceCard } from '../dispatch/DeliveryPreferenceCard';

/** Debtors (customers on credit): their limits, what they owe and how overdue it is, and each one's account. */
export function DebtorsPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [status, setStatus] = useState('');
  const debtors = useApiData<Debtor[]>(
    business ? `${business}/debtors?${new URLSearchParams({ ...(query ? { search: query } : {}), ...(status ? { status } : {}) }).toString()}` : null,
  );
  const groups = useApiData<CustomerGroupInfo[]>(business ? `${business}/catalog/customer-groups` : null);
  const [editing, setEditing] = useState<Debtor | null>(null);
  const [opened, setOpened] = useState<Debtor | null>(null);
  const canManage = hasPermission(AccountPermission.DebtorsManage);
  const canEnterBalances = hasPermission(AccountPermission.Adjust);
  const canReceive = hasPermission(AccountPermission.Receivables);
  const { stores } = useStoreChoice();

  function groupSelect(value: string | null) {
    return (
      <label className="sb-field">
        <span className="sb-field__label">Customer group (prices)</span>
        <select className="sb-input" name="customerGroupId" defaultValue={value ?? ''}>
          <option value="">None</option>
          {(groups.data ?? []).map((g) => (
            <option key={g.id} value={g.id}>{g.name}</option>
          ))}
        </select>
      </label>
    );
  }

  return (
    <>
      <section className="sb-card" aria-labelledby="debtors-heading">
        <header className="sb-card__header">
          <h2 id="debtors-heading">Debtors</h2>
        </header>
        <form
          className="sb-inline-form"
          role="search"
          onSubmit={(event) => {
            event.preventDefault();
            setQuery(search.trim());
          }}
        >
          <input className="sb-input" aria-label="Search debtors" placeholder="Name, code, GSTIN or phone" value={search} onChange={(e) => setSearch(e.target.value)} />
          <select className="sb-input" aria-label="Account status" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All accounts</option>
            {Object.entries(DebtorStatusLabels).map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
          <button className="sb-button sb-button--secondary" type="submit">Search</button>
        </form>
        <ErrorText error={debtors.error} />
        <table className="sb-table" data-testid="debtors-table">
          <thead>
            <tr>
              <th>Code</th>
              <th>Name</th>
              <th>Phone / WhatsApp</th>
              <th>Credit</th>
              <th className="sb-num">Owes</th>
              <th className="sb-num">Overdue</th>
              <th>Status</th>
              {canManage ? <th /> : null}
            </tr>
          </thead>
          <tbody>
            {(debtors.data ?? []).map((d) => (
              <tr key={d.id}>
                <td>{d.code}</td>
                <td>
                  <button type="button" className="sb-link" onClick={() => setOpened(d)}>
                    {d.displayName}
                  </button>
                </td>
                <td>{[d.phone, d.whatsAppNumber].filter(Boolean).join(' / ') || '-'}</td>
                <td>
                  {d.creditLimit > 0 ? `Rs. ${moneyFormat.format(d.creditLimit)}, ${d.creditPeriodDays} days` : 'Cash only'}
                </td>
                <td className="sb-num">{moneyFormat.format(d.balance)}</td>
                <td className={`sb-num${d.overdue > 0 ? ' sb-error' : ''}`}>{d.overdue > 0 ? moneyFormat.format(d.overdue) : '-'}</td>
                <td>{DebtorStatusLabels[d.status] ?? d.status}</td>
                {canManage ? (
                  <td>
                    <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => setEditing(d)}>
                      Edit
                    </button>
                  </td>
                ) : null}
              </tr>
            ))}
          </tbody>
        </table>
        {debtors.data && debtors.data.length === 0 ? <p className="sb-muted">No debtors found.</p> : null}
      </section>

      {opened ? (
        <AccountView
          key={opened.id}
          business={business}
          partyType="DEBTOR"
          partyId={opened.id}
          title={`Account of ${opened.displayName}`}
          onChanged={() => void debtors.reload()}
          actions={(open, reload) => (
            <>
              {canReceive ? <DebtorReceiptForm business={business} debtorId={opened.id} stores={stores} open={open} onReceived={reload} /> : null}
              {hasPermission('collections.view') ? <PromisesCard business={business} debtorId={opened.id} /> : null}
              <ReceiptsCard business={business} debtorId={opened.id} />
              <DeliveryPreferenceCard business={business} debtorId={opened.id} canManage={canManage} canSeeTransporters={hasPermission('dispatch.view')} />
              {hasPermission('messaging.view') ? <MessageLog business={business} debtorId={opened.id} canRetry={hasPermission('messaging.manage')} /> : null}
            </>
          )}
        />
      ) : null}

      {canManage && editing ? (
        <section className="sb-card" aria-labelledby="edit-debtor-heading">
          <header className="sb-card__header">
            <h2 id="edit-debtor-heading">Edit {editing.code}</h2>
          </header>
          <ActionForm
            key={editing.id}
            submitLabel="Save debtor"
            onSubmit={async (data) => {
              await api.put(`${business}/debtors/${editing.id}`, {
                legalName: text(data, 'legalName'),
                gstin: optional(data, 'gstin'),
                stateCode: text(data, 'stateCode'),
                creditLimit: Number(text(data, 'creditLimit') || '0'),
                customerGroupId: optional(data, 'customerGroupId'),
                status: text(data, 'status'),
                rowVersion: editing.rowVersion,
                ...partyContactFromForm(data),
              });
              setEditing(null);
              await debtors.reload();
            }}
          >
            <div className="sb-form-row">
              <Field label="Legal name" name="legalName" defaultValue={editing.legalName} required />
              <Field label="GSTIN" name="gstin" defaultValue={editing.gstin ?? ''} />
              <Field label="GST state code" name="stateCode" defaultValue={editing.stateCode} inputMode="numeric" required />
            </div>
            <PartyContactFields party={editing} />
            <div className="sb-form-row">
              <Field label="Credit limit (Rs.)" name="creditLimit" inputMode="decimal" defaultValue={String(editing.creditLimit)} hint="0 means cash only" />
              {groupSelect(editing.customerGroupId)}
              <label className="sb-field">
                <span className="sb-field__label">Account status</span>
                <select className="sb-input" name="status" defaultValue={editing.status}>
                  {Object.entries(DebtorStatusLabels).map(([value, label]) => (
                    <option key={value} value={value}>{label}</option>
                  ))}
                </select>
              </label>
            </div>
          </ActionForm>
        </section>
      ) : null}

      {canManage ? (
        <section className="sb-card" aria-labelledby="add-debtor-heading">
          <header className="sb-card__header">
            <h2 id="add-debtor-heading">Add a debtor</h2>
          </header>
          <ActionForm
            submitLabel="Add debtor"
            testId="add-debtor-form"
            onSubmit={async (data) => {
              await api.post(`${business}/debtors`, {
                code: text(data, 'code'),
                legalName: text(data, 'legalName'),
                gstin: optional(data, 'gstin'),
                stateCode: text(data, 'stateCode'),
                creditLimit: Number(text(data, 'creditLimit') || '0'),
                customerGroupId: optional(data, 'customerGroupId'),
                ...partyContactFromForm(data),
                ...(canEnterBalances ? openingFromForm(data) : {}),
              });
              await debtors.reload();
            }}
          >
            <div className="sb-form-row">
              <Field label="Code" name="code" required />
              <Field label="Legal name" name="legalName" required />
              <Field label="GSTIN (for B2B invoices)" name="gstin" />
              <Field label="GST state code" name="stateCode" inputMode="numeric" required defaultValue="33" />
            </div>
            <PartyContactFields />
            <div className="sb-form-row">
              <Field label="Credit limit (Rs.)" name="creditLimit" inputMode="decimal" defaultValue="0" hint="0 means cash only" />
              {groupSelect(null)}
            </div>
            {canEnterBalances ? (
              <div className="sb-form-row">
                <Field label="Opening balance (Rs., optional)" name="openingBalance" inputMode="decimal" hint="Owed by the debtor when you start; negative for an advance" />
                <Field label="Opening balance as of" name="openingBalanceDate" type="date" />
              </div>
            ) : null}
          </ActionForm>
        </section>
      ) : null}
    </>
  );
}
