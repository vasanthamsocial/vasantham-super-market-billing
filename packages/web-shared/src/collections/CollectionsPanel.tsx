'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { useApiData } from '../admin/useApiData';
import {
  CollectionPermission,
  ScheduleTypeLabels,
  WeekdayNames,
  type AbsenceInfo,
  type CollectionPlan,
  type Collector,
  type Debtor,
  type DayList,
  type RouteInfo,
  type VisitInfo,
} from '../types';
import { ActionForm, ErrorText, Field, Notice, optional, text } from '../ui';
import { DayListView } from './DayListView';

function today(): string {
  return new Date().toLocaleDateString('en-CA');
}

function titleCase(day: string): string {
  return day[0] + day.slice(1, 3).toLowerCase();
}

/** Collection set-up for managers: each collector's day, routes, debtors' plans, assigned visits and absences. */
export function CollectionsPanel() {
  const { membership, hasPermission } = useAuth();
  const business = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const canManage = hasPermission(CollectionPermission.Manage);
  const collectors = useApiData<Collector[]>(business ? `${business}/collectors` : null);
  const routes = useApiData<RouteInfo[]>(business ? `${business}/routes` : null);
  const [version, setVersion] = useState(0);
  const refresh = () => setVersion((v) => v + 1);

  return (
    <>
      <DayPreview business={business} collectors={collectors.data ?? []} version={version} />
      <RoutesCard business={business} routes={routes.data ?? []} canManage={canManage} reload={async () => { await routes.reload(); refresh(); }} />
      <PlansCard business={business} routes={routes.data ?? []} collectors={collectors.data ?? []} canManage={canManage} version={version} changed={refresh} />
      <VisitsCard business={business} collectors={collectors.data ?? []} canManage={canManage} changed={refresh} />
      <AbsencesCard business={business} collectors={collectors.data ?? []} canManage={canManage} changed={refresh} />
      <ErrorText error={collectors.error ?? routes.error} />
    </>
  );
}

function CollectorSelect({ collectors, value, onChange, label = 'Collector', name, allowNone }: {
  collectors: Collector[];
  value?: string;
  onChange?: (id: string) => void;
  label?: string;
  name?: string;
  allowNone?: string;
}) {
  return (
    <label className="sb-field">
      <span className="sb-field__label">{label}</span>
      <select className="sb-input" name={name} {...(onChange ? { value: value ?? '', onChange: (e) => onChange(e.target.value) } : { defaultValue: value ?? '' })}>
        {allowNone !== undefined ? <option value="">{allowNone}</option> : null}
        {collectors.map((c) => (
          <option key={c.userId} value={c.userId}>{c.displayName}</option>
        ))}
      </select>
    </label>
  );
}

function DayPreview({ business, collectors, version }: { business: string | null; collectors: Collector[]; version: number }) {
  const [collector, setCollector] = useState('');
  const [date, setDate] = useState(today);
  const [overdue, setOverdue] = useState(false);
  const chosen = collector || collectors[0]?.userId || '';
  const day = useApiData<DayList>(
    business && chosen ? `${business}/collections/day?collectorUserId=${chosen}&date=${date}&includeOverdue=${overdue}&r=${version}` : null,
  );
  return (
    <section className="sb-card" aria-labelledby="day-heading">
      <header className="sb-card__header">
        <h2 id="day-heading">Collector&apos;s day</h2>
      </header>
      {collectors.length === 0 ? <p className="sb-muted">No one can collect yet: give someone the collection person role.</p> : null}
      <div className="sb-inline-form">
        <CollectorSelect collectors={collectors} value={chosen} onChange={setCollector} />
        <label className="sb-field">
          <span className="sb-field__label">Date</span>
          <input className="sb-input" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
        </label>
        <label className="sb-check">
          <input type="checkbox" checked={overdue} onChange={(e) => setOverdue(e.target.checked)} /> Also every party with something overdue
        </label>
      </div>
      <ErrorText error={day.error} />
      {day.data ? <DayListView day={day.data} /> : null}
    </section>
  );
}

function RoutesCard({ business, routes, canManage, reload }: { business: string | null; routes: RouteInfo[]; canManage: boolean; reload: () => Promise<void> }) {
  const [error, setError] = useState<unknown>(null);
  return (
    <section className="sb-card" aria-labelledby="routes-heading">
      <header className="sb-card__header">
        <h2 id="routes-heading">Routes</h2>
      </header>
      <ErrorText error={error} />
      <table className="sb-table" data-testid="routes-table">
        <thead>
          <tr>
            <th>Code</th>
            <th>Name</th>
            <th className="sb-num">Parties</th>
            <th>Status</th>
            {canManage ? <th /> : null}
          </tr>
        </thead>
        <tbody>
          {routes.map((r) => (
            <tr key={r.id}>
              <td>{r.code}</td>
              <td>{r.name}{r.description ? <span className="sb-muted"> - {r.description}</span> : null}</td>
              <td className="sb-num">{r.parties}</td>
              <td>{r.isActive ? 'In use' : 'Not in use'}</td>
              {canManage ? (
                <td>
                  <button
                    type="button"
                    className="sb-button sb-button--secondary sb-button--small"
                    onClick={async () => {
                      setError(null);
                      try {
                        await api.put(`${business}/routes/${r.id}`, { name: r.name, description: r.description, isActive: !r.isActive, rowVersion: r.rowVersion });
                        await reload();
                      } catch (caught) {
                        setError(caught);
                      }
                    }}
                  >
                    {r.isActive ? 'Stop using' : 'Use again'}
                  </button>
                </td>
              ) : null}
            </tr>
          ))}
        </tbody>
      </table>
      {canManage ? (
        <ActionForm
          submitLabel="Add route"
          testId="add-route-form"
          onSubmit={async (data) => {
            await api.post(`${business}/routes`, { code: text(data, 'code'), name: text(data, 'name'), description: optional(data, 'description') });
            await reload();
          }}
        >
          <div className="sb-form-row">
            <Field label="Route code" name="code" required />
            <Field label="Route name" name="name" required />
            <Field label="Description (optional)" name="description" />
          </div>
        </ActionForm>
      ) : null}
    </section>
  );
}

/** Finds a debtor by name, code or phone. */
function DebtorPicker({ business, onPick, label = 'Find a debtor' }: { business: string | null; onPick: (debtor: Debtor) => void; label?: string }) {
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const found = useApiData<Debtor[]>(business && query ? `${business}/debtors?search=${encodeURIComponent(query)}` : null);
  return (
    <div>
      <form
        className="sb-inline-form"
        role="search"
        onSubmit={(e) => {
          e.preventDefault();
          setQuery(search.trim());
        }}
      >
        <input className="sb-input" aria-label={label} placeholder="Name, code or phone" value={search} onChange={(e) => setSearch(e.target.value)} />
        <button className="sb-button sb-button--secondary" type="submit">Find</button>
      </form>
      <ErrorText error={found.error} />
      {query && found.data ? (
        <ul className="sb-plain-list">
          {found.data.length === 0 ? <li className="sb-muted">No matching debtors.</li> : null}
          {found.data.slice(0, 10).map((d) => (
            <li key={d.id}>
              <button type="button" className="sb-link" onClick={() => { onPick(d); setQuery(''); setSearch(''); }}>
                {d.code} - {d.displayName}
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

function PlansCard({ business, routes, collectors, canManage, version, changed }: {
  business: string | null;
  routes: RouteInfo[];
  collectors: Collector[];
  canManage: boolean;
  version: number;
  changed: () => void;
}) {
  const [routeFilter, setRouteFilter] = useState('');
  const plans = useApiData<CollectionPlan[]>(business ? `${business}/collection-plans?${routeFilter ? `routeId=${routeFilter}&` : ''}r=${version}` : null);
  const [editing, setEditing] = useState<CollectionPlan | null>(null);
  const [error, setError] = useState<unknown>(null);

  async function open(debtorId: string) {
    setError(null);
    try {
      setEditing(await api.get<CollectionPlan>(`${business}/debtors/${debtorId}/collection-plan`));
    } catch (caught) {
      setError(caught);
    }
  }

  return (
    <section className="sb-card" aria-labelledby="plans-heading">
      <header className="sb-card__header">
        <h2 id="plans-heading">Collection plans</h2>
      </header>
      <div className="sb-inline-form">
        <label className="sb-field">
          <span className="sb-field__label">Route</span>
          <select className="sb-input" value={routeFilter} onChange={(e) => setRouteFilter(e.target.value)}>
            <option value="">All routes</option>
            {routes.map((r) => (
              <option key={r.id} value={r.id}>{r.code} - {r.name}</option>
            ))}
          </select>
        </label>
      </div>
      <ErrorText error={plans.error ?? error} />
      <table className="sb-table" data-testid="plans-table">
        <thead>
          <tr>
            <th>Route</th>
            <th>Debtor</th>
            <th>Collector (backup)</th>
            <th>Visits</th>
            {canManage ? <th /> : null}
          </tr>
        </thead>
        <tbody>
          {(plans.data ?? []).map((p) => (
            <tr key={p.debtorId}>
              <td>{p.routeCode ? `${p.routeCode}${p.visitSequence ? ` #${p.visitSequence}` : ''}` : '-'}</td>
              <td>{p.debtorCode} - {p.debtorName}</td>
              <td>{p.primaryCollector ?? '-'}{p.backupCollector ? ` (${p.backupCollector})` : ''}</td>
              <td>{p.description}</td>
              {canManage ? (
                <td>
                  <button type="button" className="sb-button sb-button--secondary sb-button--small" onClick={() => void open(p.debtorId)}>Edit</button>
                </td>
              ) : null}
            </tr>
          ))}
        </tbody>
      </table>
      {plans.data && plans.data.length === 0 ? <p className="sb-muted">No debtors are planned for collection yet.</p> : null}
      {canManage ? (
        <>
          <h3>Plan a debtor</h3>
          <DebtorPicker business={business} onPick={(d) => void open(d.id)} label="Find a debtor to plan" />
        </>
      ) : null}
      {editing ? (
        <PlanForm
          key={editing.debtorId}
          business={business}
          plan={editing}
          routes={routes.filter((r) => r.isActive || r.id === editing.routeId)}
          collectors={collectors}
          saved={() => {
            setEditing(null);
            void plans.reload();
            changed();
          }}
        />
      ) : null}
    </section>
  );
}

function PlanForm({ business, plan, routes, collectors, saved }: {
  business: string | null;
  plan: CollectionPlan;
  routes: RouteInfo[];
  collectors: Collector[];
  saved: () => void;
}) {
  const [type, setType] = useState(plan.scheduleType);
  return (
    <ActionForm
      submitLabel="Save plan"
      testId="plan-form"
      onSubmit={async (data) => {
        const number = (name: string) => (text(data, name) === '' ? null : Number(text(data, name)));
        await api.put(`${business}/debtors/${plan.debtorId}/collection-plan`, {
          routeId: optional(data, 'routeId'),
          visitSequence: number('visitSequence'),
          primaryCollectorUserId: optional(data, 'primary'),
          backupCollectorUserId: optional(data, 'backup'),
          preferredFrom: optional(data, 'from'),
          preferredTo: optional(data, 'to'),
          scheduleType: type,
          weekdays: WeekdayNames.filter((d) => data.get(`day-${d}`) === 'on'),
          anchorDate: optional(data, 'anchorDate'),
          monthDay: number('monthDay'),
          dueOffsetDays: number('dueOffsetDays'),
        });
        saved();
      }}
    >
      <h3>Plan for {plan.debtorCode} - {plan.debtorName}</h3>
      <div className="sb-form-row">
        <label className="sb-field">
          <span className="sb-field__label">Route</span>
          <select className="sb-input" name="routeId" defaultValue={plan.routeId ?? ''}>
            <option value="">No route</option>
            {routes.map((r) => (
              <option key={r.id} value={r.id}>{r.code} - {r.name}</option>
            ))}
          </select>
        </label>
        <Field label="Visit sequence" name="visitSequence" inputMode="numeric" defaultValue={plan.visitSequence?.toString() ?? ''} />
        <CollectorSelect collectors={collectors} name="primary" value={plan.primaryCollectorUserId ?? ''} label="Collector" allowNone="Nobody" />
        <CollectorSelect collectors={collectors} name="backup" value={plan.backupCollectorUserId ?? ''} label="Backup collector" allowNone="Nobody" />
      </div>
      <div className="sb-form-row">
        <Field label="Best from" name="from" type="time" defaultValue={plan.preferredFrom?.slice(0, 5) ?? ''} />
        <Field label="Best until" name="to" type="time" defaultValue={plan.preferredTo?.slice(0, 5) ?? ''} />
        <label className="sb-field">
          <span className="sb-field__label">Visit</span>
          <select className="sb-input" value={type} onChange={(e) => setType(e.target.value)}>
            {Object.entries(ScheduleTypeLabels).map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
        </label>
      </div>
      {type === 'WEEKDAYS' || type === 'FORTNIGHTLY' ? (
        <fieldset className="sb-inline-form" aria-label="Weekdays">
          {WeekdayNames.map((d) => (
            <label key={d} className="sb-check">
              <input type="checkbox" name={`day-${d}`} defaultChecked={plan.weekdays.includes(d)} /> {titleCase(d)}
            </label>
          ))}
        </fieldset>
      ) : null}
      {type === 'FORTNIGHTLY' || type === 'SPECIFIC_DATE' ? (
        <Field label={type === 'FORTNIGHTLY' ? 'A date in a visiting week' : 'Visit date'} name="anchorDate" type="date" defaultValue={plan.anchorDate ?? ''} />
      ) : null}
      {type === 'MONTHLY' ? <Field label="Day of the month" name="monthDay" inputMode="numeric" defaultValue={plan.monthDay?.toString() ?? ''} /> : null}
      {type === 'DUE_DATE' ? (
        <Field label="Days after the due date (negative: before)" name="dueOffsetDays" inputMode="numeric" defaultValue={plan.dueOffsetDays?.toString() ?? '0'} />
      ) : null}
    </ActionForm>
  );
}

function VisitsCard({ business, collectors, canManage, changed }: { business: string | null; collectors: Collector[]; canManage: boolean; changed: () => void }) {
  const [date, setDate] = useState(today);
  const [version, setVersion] = useState(0);
  const visits = useApiData<VisitInfo[]>(business ? `${business}/collection-visits?date=${date}&r=${version}` : null);
  const [debtor, setDebtor] = useState<Debtor | null>(null);
  const [error, setError] = useState<unknown>(null);
  const done = () => {
    setVersion((v) => v + 1);
    changed();
  };

  return (
    <section className="sb-card" aria-labelledby="visits-heading">
      <header className="sb-card__header">
        <h2 id="visits-heading">Assigned visits</h2>
      </header>
      <label className="sb-field">
        <span className="sb-field__label">Date</span>
        <input className="sb-input" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
      </label>
      <ErrorText error={visits.error ?? error} />
      <ul className="sb-plain-list" data-testid="visits-list">
        {(visits.data ?? []).map((v) => (
          <li key={v.id}>
            {v.collector}: {v.debtorName}{v.note ? ` - ${v.note}` : ''} {v.isCancelled ? <span className="sb-muted">(cancelled)</span> : null}
            {canManage && !v.isCancelled ? (
              <button
                type="button"
                className="sb-button sb-button--secondary sb-button--small"
                onClick={async () => {
                  setError(null);
                  try {
                    await api.post(`${business}/collection-visits/${v.id}/cancel`);
                    done();
                  } catch (caught) {
                    setError(caught);
                  }
                }}
              >
                Cancel
              </button>
            ) : null}
          </li>
        ))}
      </ul>
      {visits.data && visits.data.length === 0 ? <p className="sb-muted">No visits assigned for this day.</p> : null}
      {canManage ? (
        <>
          <h3>Assign a visit</h3>
          {debtor ? (
            <ActionForm
              key={debtor.id}
              submitLabel="Assign visit"
              testId="assign-visit-form"
              onSubmit={async (data) => {
                await api.post(`${business}/collection-visits`, {
                  debtorId: debtor.id,
                  collectorUserId: text(data, 'collector'),
                  visitDate: text(data, 'visitDate'),
                  note: optional(data, 'note'),
                });
                setDebtor(null);
                done();
              }}
            >
              <p>{debtor.code} - {debtor.displayName}</p>
              <div className="sb-form-row">
                <CollectorSelect collectors={collectors} name="collector" />
                <Field label="Visit date" name="visitDate" type="date" defaultValue={date} />
                <Field label="Note for the collector" name="note" />
              </div>
            </ActionForm>
          ) : (
            <DebtorPicker business={business} onPick={setDebtor} label="Find a debtor to visit" />
          )}
        </>
      ) : null}
    </section>
  );
}

function AbsencesCard({ business, collectors, canManage, changed }: { business: string | null; collectors: Collector[]; canManage: boolean; changed: () => void }) {
  const [version, setVersion] = useState(0);
  const absences = useApiData<AbsenceInfo[]>(business ? `${business}/collector-absences?since=${today()}&r=${version}` : null);
  const [notice, setNotice] = useState<string | null>(null);
  const done = () => {
    setVersion((v) => v + 1);
    changed();
  };
  return (
    <section className="sb-card" aria-labelledby="absences-heading">
      <header className="sb-card__header">
        <h2 id="absences-heading">Collectors away</h2>
      </header>
      <ErrorText error={absences.error} />
      {notice ? <Notice>{notice}</Notice> : null}
      <ul className="sb-plain-list">
        {(absences.data ?? []).map((a) => (
          <li key={a.id}>
            {a.absentOn}: {a.collector}{a.reason ? ` (${a.reason})` : ''}{' '}
            {canManage ? (
              <button
                type="button"
                className="sb-button sb-button--secondary sb-button--small"
                onClick={async () => {
                  await api.del(`${business}/collector-absences/${a.id}`);
                  setNotice(`${a.collector} is back on ${a.absentOn}.`);
                  done();
                }}
              >
                Remove
              </button>
            ) : null}
          </li>
        ))}
      </ul>
      {absences.data && absences.data.length === 0 ? <p className="sb-muted">Nobody is marked away.</p> : null}
      {canManage ? (
        <ActionForm
          submitLabel="Mark away"
          onSubmit={async (data) => {
            await api.post(`${business}/collector-absences`, { collectorUserId: text(data, 'collector'), absentOn: text(data, 'absentOn'), reason: optional(data, 'reason') });
            setNotice('Their parties go to the backup collectors that day.');
            done();
          }}
        >
          <div className="sb-form-row">
            <CollectorSelect collectors={collectors} name="collector" />
            <Field label="Away on" name="absentOn" type="date" defaultValue={today()} />
            <Field label="Reason (optional)" name="reason" />
          </div>
        </ActionForm>
      ) : null}
    </section>
  );
}
