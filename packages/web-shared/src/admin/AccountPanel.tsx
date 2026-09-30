'use client';

import { useState } from 'react';
import { api } from '../api';
import { ChangePasswordForm, MfaEnrolment } from '../auth/AuthGate';
import { useAuth } from '../auth/AuthContext';
import type { SessionInfo } from '../types';
import { ActionForm, ErrorText, Field, formatDateTime, Notice, text } from '../ui';
import { useApiData } from './useApiData';

/** The signed-in user's own account: password, two-step verification and active sessions. */
export function AccountPanel() {
  const { me, refresh } = useAuth();
  const sessions = useApiData<SessionInfo[]>('/api/v1/auth/sessions');
  const [sessionError, setSessionError] = useState<unknown>(null);

  if (!me) return null;

  return (
    <div className="sb-stack">
      <section className="sb-card" aria-labelledby="account-heading">
        <header className="sb-card__header">
          <h2 id="account-heading">{me.displayName}</h2>
          <span className="sb-muted">{me.username}</span>
        </header>
        <ul className="sb-plain-list">
          {me.memberships.map((m) => (
            <li key={m.businessId}>
              <strong>{m.businessName}</strong>: {m.roles.map((r) => r.roleName + (r.storeName ? ` @ ${r.storeName}` : '')).join(', ')}
            </li>
          ))}
        </ul>
      </section>

      <section className="sb-card" aria-labelledby="password-heading">
        <h2 id="password-heading">Change password</h2>
        <ChangePasswordForm onChanged={() => void sessions.reload()} />
      </section>

      <section className="sb-card" aria-labelledby="mfa-heading">
        <h2 id="mfa-heading">Two-step verification</h2>
        {me.mfaEnabled ? (
          <>
            <Notice tone="success">Two-step verification is on.</Notice>
            {me.mfaRequiredByPolicy ? (
              <p className="sb-muted">Your role requires it, so it cannot be turned off.</p>
            ) : (
              <details className="sb-details">
                <summary>Turn off</summary>
                <ActionForm
                  submitLabel="Turn off two-step verification"
                  onSubmit={async (data) => {
                    await api.post('/api/v1/auth/mfa/disable', { password: data.get('password'), code: text(data, 'code') });
                    await refresh();
                  }}
                >
                  <Field label="Password" name="password" type="password" autoComplete="current-password" required />
                  <Field label="Current code" name="code" inputMode="numeric" autoComplete="one-time-code" required />
                </ActionForm>
              </details>
            )}
          </>
        ) : (
          <MfaEnrolment />
        )}
      </section>

      <section className="sb-card" aria-labelledby="sessions-heading">
        <h2 id="sessions-heading">Where you are signed in</h2>
        <ErrorText error={sessionError ?? sessions.error} />
        <ul className="sb-module-list">
          {(sessions.data ?? []).map((session) => (
            <li key={session.id} className="sb-module">
              <div>
                <strong>{session.isCurrent ? 'This device' : session.userAgent ?? 'Unknown device'}</strong>
                <p className="sb-muted">
                  From {session.ipAddress ?? 'unknown'} - signed in {formatDateTime(session.createdAtUtc)}, last active {formatDateTime(session.lastSeenAtUtc)}
                </p>
              </div>
              {!session.isCurrent ? (
                <button
                  type="button"
                  className="sb-button sb-button--small sb-button--secondary"
                  onClick={async () => {
                    setSessionError(null);
                    try {
                      await api.del(`/api/v1/auth/sessions/${session.id}`);
                      await sessions.reload();
                    } catch (caught) {
                      setSessionError(caught);
                    }
                  }}
                >
                  End session
                </button>
              ) : null}
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
