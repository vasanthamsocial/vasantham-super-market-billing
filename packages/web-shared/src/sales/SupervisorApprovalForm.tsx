'use client';

import { useState } from 'react';
import { api, errorMessage } from '../api';
import type { SupervisorApproval } from '../types';

export interface ApprovalRequestDetails {
  kind: 'PRICE_OVERRIDE' | 'DISCOUNT' | 'RETURN' | 'PAY_OUT' | 'CREDIT_LIMIT';
  variantUnitId?: string;
  price?: number;
  maxAmount?: number;
}

/**
 * A supervisor approves at the counter by typing their own sign-in details. Their failed attempts count towards their
 * own lockout; the approval works once, for this counter and cashier, for 10 minutes.
 */
export function SupervisorApprovalForm({ what, request, onApproved }: { what: string; request: ApprovalRequestDetails; onApproved: (approval: SupervisorApproval) => void }) {
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  return (
    <form
      className="sb-form"
      data-testid="approval-form"
      onSubmit={(e) => {
        e.preventDefault();
        const data = new FormData(e.currentTarget);
        const value = (name: string) => String(data.get(name) ?? '');
        setBusy(true);
        setError(null);
        void api
          .post<SupervisorApproval>('/api/v1/pos/supervisor-approvals', {
            username: value('username'),
            password: value('password'),
            mfaCode: value('mfaCode') || null,
            kind: request.kind,
            variantUnitId: request.variantUnitId ?? null,
            price: request.price ?? null,
            maxAmount: request.maxAmount ?? null,
            reason: value('reason'),
          })
          .then(onApproved)
          .catch(setError)
          .finally(() => setBusy(false));
      }}
    >
      <p>{what} needs a supervisor. The supervisor enters their own sign-in details:</p>
      {[
        { label: 'Supervisor username', name: 'username', type: 'text', auto: 'off' },
        { label: 'Password', name: 'password', type: 'password', auto: 'off' },
        { label: 'Two-step code (if they use one)', name: 'mfaCode', type: 'text', auto: 'one-time-code' },
        { label: 'Reason', name: 'reason', type: 'text', auto: 'off' },
      ].map((f, i) => (
        <div className="sb-field" key={f.name}>
          <label className="sb-field__label" htmlFor={`approval-${f.name}`}>{f.label}</label>
          <input id={`approval-${f.name}`} className="sb-input" name={f.name} type={f.type} autoComplete={f.auto} autoFocus={i === 0} />
        </div>
      ))}
      {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
      <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Checking...' : 'Approve'}</button>
    </form>
  );
}
