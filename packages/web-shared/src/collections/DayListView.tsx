'use client';

import type { ReactNode } from 'react';
import { VisitReasonLabels, type DayList, type DayParty } from '../types';
import { moneyFormat } from '../stock/StockPanel';

function time(value: string | null): string {
  return value ? value.slice(0, 5) : '';
}

/**
 * A collector's day: each party with why they are on the list, what is due and overdue, the oldest unpaid bill, the
 * last collection and any promise. Phone-first: one card per party.
 */
export function DayListView({ day, action }: { day: DayList; action?: (party: DayParty) => ReactNode }) {
  return (
    <section aria-label={`Visits on ${day.date}`} data-testid="day-list">
      <p className="sb-muted" data-testid="day-summary">
        {day.parties.length} part{day.parties.length === 1 ? 'y' : 'ies'} - due Rs. {moneyFormat.format(day.dueTotal)} (overdue Rs. {moneyFormat.format(day.overdueTotal)})
        {day.collectedTotal > 0 ? `, collected Rs. ${moneyFormat.format(day.collectedTotal)}` : ''}
      </p>
      {day.absent ? <p className="sb-notice sb-notice--warning" role="status">{day.collector} is marked away on this day; their parties go to the backup collectors.</p> : null}
      {day.parties.length === 0 && !day.absent ? <p className="sb-muted">No visits on this day.</p> : null}
      <ol className="sb-plain-list sb-day">
        {day.parties.map((p) => (
          <li key={p.debtorId} className={`sb-card sb-day__party${p.status === 'COLLECTED' ? ' sb-day__party--done' : ''}`} data-testid={`day-party-${p.code}`}>
            <header className="sb-day__head">
              <strong>
                {p.routeCode ? `${p.routeCode}${p.visitSequence ? `-${p.visitSequence}` : ''} ` : ''}
                {p.name}
              </strong>
              <span className="sb-chip">{p.status === 'COLLECTED' ? `Collected Rs. ${moneyFormat.format(p.collectedToday)}` : 'To visit'}</span>
            </header>
            <p className="sb-muted">
              {p.reasons.map((r) => VisitReasonLabels[r] ?? r).join(', ')}
              {p.preferredFrom ? ` - best ${time(p.preferredFrom)}${p.preferredTo ? `-${time(p.preferredTo)}` : ''}` : ''}
            </p>
            {p.address ? <p>{p.address}</p> : null}
            {p.phone || p.whatsAppNumber ? (
              <p>
                {p.phone ? <a href={`tel:${p.phone}`}>{p.phone}</a> : null}
                {p.whatsAppNumber && p.whatsAppNumber !== p.phone ? <> / <a href={`tel:${p.whatsAppNumber}`}>{p.whatsAppNumber}</a></> : null}
              </p>
            ) : null}
            <dl className="sb-day__amounts">
              <dt>Owes</dt>
              <dd>{moneyFormat.format(p.totalBalance)}</dd>
              <dt>Due now</dt>
              <dd>{moneyFormat.format(p.dueBalance)}</dd>
              <dt>Overdue</dt>
              <dd className={p.overdueBalance > 0 ? 'sb-error' : undefined}>{moneyFormat.format(p.overdueBalance)}</dd>
              <dt>Not yet due</dt>
              <dd>{moneyFormat.format(p.notYetDueBalance)}</dd>
            </dl>
            {p.oldestUnpaidDocument ? (
              <p className="sb-muted">
                Oldest unpaid: {p.oldestUnpaidDocument} (due {p.oldestUnpaidDueDate}{p.daysOverdue > 0 ? `, ${p.daysOverdue} days overdue` : ''})
              </p>
            ) : null}
            {p.lastCollectionDate ? <p className="sb-muted">Last paid Rs. {moneyFormat.format(p.lastCollectionAmount ?? 0)} on {p.lastCollectionDate}</p> : null}
            {p.promisedAmount ? <p>Promised Rs. {moneyFormat.format(p.promisedAmount)} by {p.promisedDate}</p> : null}
            {p.notes.map((n, i) => (
              <p key={i} className="sb-notice">{n}</p>
            ))}
            {action ? action(p) : null}
          </li>
        ))}
      </ol>
    </section>
  );
}
