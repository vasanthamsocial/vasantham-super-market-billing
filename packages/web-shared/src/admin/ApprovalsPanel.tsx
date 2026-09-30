'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { Permission, type Approval } from '../types';
import { ErrorText, formatDateTime, Notice } from '../ui';
import { useApiData } from './useApiData';

/** Maker-checker queue: requests waiting for a second person, and the decision history. */
export function ApprovalsPanel() {
  const { membership, hasPermission, me } = useAuth();
  const [filter, setFilter] = useState<'pending' | ''>('pending');
  const path = membership ? `/api/v1/businesses/${membership.businessId}/approvals${filter ? `?status=${filter}` : ''}` : null;
  const { data, error, reload } = useApiData<Approval[]>(path);
  const [actionError, setActionError] = useState<unknown>(null);

  if (!hasPermission(Permission.ApprovalsView)) {
    return <Notice tone="warning">You do not have permission to view approvals.</Notice>;
  }

  async function decide(approval: Approval, decision: 'approve' | 'reject' | 'cancel') {
    setActionError(null);
    let note: string | null = null;
    if (decision !== 'cancel') {
      note = window.prompt(decision === 'approve' ? 'Note (optional)' : 'Reason for rejecting (required)') ?? null;
      if (decision === 'reject' && !note) return;
    }
    try {
      await api.post(`/api/v1/approvals/${approval.id}/${decision}`, decision === 'cancel' ? {} : { note });
      await reload();
    } catch (caught) {
      setActionError(caught);
    }
  }

  return (
    <section className="sb-card" aria-labelledby="approvals-heading">
      <header className="sb-card__header">
        <h2 id="approvals-heading">Approvals</h2>
        <select className="sb-input sb-input--inline" value={filter} onChange={(e) => setFilter(e.target.value as 'pending' | '')} aria-label="Show">
          <option value="pending">Waiting for decision</option>
          <option value="">All requests</option>
        </select>
      </header>
      <ErrorText error={actionError ?? error} />
      {data && data.length === 0 ? <p className="sb-muted">Nothing to show.</p> : null}
      <ul className="sb-module-list" data-testid="approvals-list">
        {(data ?? []).map((approval) => (
          <li key={approval.id} className="sb-module" data-testid="approval-item">
            <div>
              <strong>{approval.summary}</strong>
              <p className="sb-muted">
                Requested by {approval.requestedBy} on {formatDateTime(approval.requestedAtUtc)}
                {approval.reason ? ` - "${approval.reason}"` : ''}
              </p>
              {approval.decidedBy ? (
                <p className="sb-muted">
                  {approval.status} by {approval.decidedBy} on {formatDateTime(approval.decidedAtUtc)}
                  {approval.decisionNote ? ` - "${approval.decisionNote}"` : ''}
                </p>
              ) : null}
            </div>
            <div className="sb-actions">
              <span className={`sb-badge sb-badge--${approval.status === 'pending' ? 'delayed' : approval.status === 'approved' ? 'live' : 'planned'}`}>
                {approval.status}
              </span>
              {approval.canDecide ? (
                <>
                  <button type="button" className="sb-button sb-button--small" onClick={() => void decide(approval, 'approve')}>Approve</button>
                  <button type="button" className="sb-button sb-button--small sb-button--secondary" onClick={() => void decide(approval, 'reject')}>Reject</button>
                </>
              ) : null}
              {approval.status === 'pending' && approval.requestedByUserId === me?.userId ? (
                <button type="button" className="sb-button sb-button--small sb-button--secondary" onClick={() => void decide(approval, 'cancel')}>Cancel</button>
              ) : null}
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
