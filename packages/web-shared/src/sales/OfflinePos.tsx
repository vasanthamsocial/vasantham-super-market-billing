'use client';

import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api, errorMessage } from '../api';
import { unreachable } from '../offline/collectionQueue';
import { InvoiceKindLabels, type Invoice, type PosContext } from '../types';
import { counterAgent, type AgentSettings } from './counterAgent';
import { InvoiceReceipt } from './InvoiceReceipt';
import {
  offlineAgent,
  offlineInvoice,
  type OfflineBill,
  type OfflineBillResult,
  type OfflineCartLine,
  type OfflineDraft,
  type OfflineItemMatch,
  type OfflinePayment,
  type OfflineStatus,
} from './offlineBilling';

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const PackRefreshMs = 5 * 60_000;
const TickMs = 20_000;

function newId(): string {
  // A UUIDv7 (time-ordered), as the server's ids.
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  const ms = BigInt(Date.now());
  for (let i = 0; i < 6; i++) bytes[i] = Number((ms >> BigInt(8 * (5 - i))) & 0xffn);
  bytes[6] = (bytes[6]! & 0x0f) | 0x70;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * Offline billing on this counter PC (D-039). While the server answers, bills issued offline are delivered to it and the
 * counter agent's price list is kept fresh; when it stops answering, the POS switches to billing through the agent.
 */
export function useOfflineCounter(context: PosContext | null, agent: AgentSettings | null, active: boolean) {
  const capable = !!context?.offlineSeries && !!agent;
  const [status, setStatus] = useState<OfflineStatus | null>(null);
  const [serverDown, setServerDown] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [delivered, setDelivered] = useState<OfflineBillResult[]>([]);
  const busy = useRef(false);
  const packAt = useRef(0);

  const refreshStatus = useCallback(async () => {
    if (!capable || !agent) return;
    try {
      setStatus(await offlineAgent.status(agent));
      setProblem(null);
    } catch (caught) {
      setProblem(errorMessage(caught));
    }
  }, [capable, agent]);

  /** Delivers waiting bills and refreshes the price list; finds out whether the server answers. */
  const tick = useCallback(async (forcePack = false) => {
    if (!capable || !agent || !active || busy.current) return;
    busy.current = true;
    try {
      let bills: OfflineBill[];
      try {
        bills = await offlineAgent.pending(agent);
      } catch (caught) {
        setProblem(errorMessage(caught));
        return;
      }

      let results: OfflineBillResult[] | null = null;
      let pack: unknown = null;
      try {
        if (bills.length > 0) {
          results = (await api.post<{ results: OfflineBillResult[] }>('/api/v1/pos/offline/sync', { bills })).results;
        }
        if (forcePack || Date.now() - packAt.current > PackRefreshMs) {
          pack = await api.get<unknown>('/api/v1/pos/offline/pack');
        } else if (bills.length === 0) {
          await api.get('/api/v1/pos/shift'); // only to learn whether the server answers
        }
        setServerDown(false);
      } catch (caught) {
        if (unreachable(caught)) setServerDown(true);
        else setProblem(errorMessage(caught));
      }

      try {
        if (results) {
          setStatus(await offlineAgent.acknowledge(agent, results.map((r) => ({ id: r.id, status: r.status }))));
          setDelivered(results.filter((r) => r.status !== 'NOT_PROCESSED'));
        }
        if (pack) {
          setStatus(await offlineAgent.setPack(agent, pack));
          packAt.current = Date.now();
        } else {
          setStatus(await offlineAgent.status(agent));
        }
      } catch (caught) {
        setProblem(errorMessage(caught));
      }
    } finally {
      busy.current = false;
    }
  }, [capable, agent, active]);

  useEffect(() => {
    if (!capable || !active) return;
    void tick(true);
    const timer = window.setInterval(() => void tick(), TickMs);
    const online = () => void tick(true);
    window.addEventListener('online', online);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener('online', online);
    };
  }, [capable, active, tick]);

  /** The POS found the server unreachable: bill through the agent until it answers again. */
  const markDown = useCallback(() => {
    if (!capable) return;
    setServerDown(true);
    void refreshStatus();
  }, [capable, refreshStatus]);

  return { capable, status, serverDown, markDown, problem, delivered, deliver: () => tick(), refreshStatus };
}

export type OfflineCounter = ReturnType<typeof useOfflineCounter>;

interface Line extends OfflineCartLine {
  key: string;
}

/** The counter while the server cannot be reached: scan, price and issue invoices through the counter agent. */
export function OfflinePos({ context, agent, offline, cashier, initial, unconfirmed }: {
  context: PosContext;
  agent: AgentSettings;
  offline: OfflineCounter;
  cashier: string;
  initial: OfflineCartLine[];
  unconfirmed: number | null;
}) {
  const status = offline.status;
  const [lines, setLines] = useState<Line[]>(() => initial.map((l) => ({ ...l, key: newId() })));
  const [channel, setChannel] = useState('RETAIL');
  const [scan, setScan] = useState('');
  const [choices, setChoices] = useState<{ matches: OfflineItemMatch[]; quantity: number } | null>(null);
  const [mrpChoice, setMrpChoice] = useState<{ item: OfflineItemMatch; quantity: number } | null>(null);
  const [draft, setDraft] = useState<OfflineDraft | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [paying, setPaying] = useState(false);
  const [done, setDone] = useState<Invoice | null>(null);
  const scanRef = useRef<HTMLInputElement>(null);
  const run = useRef(0);

  const cartLines = lines.map(({ variantUnitId, quantity, mrp }) => ({ variantUnitId, quantity, mrp }));
  const cartKey = JSON.stringify({ channel, cartLines });

  // Priced by the agent from the price list, with the server's own rules.
  useEffect(() => {
    const parsed = JSON.parse(cartKey) as { channel: string; cartLines: OfflineCartLine[] };
    if (parsed.cartLines.length === 0) {
      setDraft(null);
      return;
    }
    const current = ++run.current;
    offlineAgent
      .price(agent, { channel: parsed.channel, lines: parsed.cartLines })
      .then((priced) => {
        if (current === run.current) {
          setDraft(priced);
          setError(null);
        }
      })
      .catch((caught: unknown) => {
        if (current === run.current) {
          setDraft(null);
          setError(errorMessage(caught));
        }
      });
  }, [agent, cartKey]);

  function add(item: OfflineItemMatch, quantity: number, mrp: number | null) {
    setLines((current) => {
      const existing = current.findIndex((l) => l.variantUnitId === item.variantUnitId && l.mrp === mrp);
      return existing >= 0
        ? current.map((l, i) => (i === existing ? { ...l, quantity: Math.round((l.quantity + quantity) * 1000) / 1000 } : l))
        : [...current, { key: newId(), variantUnitId: item.variantUnitId, quantity, mrp }];
    });
    setChoices(null);
    setMrpChoice(null);
    window.setTimeout(() => scanRef.current?.focus(), 0);
  }

  function choose(item: OfflineItemMatch, quantity: number) {
    if (item.mrps.length > 1) setMrpChoice({ item, quantity });
    else add(item, quantity, item.mrps[0] ?? null);
  }

  async function onScan(event: FormEvent) {
    event.preventDefault();
    const text = scan.trim();
    if (!text) return;
    const multiplied = /^(\d+(?:\.\d+)?)\*(.+)$/.exec(text);
    const quantity = multiplied ? Number(multiplied[1]) : 1;
    const code = multiplied ? multiplied[2]!.trim() : text;
    setScan('');
    try {
      const found = await offlineAgent.find(agent, code);
      if (found.length === 0) setError(`"${code}" is not in the offline price list.`);
      else if (found.length === 1) choose(found[0]!, quantity);
      else setChoices({ matches: found, quantity });
    } catch (caught) {
      setError(errorMessage(caught));
    }
  }

  function newBill() {
    setLines([]);
    setDraft(null);
    setDone(null);
    setPaying(false);
    setError(null);
    window.setTimeout(() => scanRef.current?.focus(), 0);
  }

  if (done) {
    return (
      <section className="sb-card" data-testid="offline-done">
        <h2>Bill {done.number} issued</h2>
        <p className="sb-notice sb-notice--success" role="status">
          {InvoiceKindLabels[done.kind] ?? done.kind} for Rs. {money.format(done.grandTotal)}
          {done.changeDue > 0 ? `. Give change Rs. ${money.format(done.changeDue)}` : ''}. It is sent to the server when it is back.
        </p>
        <InvoiceReceipt invoice={done} />
        <div className="sb-actions">
          <button type="button" className="sb-button sb-button--secondary" onClick={() => window.print()}>Print receipt</button>
          <button type="button" className="sb-button" autoFocus onClick={newBill}>New bill</button>
        </div>
      </section>
    );
  }

  return (
    <div className="sb-pos" data-testid="offline-pos">
      <p className="sb-notice sb-notice--warning" role="status" data-testid="offline-pos-banner">
        The store server cannot be reached: billing on this PC. Bills are numbered {status?.series ?? context.offlineSeries}-... (next{' '}
        {status?.nextNumber ?? '?'}), are real invoices, and are sent to the server as soon as it answers.
        {status ? ` ${status.pending} waiting (Rs. ${money.format(status.pendingAmount)}).` : ''}
        {' '}Cash, card or UPI only: no discounts, price changes, credit or returns.
      </p>
      {unconfirmed !== null ? (
        <p className="sb-error" role="alert" data-testid="offline-unconfirmed">
          The bill being paid when the server stopped answering (Rs. {money.format(unconfirmed)}) may already have been issued. Do not bill the same
          goods again unless you are sure; check it when the server is back.
        </p>
      ) : null}
      {offline.problem ? <p className="sb-error" role="alert">{offline.problem}</p> : null}
      {status && !status.ready ? (
        <p className="sb-error" role="alert" data-testid="offline-refused">{status.refusal}</p>
      ) : (
        <div className="sb-pos__body">
          <section className="sb-pos__items" aria-label="Bill items">
            <form onSubmit={onScan} className="sb-pos__scan">
              <input
                ref={scanRef}
                autoFocus
                className="sb-input sb-pos__scan-input"
                aria-label="Scan or type an item (offline)"
                placeholder="Scan a barcode or type a name and press Enter (3*code for three)"
                value={scan}
                onChange={(e) => setScan(e.target.value)}
                disabled={paying}
              />
            </form>
            {choices ? (
              <ul className="sb-plain-list" aria-label="Matching items">
                {choices.matches.map((m) => (
                  <li key={m.variantUnitId}>
                    <button type="button" className="sb-link" onClick={() => choose(m, choices.quantity)}>{m.name} ({m.unitCode})</button>
                  </li>
                ))}
              </ul>
            ) : null}
            {mrpChoice ? (
              <div className="sb-actions" aria-label="Which MRP is on the pack">
                {mrpChoice.item.mrps.map((m) => (
                  <button key={m} type="button" className="sb-button sb-button--secondary" onClick={() => add(mrpChoice.item, mrpChoice.quantity, m)}>
                    MRP Rs. {money.format(m)}
                  </button>
                ))}
              </div>
            ) : null}
            {error ? <p className="sb-error" role="alert" data-testid="offline-error">{error}</p> : null}
            <table className="sb-table sb-pos__lines" data-testid="offline-lines">
              <thead>
                <tr>
                  <th>#</th>
                  <th>Item</th>
                  <th className="sb-num">Qty</th>
                  <th className="sb-num">Price</th>
                  <th className="sb-num">Amount</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {lines.map((l, i) => {
                  const priced = draft?.lines[i];
                  return (
                    <tr key={l.key}>
                      <td>{i + 1}</td>
                      <td>{priced?.description ?? '...'}{priced?.mrp ? <span className="sb-muted"> MRP {money.format(priced.mrp)}</span> : null}</td>
                      <td className="sb-num">
                        <input
                          className="sb-input sb-input--inline"
                          aria-label={`Quantity of line ${i + 1}`}
                          inputMode="decimal"
                          size={6}
                          defaultValue={l.quantity}
                          disabled={paying}
                          onBlur={(e) => {
                            const value = Number(e.target.value);
                            if (Number.isFinite(value) && value > 0) setLines((c) => c.map((x) => (x.key === l.key ? { ...x, quantity: value } : x)));
                          }}
                        />{' '}
                        {priced?.unitCode ?? ''}
                      </td>
                      <td className="sb-num">{priced ? money.format(priced.unitPrice) : ''}</td>
                      <td className="sb-num">{priced ? money.format(priced.amounts.total) : ''}</td>
                      <td>
                        <button type="button" className="sb-link" disabled={paying} onClick={() => setLines((c) => c.filter((x) => x.key !== l.key))}>Remove</button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </section>
          <aside className="sb-pos__totals" aria-label="Bill total">
            <div className="sb-pos__grand" data-testid="offline-total">
              <span>Total</span>
              <strong>{draft ? `Rs. ${money.format(draft.result.grandTotal)}` : 'Rs. 0.00'}</strong>
            </div>
            <label className="sb-field">
              <span className="sb-field__label">Billing type</span>
              <select className="sb-input" value={channel} disabled={paying} onChange={(e) => setChannel(e.target.value)}>
                <option value="RETAIL">Retail</option>
                <option value="WHOLESALE">Wholesale</option>
              </select>
            </label>
            {draft ? (
              <dl className="sb-pos__summary">
                <dt>Document</dt>
                <dd>{InvoiceKindLabels[draft.kind] ?? draft.kind}</dd>
                {draft.result.cgst + draft.result.igst > 0 ? (<><dt>Tax</dt><dd>{money.format(draft.result.cgst + draft.result.sgst + draft.result.igst + draft.result.cess)}</dd></>) : null}
                {draft.result.roundOff !== 0 ? (<><dt>Round off</dt><dd>{money.format(draft.result.roundOff)}</dd></>) : null}
              </dl>
            ) : null}
            {paying && draft ? (
              <OfflinePayment
                total={draft.result.grandTotal}
                cancel={() => setPaying(false)}
                issue={async (payments, id) => {
                  const bill = await offlineAgent.issue(agent, { id, cart: { channel, lines: cartLines }, payments, expectedGrandTotal: draft.result.grandTotal });
                  const invoice = offlineInvoice(bill, context, cashier);
                  setDone(invoice);
                  void offline.refreshStatus();
                  if (agent.autoPrint) {
                    counterAgent.printReceipt(agent, invoice, payments.some((p) => p.method === 'CASH')).catch(() => undefined);
                  }
                }}
              />
            ) : (
              <button type="button" className="sb-button sb-pos__pay" disabled={!draft} onClick={() => setPaying(true)}>Pay</button>
            )}
          </aside>
        </div>
      )}
      {offline.delivered.length > 0 ? <DeliveredNotice results={offline.delivered} /> : null}
    </div>
  );
}

/** Cash, card and UPI with references; the same id for the same attempt, so a retry never issues twice. */
function OfflinePayment({ total, cancel, issue }: {
  total: number;
  cancel: () => void;
  issue: (payments: OfflinePayment[], id: string) => Promise<void>;
}) {
  const [cash, setCash] = useState(total.toFixed(2));
  const [card, setCard] = useState('');
  const [cardRef, setCardRef] = useState('');
  const [upi, setUpi] = useState('');
  const [upiRef, setUpiRef] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const attempt = useRef<{ body: string; id: string } | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    const payments: OfflinePayment[] = [];
    for (const [method, amount, reference] of [['CASH', cash, null], ['CARD', card, cardRef], ['UPI', upi, upiRef]] as const) {
      const value = Number(amount.trim() || '0');
      if (!Number.isFinite(value) || value < 0) {
        setError('Enter amounts in rupees and paise.');
        return;
      }
      if (value > 0) payments.push({ method, amount: Math.round(value * 100) / 100, reference: reference?.trim() || null });
    }
    const body = JSON.stringify(payments);
    if (attempt.current?.body !== body) attempt.current = { body, id: newId() };
    setBusy(true);
    setError(null);
    try {
      await issue(payments, attempt.current.id);
    } catch (caught) {
      setError(errorMessage(caught));
    } finally {
      setBusy(false);
    }
  }

  const paid = [cash, card, upi].reduce((sum, v) => sum + (Number(v.trim() || '0') || 0), 0);
  return (
    <form className="sb-form" onSubmit={submit} data-testid="offline-payment">
      <label className="sb-field"><span className="sb-field__label">Cash (Rs.)</span><input className="sb-input" inputMode="decimal" value={cash} onChange={(e) => setCash(e.target.value)} /></label>
      <label className="sb-field"><span className="sb-field__label">Card (Rs.)</span><input className="sb-input" inputMode="decimal" value={card} onChange={(e) => setCard(e.target.value)} /></label>
      {card.trim() ? <label className="sb-field"><span className="sb-field__label">Card reference</span><input className="sb-input" maxLength={60} value={cardRef} onChange={(e) => setCardRef(e.target.value)} /></label> : null}
      <label className="sb-field"><span className="sb-field__label">UPI (Rs.)</span><input className="sb-input" inputMode="decimal" value={upi} onChange={(e) => setUpi(e.target.value)} /></label>
      {upi.trim() ? <label className="sb-field"><span className="sb-field__label">UPI reference</span><input className="sb-input" maxLength={60} value={upiRef} onChange={(e) => setUpiRef(e.target.value)} /></label> : null}
      <p>Paid Rs. {money.format(paid)}{paid > total ? `, change Rs. ${money.format(paid - total)}` : ''}</p>
      {error ? <p className="sb-error" role="alert">{error}</p> : null}
      <div className="sb-actions">
        <button className="sb-button" type="submit" disabled={busy}>{busy ? 'Issuing...' : `Issue bill of Rs. ${money.format(total)}`}</button>
        <button className="sb-button sb-button--secondary" type="button" disabled={busy} onClick={cancel}>Back</button>
      </div>
    </form>
  );
}

/** What became of the bills delivered to the server (shown on both screens). */
export function DeliveredNotice({ results }: { results: OfflineBillResult[] }) {
  const posted = results.filter((r) => r.status === 'POSTED' || r.status === 'DUPLICATE').length;
  const waiting = results.filter((r) => r.status === 'QUARANTINED').length;
  const flagged = results.filter((r) => r.review).length;
  return (
    <p className={waiting > 0 ? 'sb-notice sb-notice--warning' : 'sb-notice sb-notice--success'} role="status" data-testid="offline-delivered">
      {posted} offline bill{posted === 1 ? '' : 's'} reached the server
      {waiting > 0 ? `; ${waiting} could not be posted and wait for a manager` : ''}
      {flagged > 0 ? `; ${flagged} to be checked by a manager` : ''}.
    </p>
  );
}
