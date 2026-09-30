'use client';

import { useId, useState, type FormEvent, type InputHTMLAttributes, type ReactNode } from 'react';
import { errorMessage } from './api';

/** Labelled input. The hint is linked with aria-describedby so it is announced but is not part of the label. */
export function Field({
  label,
  hint,
  ...input
}: { label: string; hint?: string } & InputHTMLAttributes<HTMLInputElement>) {
  const id = useId();
  const hintId = hint ? `${id}-hint` : undefined;
  return (
    <div className="sb-field">
      <label className="sb-field__label" htmlFor={id}>
        {label}
      </label>
      <input id={id} className="sb-input" aria-describedby={hintId} {...input} />
      {hint ? (
        <span id={hintId} className="sb-muted sb-field__hint">
          {hint}
        </span>
      ) : null}
    </div>
  );
}

export function ErrorText({ error }: { error: unknown }) {
  if (!error) return null;
  return (
    <p className="sb-error" role="alert" data-testid="form-error">
      {errorMessage(error)}
    </p>
  );
}

export function Notice({ children, tone = 'info' }: { children: ReactNode; tone?: 'info' | 'success' | 'warning' }) {
  return (
    <div className={`sb-notice sb-notice--${tone}`} role="status">
      {children}
    </div>
  );
}

/**
 * A form that tracks its own busy and error state, so every screen handles failures the same way:
 * the API's message is shown, and the button is disabled while the request runs.
 */
export function ActionForm({
  onSubmit,
  submitLabel,
  children,
  testId,
}: {
  onSubmit: (data: FormData) => Promise<void>;
  submitLabel: string;
  children: ReactNode;
  testId?: string;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function handle(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    setBusy(true);
    setError(null);
    try {
      await onSubmit(new FormData(form));
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="sb-form" onSubmit={handle} data-testid={testId} noValidate>
      {children}
      <ErrorText error={error} />
      <button className="sb-button" type="submit" disabled={busy}>
        {busy ? 'Please wait...' : submitLabel}
      </button>
    </form>
  );
}

export function text(data: FormData, name: string): string {
  const value = data.get(name);
  return typeof value === 'string' ? value.trim() : '';
}

export function optional(data: FormData, name: string): string | null {
  const value = text(data, name);
  return value.length > 0 ? value : null;
}

export function formatDateTime(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString() : '-';
}
