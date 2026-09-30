'use client';

import { useAuth } from '../auth/AuthContext';
import { Permission, type AuditEvent } from '../types';
import { ErrorText, formatDateTime, Notice } from '../ui';
import { useApiData } from './useApiData';

export function AuditPanel() {
  const { membership, hasPermission } = useAuth();
  const { data, error, reload, loading } = useApiData<AuditEvent[]>(
    membership && hasPermission(Permission.AuditView) ? `/api/v1/businesses/${membership.businessId}/audit?limit=200` : null,
  );

  if (!hasPermission(Permission.AuditView)) {
    return <Notice tone="warning">You do not have permission to view the audit trail.</Notice>;
  }

  return (
    <section className="sb-card" aria-labelledby="audit-heading">
      <header className="sb-card__header">
        <h2 id="audit-heading">Audit trail</h2>
        <button type="button" className="sb-button sb-button--small sb-button--secondary" onClick={() => void reload()} disabled={loading}>
          Refresh
        </button>
      </header>
      <p className="sb-muted">Every sign-in, change and approval is recorded here permanently. Entries cannot be edited or deleted.</p>
      <ErrorText error={error} />
      <table className="sb-table" data-testid="audit-table">
        <thead>
          <tr>
            <th>#</th>
            <th>When</th>
            <th>Event</th>
            <th>By</th>
            <th>Details</th>
          </tr>
        </thead>
        <tbody>
          {(data ?? []).map((event) => (
            <tr key={event.sequence}>
              <td>{event.sequence}</td>
              <td>{formatDateTime(event.occurredAtUtc)}</td>
              <td>{event.eventType}</td>
              <td>{event.actor ?? '-'}</td>
              <td className="sb-code-cell">{summarise(event.payloadJson)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function summarise(payloadJson: string): string {
  try {
    const payload = JSON.parse(payloadJson) as { details?: unknown };
    return payload.details ? JSON.stringify(payload.details) : '';
  } catch {
    return payloadJson;
  }
}
