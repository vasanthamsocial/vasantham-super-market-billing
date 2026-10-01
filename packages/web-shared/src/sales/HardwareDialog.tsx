'use client';

import { useState } from 'react';
import { errorMessage } from '../api';
import { counterAgent, defaultAgentUrl, saveAgentSettings, type AgentSettings, type AgentStatus } from './counterAgent';

/** This counter PC's hardware: where the counter agent is, its pairing token, and whether receipts print by themselves. */
export function HardwareDialog({ current, onSaved, onClose }: { current: AgentSettings | null; onSaved: (s: AgentSettings | null) => void; onClose: () => void }) {
  const [url, setUrl] = useState(current?.url ?? defaultAgentUrl);
  const [token, setToken] = useState(current?.token ?? '');
  const [autoPrint, setAutoPrint] = useState(current?.autoPrint ?? true);
  const [status, setStatus] = useState<AgentStatus | null>(null);
  const [error, setError] = useState<unknown>(null);
  const settings: AgentSettings = { url: url.trim(), token: token.trim(), autoPrint };

  return (
    <div className="sb-modal" role="dialog" aria-modal="true" aria-label="Counter hardware" data-testid="pos-hardware">
      <div className="sb-modal__box">
        <header className="sb-card__header">
          <h2>Counter hardware</h2>
          <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={onClose}>Close (Esc)</button>
        </header>
        <p className="sb-muted">
          Receipt printer, cash drawer, scale and customer display are driven by the counter agent installed on this PC. Its pairing
          token is in counter-agent.json on this PC.
        </p>
        <div className="sb-field">
          <label className="sb-field__label" htmlFor="agent-url">Agent address</label>
          <input id="agent-url" className="sb-input" value={url} onChange={(e) => setUrl(e.target.value)} />
        </div>
        <div className="sb-field">
          <label className="sb-field__label" htmlFor="agent-token">Pairing token</label>
          <input id="agent-token" className="sb-input" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" autoFocus />
        </div>
        <label className="sb-check">
          <input type="checkbox" checked={autoPrint} onChange={(e) => setAutoPrint(e.target.checked)} /> Print the receipt (and open the drawer for cash) as soon as a bill is done
        </label>
        {status ? (
          <p className="sb-notice sb-notice--success" role="status" data-testid="agent-status">
            Agent {status.version ?? ''} answered. Printer: {status.printer}; drawer: {status.drawer ? 'yes' : 'no'}; scale: {status.scale}; display: {status.display}.
          </p>
        ) : null}
        {error ? <p className="sb-error" role="alert">{errorMessage(error)}</p> : null}
        <div className="sb-actions">
          <button
            type="button"
            className="sb-button sb-button--secondary"
            onClick={() => {
              setError(null);
              setStatus(null);
              counterAgent.status(settings).then(setStatus).catch(setError);
            }}
          >
            Test connection
          </button>
          <button
            type="button"
            className="sb-button"
            onClick={() => {
              const saved = settings.token ? settings : null;
              saveAgentSettings(saved);
              onSaved(saved);
            }}
          >
            Save
          </button>
        </div>
      </div>
    </div>
  );
}
