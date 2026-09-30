'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import { CatalogPermission, TaxModeLabels, type TaxRegistrationInfo } from '../types';
import { ActionForm, ErrorText, Field, formatDateTime, Notice, optional, text } from '../ui';

/**
 * The legal GST registration history. Changes are prepared by the accountant, approved by another person in
 * Approvals, and can only take effect today or later; past bills are never reinterpreted.
 */
export function TaxRegistrationPanel() {
  const { membership, hasPermission } = useAuth();
  const url = membership ? `/api/v1/businesses/${membership.businessId}/tax-registrations` : null;
  const history = useApiData<TaxRegistrationInfo[]>(url);
  const [message, setMessage] = useState<string | null>(null);
  const current = history.data?.find((h) => h.isCurrent);

  return (
    <section className="sb-card" aria-labelledby="tax-heading">
      <header className="sb-card__header">
        <h2 id="tax-heading">GST registration</h2>
        {current ? <span className="sb-badge sb-badge--live" data-testid="current-tax-mode">{TaxModeLabels[current.mode]}</span> : null}
      </header>
      <ErrorText error={history.error} />
      {message ? <Notice tone="success">{message}</Notice> : null}
      <table className="sb-table" data-testid="tax-history">
        <thead>
          <tr>
            <th>From</th>
            <th>Registration</th>
            <th>GSTIN</th>
            <th>Reason</th>
            <th>Recorded by</th>
          </tr>
        </thead>
        <tbody>
          {(history.data ?? []).map((h) => (
            <tr key={h.id}>
              <td>{h.effectiveFrom}{h.isCurrent ? ' (current)' : ''}</td>
              <td>{TaxModeLabels[h.mode]}</td>
              <td>{h.gstin ?? '-'}</td>
              <td>{h.reason}{h.evidenceReference ? ` (evidence: ${h.evidenceReference})` : ''}</td>
              <td>{h.recordedBy}, {formatDateTime(h.recordedAtUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {hasPermission(CatalogPermission.TaxReview) && url ? (
        <details className="sb-details">
          <summary>Request a change (accountant)</summary>
          <p className="sb-muted">
            The change is checked and approved by an owner or manager under Approvals. It cannot take effect in the past.
          </p>
          <ActionForm testId="tax-change-form" submitLabel="Submit for approval" onSubmit={async (data) => {
            await api.post(`${url}/change-requests`, {
              mode: text(data, 'mode'),
              gstin: optional(data, 'gstin'),
              effectiveFrom: text(data, 'effectiveFrom'),
              reason: text(data, 'reason'),
              evidenceReference: optional(data, 'evidence'),
              backupConfirmed: data.get('backup') === 'on',
            });
            setMessage('Submitted. It will apply once approved.');
            await history.reload();
          }}>
            <label className="sb-field">
              <span className="sb-field__label">New registration</span>
              <select className="sb-input" name="mode">
                {Object.entries(TaxModeLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
              </select>
            </label>
            <Field label="GSTIN" name="gstin" hint="Required for regular and composition." />
            <Field label="Effective from" name="effectiveFrom" type="date" required />
            <Field label="Reason" name="reason" required />
            <Field label="Evidence reference" name="evidence" hint="Certificate or order number, and where the document is kept." />
            <label className="sb-check"><input type="checkbox" name="backup" /> A verified backup was taken today</label>
          </ActionForm>
        </details>
      ) : null}
    </section>
  );
}
