'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  MessageChannelLabels,
  MessageKindLabels,
  MessageStatusLabels,
  MessagingPermission,
  type MessageTemplate,
  type MessagingSettings,
  type OutboundMessage,
} from '../types';
import { ActionForm, ErrorText, Field, formatDateTime, optional, text } from '../ui';

/** WhatsApp and SMS to debtors: whether they are sent, the wording, and every message with its delivery history. */
export function MessagingPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const settings = useApiData<MessagingSettings>(business ? `${business}/messaging/settings` : null);
  const templates = useApiData<MessageTemplate[]>(business ? `${business}/messaging/templates` : null);
  const [editing, setEditing] = useState<MessageTemplate | null>(null);
  const canManage = hasPermission(MessagingPermission.Manage);
  if (!business) return null;
  const s = settings.data;

  return (
    <>
      <section className="sb-card" aria-labelledby="messaging-settings-heading">
        <header className="sb-card__header">
          <h2 id="messaging-settings-heading">Messages to debtors</h2>
        </header>
        <p className="sb-muted">
          Credit invoices (with the PDF) and receipts are sent to debtors who agreed to receive them. Sending happens after the bill is saved; a failure never
          affects the bill or the receipt and is retried.
        </p>
        <ErrorText error={settings.error} />
        {s ? (
          <>
            <p>
              WhatsApp provider: <strong>{s.whatsAppProvider}</strong>
              {s.whatsAppReady ? '' : ' (not set up for this installation; messages wait until it is)'}. SMS provider: <strong>{s.smsProvider}</strong>.
            </p>
            {canManage ? (
              <ActionForm
                key={s.rowVersion}
                submitLabel="Save messaging settings"
                testId="messaging-settings-form"
                onSubmit={async (data) => {
                  await api.put(`${business}/messaging/settings`, {
                    whatsAppEnabled: data.get('whatsAppEnabled') === 'on',
                    smsEnabled: data.get('smsEnabled') === 'on',
                    sendInvoices: data.get('sendInvoices') === 'on',
                    sendReceipts: data.get('sendReceipts') === 'on',
                    rowVersion: s.rowVersion,
                  });
                  await settings.reload();
                }}
              >
                <label className="sb-check">
                  <input type="checkbox" name="whatsAppEnabled" defaultChecked={s.whatsAppEnabled} /> Send WhatsApp messages
                </label>
                <label className="sb-check">
                  <input type="checkbox" name="smsEnabled" defaultChecked={s.smsEnabled} /> Send SMS
                </label>
                <label className="sb-check">
                  <input type="checkbox" name="sendInvoices" defaultChecked={s.sendInvoices} /> For credit invoices
                </label>
                <label className="sb-check">
                  <input type="checkbox" name="sendReceipts" defaultChecked={s.sendReceipts} /> For receipts
                </label>
              </ActionForm>
            ) : (
              <p>
                WhatsApp {s.whatsAppEnabled ? 'on' : 'off'}, SMS {s.smsEnabled ? 'on' : 'off'}; invoices {s.sendInvoices ? 'on' : 'off'}, receipts{' '}
                {s.sendReceipts ? 'on' : 'off'}.
              </p>
            )}
          </>
        ) : null}
      </section>

      <section className="sb-card" aria-labelledby="templates-heading">
        <header className="sb-card__header">
          <h2 id="templates-heading">Wording</h2>
        </header>
        <p className="sb-muted">
          WhatsApp sends the template approved by Meta under the given name, with the values in this order. SMS sends the text below and needs the DLT template
          id registered for exactly that text.
        </p>
        <ErrorText error={templates.error} />
        <table className="sb-table" data-testid="templates-table">
          <thead>
            <tr>
              <th>Message</th>
              <th>Channel</th>
              <th>Template name / DLT id</th>
              <th>Text</th>
              <th>Active</th>
              {canManage ? <th /> : null}
            </tr>
          </thead>
          <tbody>
            {(templates.data ?? []).map((t) => (
              <tr key={`${t.kind}-${t.channel}`}>
                <td>{MessageKindLabels[t.kind] ?? t.kind}</td>
                <td>{MessageChannelLabels[t.channel] ?? t.channel}</td>
                <td>{(t.channel === 'SMS' ? t.dltTemplateId : t.providerTemplateName) ?? '-'}</td>
                <td>{t.body}</td>
                <td>{t.isActive ? 'Yes' : 'No'}</td>
                {canManage ? (
                  <td>
                    <button
                      type="button"
                      className="sb-button sb-button--secondary sb-button--small"
                      aria-label={`Edit ${MessageKindLabels[t.kind] ?? t.kind} ${MessageChannelLabels[t.channel] ?? t.channel}`}
                      onClick={() => setEditing(t)}
                    >
                      Edit
                    </button>
                  </td>
                ) : null}
              </tr>
            ))}
          </tbody>
        </table>
        {canManage && editing ? (
          <ActionForm
            key={`${editing.kind}-${editing.channel}`}
            submitLabel="Save wording"
            testId="template-form"
            onSubmit={async (data) => {
              await api.put(`${business}/messaging/templates/${editing.kind}/${editing.channel}`, {
                providerTemplateName: optional(data, 'providerTemplateName'),
                dltTemplateId: optional(data, 'dltTemplateId'),
                languageCode: text(data, 'languageCode'),
                body: text(data, 'body'),
                isActive: data.get('isActive') === 'on',
              });
              setEditing(null);
              await templates.reload();
            }}
          >
            <h3>
              {MessageKindLabels[editing.kind] ?? editing.kind} by {MessageChannelLabels[editing.channel] ?? editing.channel}
            </h3>
            <div className="sb-form-row">
              {editing.channel === 'WHATSAPP' ? (
                <Field label="Approved template name" name="providerTemplateName" defaultValue={editing.providerTemplateName ?? ''} hint="As approved in WhatsApp Manager" />
              ) : (
                <Field label="DLT template id" name="dltTemplateId" defaultValue={editing.dltTemplateId ?? ''} inputMode="numeric" />
              )}
              <Field label="Language code" name="languageCode" defaultValue={editing.languageCode} required />
            </div>
            <label className="sb-field">
              <span className="sb-field__label">Text</span>
              <textarea className="sb-input" name="body" rows={3} defaultValue={editing.body} required />
            </label>
            <p className="sb-muted sb-field__hint">You can use {editing.placeholders.map((p) => `{{${p}}}`).join(', ')}</p>
            <label className="sb-check">
              <input type="checkbox" name="isActive" defaultChecked={editing.isActive} /> Active
            </label>
          </ActionForm>
        ) : null}
      </section>

      <MessageLog business={business} canRetry={canManage} />
    </>
  );
}

/** Messages with their delivery state; each opens to its full history. A debtor's account shows only theirs. */
export function MessageLog({ business, debtorId, canRetry }: { business: string | null; debtorId?: string; canRetry: boolean }) {
  const [status, setStatus] = useState('');
  const query = new URLSearchParams({ ...(status ? { status } : {}), ...(debtorId ? { debtorId } : {}) }).toString();
  const messages = useApiData<OutboundMessage[]>(business ? `${business}/messages${query ? `?${query}` : ''}` : null);
  const [opened, setOpened] = useState<string | null>(null);
  const [retryError, setRetryError] = useState<unknown>(null);

  async function retry(id: string) {
    setRetryError(null);
    try {
      await api.post(`${business}/messages/${id}/retry`, {});
      await messages.reload();
    } catch (caught) {
      setRetryError(caught);
    }
  }

  return (
    <section className="sb-card" aria-labelledby={`messages-heading-${debtorId ?? 'all'}`}>
      <header className="sb-card__header">
        <h2 id={`messages-heading-${debtorId ?? 'all'}`}>{debtorId ? 'Messages sent' : 'Message log'}</h2>
        <select className="sb-input" aria-label="Message status" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">All messages</option>
          {['QUEUED', 'SENT', 'DELIVERED', 'READ', 'FAILED', 'SKIPPED'].map((value) => (
            <option key={value} value={value}>{MessageStatusLabels[value]}</option>
          ))}
        </select>
      </header>
      <ErrorText error={messages.error ?? retryError} />
      <table className="sb-table" data-testid="messages-table">
        <thead>
          <tr>
            <th>When</th>
            {debtorId ? null : <th>Debtor</th>}
            <th>About</th>
            <th>Channel</th>
            <th>To</th>
            <th>Status</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {(messages.data ?? []).map((m) => (
            <MessageRow key={m.id} message={m} showDebtor={!debtorId} open={opened === m.id} onToggle={() => setOpened(opened === m.id ? null : m.id)}
              onRetry={canRetry && m.status === 'FAILED' && !m.sentAtUtc ? () => void retry(m.id) : undefined} />
          ))}
        </tbody>
      </table>
      {messages.data && messages.data.length === 0 ? <p className="sb-muted">No messages.</p> : null}
    </section>
  );
}

function MessageRow({
  message: m,
  showDebtor,
  open,
  onToggle,
  onRetry,
}: {
  message: OutboundMessage;
  showDebtor: boolean;
  open: boolean;
  onToggle: () => void;
  onRetry?: () => void;
}) {
  const statusText = `${MessageStatusLabels[m.status] ?? m.status}${m.skipReason ? `: ${m.skipReason}` : ''}${m.status === 'QUEUED' && m.lastError ? ` (attempt ${m.attempts} failed: ${m.lastError})` : ''}${m.status === 'FAILED' && m.lastError ? `: ${m.lastError}` : ''}`;
  return (
    <>
      <tr>
        <td>{formatDateTime(m.createdAtUtc)}</td>
        {showDebtor ? <td>{m.debtorName}</td> : null}
        <td>
          {MessageKindLabels[m.kind] ?? m.kind} {m.documentNumber}
        </td>
        <td>{MessageChannelLabels[m.channel] ?? m.channel}</td>
        <td>{m.toNumber ?? '-'}</td>
        <td className={m.status === 'FAILED' ? 'sb-error' : undefined}>{statusText}</td>
        <td>
          <button type="button" className="sb-button sb-button--secondary sb-button--small" aria-expanded={open} onClick={onToggle}>
            {open ? 'Hide' : 'History'}
          </button>
          {onRetry ? (
            <button type="button" className="sb-button sb-button--small" onClick={onRetry}>
              Send again
            </button>
          ) : null}
        </td>
      </tr>
      {open ? (
        <tr>
          <td colSpan={showDebtor ? 7 : 6}>
            <p>{m.body}</p>
            {m.attachmentSha256 ? <p className="sb-muted">PDF sent, SHA-256 {m.attachmentSha256}</p> : null}
            <ul className="sb-plain-list">
              {m.events.map((e, i) => (
                <li key={i}>
                  {formatDateTime(e.atUtc)}: {MessageStatusLabels[e.status] ?? e.status}
                  {e.detail ? ` - ${e.detail}` : ''}
                </li>
              ))}
            </ul>
          </td>
        </tr>
      ) : null}
    </>
  );
}
