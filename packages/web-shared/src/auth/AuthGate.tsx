'use client';

import { useEffect, useState, type ReactNode } from 'react';
import QRCode from 'qrcode';
import { api } from '../api';
import { SystemStatus } from '../SystemStatus';
import type { Me } from '../types';
import { ActionForm, Field, Notice, optional, text } from '../ui';
import { useAuth } from './AuthContext';

/**
 * Shows exactly one thing: first-run setup, sign-in, the MFA code prompt, a forced password change, forced MFA
 * enrolment, or (only for a fully signed-in session) the application itself. The API enforces the same states,
 * so this is a convenience, not the security boundary.
 */
export function AuthGate({ children, appTitle }: { children: ReactNode; appTitle: string }) {
  const { status, me } = useAuth();

  if (status === 'loading') {
    return <CenteredCard title={appTitle}><p className="sb-muted">Loading...</p></CenteredCard>;
  }

  if (status === 'unavailable') {
    return (
      <CenteredCard title={appTitle}>
        <Notice tone="warning">The billing server cannot be reached. Check that the store server is running.</Notice>
        <SystemStatus />
      </CenteredCard>
    );
  }

  if (status === 'setup-required') {
    return <CenteredCard title="First-time setup"><SetupForm /></CenteredCard>;
  }

  if (status === 'signed-out' || !me) {
    return (
      <CenteredCard title={appTitle}>
        <SignIn />
        <SystemStatus />
      </CenteredCard>
    );
  }

  switch (me.sessionState) {
    case 'mfa_required':
      return <CenteredCard title="Two-step verification"><MfaVerifyForm /></CenteredCard>;
    case 'password_change_required':
      return (
        <CenteredCard title="Choose a new password">
          <Notice>Your password was set by a manager. Choose your own password to continue.</Notice>
          <ChangePasswordForm />
        </CenteredCard>
      );
    case 'mfa_enrolment_required':
      return (
        <CenteredCard title="Set up two-step verification">
          <Notice>Your role requires two-step verification with an authenticator app.</Notice>
          <MfaEnrolment />
        </CenteredCard>
      );
    default:
      return <>{children}</>;
  }
}

function CenteredCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="sb-centered">
      <div className="sb-card sb-auth-card">
        <p className="sb-topbar__product">SupermarketBilling</p>
        <h1>{title}</h1>
        {children}
      </div>
    </div>
  );
}

function SignIn() {
  const [mode, setMode] = useState<'login' | 'reset'>('login');
  return mode === 'login' ? (
    <>
      <LoginForm />
      <button type="button" className="sb-link" onClick={() => setMode('reset')}>
        I have a password reset code
      </button>
    </>
  ) : (
    <>
      <ResetPasswordForm onDone={() => setMode('login')} />
      <button type="button" className="sb-link" onClick={() => setMode('login')}>
        Back to sign in
      </button>
    </>
  );
}

function LoginForm() {
  const { setMe, deploymentMode } = useAuth();
  return (
    <ActionForm
      testId="login-form"
      submitLabel="Sign in"
      onSubmit={async (data) => {
        setMe(await api.post<Me>('/api/v1/auth/login', { username: text(data, 'username'), password: data.get('password'), companyCode: optional(data, 'companyCode') }));
      }}
    >
      {deploymentMode === 'cloud' ? <Field label="Company code" name="companyCode" autoComplete="organization" required /> : null}
      <Field label="Username" name="username" autoComplete="username" autoFocus required />
      <Field label="Password" name="password" type="password" autoComplete="current-password" required />
    </ActionForm>
  );
}

function ResetPasswordForm({ onDone }: { onDone: () => void }) {
  const { deploymentMode } = useAuth();
  const [done, setDone] = useState(false);
  if (done) {
    return (
      <Notice tone="success">
        Password changed. <button type="button" className="sb-link" onClick={onDone}>Sign in</button>
      </Notice>
    );
  }

  return (
    <ActionForm
      submitLabel="Set new password"
      onSubmit={async (data) => {
        await api.post('/api/v1/auth/password/reset', {
          username: text(data, 'username'),
          resetCode: text(data, 'resetCode'),
          newPassword: data.get('newPassword'),
          companyCode: optional(data, 'companyCode'),
        });
        setDone(true);
      }}
    >
      {deploymentMode === 'cloud' ? <Field label="Company code" name="companyCode" autoComplete="organization" required /> : null}
      <Field label="Username" name="username" autoComplete="username" required />
      <Field label="Reset code from your manager" name="resetCode" autoComplete="one-time-code" placeholder="XXXX-XXXX-XXXX" required />
      <Field label="New password" name="newPassword" type="password" autoComplete="new-password" hint="At least 10 characters." required />
    </ActionForm>
  );
}

function MfaVerifyForm() {
  const { setMe, logout } = useAuth();
  return (
    <>
      <p className="sb-muted">Enter the 6-digit code from your authenticator app, or one of your recovery codes.</p>
      <ActionForm
        testId="mfa-verify-form"
        submitLabel="Verify"
        onSubmit={async (data) => {
          setMe(await api.post<Me>('/api/v1/auth/mfa/verify', { code: text(data, 'code') }));
        }}
      >
        <Field label="Code" name="code" inputMode="numeric" autoComplete="one-time-code" autoFocus required />
      </ActionForm>
      <button type="button" className="sb-link" onClick={() => void logout()}>
        Cancel and sign out
      </button>
    </>
  );
}

export function ChangePasswordForm({ onChanged }: { onChanged?: () => void }) {
  const { setMe } = useAuth();
  const [message, setMessage] = useState<string | null>(null);
  return (
    <>
      {message ? <Notice tone="success">{message}</Notice> : null}
      <ActionForm
        testId="change-password-form"
        submitLabel="Change password"
        onSubmit={async (data) => {
          if (data.get('newPassword') !== data.get('confirmPassword')) throw new Error('The new passwords do not match.');
          setMe(await api.post<Me>('/api/v1/auth/password/change', { currentPassword: data.get('currentPassword'), newPassword: data.get('newPassword') }));
          setMessage('Password changed. Your other sessions have been signed out.');
          onChanged?.();
        }}
      >
        <Field label="Current password" name="currentPassword" type="password" autoComplete="current-password" required />
        <Field label="New password" name="newPassword" type="password" autoComplete="new-password" hint="At least 10 characters, not containing your username." required />
        <Field label="Repeat new password" name="confirmPassword" type="password" autoComplete="new-password" required />
      </ActionForm>
    </>
  );
}

/** Enrolment: scan the QR code, confirm with a code, then save the one-time recovery codes. */
export function MfaEnrolment({ onEnabled }: { onEnabled?: () => void }) {
  const { refresh } = useAuth();
  const [setup, setSetup] = useState<{ secret: string; otpAuthUri: string } | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);

  useEffect(() => {
    if (setup) {
      void QRCode.toDataURL(setup.otpAuthUri, { margin: 1, width: 200 }).then(setQr);
    }
  }, [setup]);

  if (recoveryCodes) {
    return (
      <div data-testid="recovery-codes">
        <Notice tone="warning">
          Save these recovery codes somewhere safe (for example printed). Each works once if your phone is lost.
          They will not be shown again.
        </Notice>
        <ol className="sb-codes">
          {recoveryCodes.map((code) => (
            <li key={code}>{code}</li>
          ))}
        </ol>
        <button type="button" className="sb-button" onClick={() => { void refresh(); onEnabled?.(); }}>
          I have saved them - continue
        </button>
      </div>
    );
  }

  if (!setup) {
    return (
      <ActionForm submitLabel="Start setup" testId="mfa-start" onSubmit={async () => setSetup(await api.post('/api/v1/auth/mfa/setup'))}>
        <p className="sb-muted">You need an authenticator app such as Google Authenticator or Microsoft Authenticator. No internet connection is needed.</p>
      </ActionForm>
    );
  }

  return (
    <div className="sb-stack">
      <p>1. Scan this code with your authenticator app:</p>
      {qr ? <img src={qr} alt="QR code for your authenticator app" width={200} height={200} /> : null}
      <p className="sb-muted">
        Or enter this key manually: <code data-testid="mfa-secret">{setup.secret}</code>
      </p>
      <p>2. Enter the 6-digit code the app shows:</p>
      <ActionForm
        testId="mfa-confirm-form"
        submitLabel="Turn on two-step verification"
        onSubmit={async (data) => {
          const result = await api.post<{ recoveryCodes: string[] }>('/api/v1/auth/mfa/confirm', { code: text(data, 'code') });
          setRecoveryCodes(result.recoveryCodes);
        }}
      >
        <Field label="Code" name="code" inputMode="numeric" autoComplete="one-time-code" required />
      </ActionForm>
    </div>
  );
}

function SetupForm() {
  const { refresh } = useAuth();
  return (
    <>
      <p className="sb-muted">
        Create the first business, store and owner account. The setup code is in the file named in the API&apos;s
        startup log (by default <code>App_Data\setup-code.txt</code> next to the API).
      </p>
      <ActionForm
        testId="setup-form"
        submitLabel="Complete setup"
        onSubmit={async (data) => {
          if (data.get('ownerPassword') !== data.get('ownerPasswordConfirm')) throw new Error('The passwords do not match.');
          const stateCode = text(data, 'stateCode');
          await api.post('/api/v1/setup', {
            setupCode: text(data, 'setupCode'),
            provisioningKey: null,
            companyCode: text(data, 'companyCode'),
            business: {
              code: text(data, 'businessCode'),
              legalName: text(data, 'legalName'),
              tradeName: optional(data, 'tradeName'),
              stateCode,
              gstin: optional(data, 'gstin'),
              address: optional(data, 'address'),
            },
            store: { code: text(data, 'storeCode'), name: text(data, 'storeName'), stateCode, gstin: null, address: null },
            ownerUsername: text(data, 'ownerUsername'),
            ownerDisplayName: text(data, 'ownerDisplayName'),
            ownerPassword: data.get('ownerPassword'),
          });
          await refresh();
        }}
      >
        <Field label="Setup code" name="setupCode" autoComplete="off" required />
        <Field label="Company code" name="companyCode" hint="Your company's code for the SupermarketBilling service: 3-20 letters or digits, e.g. SRIMURUGAN." required />
        <fieldset className="sb-fieldset">
          <legend>Business</legend>
          <Field label="Business code" name="businessCode" placeholder="e.g. SMKT" required />
          <Field label="Legal name" name="legalName" required />
          <Field label="Trade name" name="tradeName" />
          <Field label="GST state code" name="stateCode" placeholder="e.g. 33" inputMode="numeric" required />
          <Field label="GSTIN (if registered)" name="gstin" />
          <Field label="Address" name="address" />
        </fieldset>
        <fieldset className="sb-fieldset">
          <legend>First store</legend>
          <Field label="Store code" name="storeCode" defaultValue="MAIN" required />
          <Field label="Store name" name="storeName" required />
        </fieldset>
        <fieldset className="sb-fieldset">
          <legend>Owner account</legend>
          <Field label="Username" name="ownerUsername" autoComplete="username" required />
          <Field label="Your name" name="ownerDisplayName" required />
          <Field label="Password" name="ownerPassword" type="password" autoComplete="new-password" hint="At least 10 characters." required />
          <Field label="Repeat password" name="ownerPasswordConfirm" type="password" autoComplete="new-password" required />
        </fieldset>
      </ActionForm>
    </>
  );
}
