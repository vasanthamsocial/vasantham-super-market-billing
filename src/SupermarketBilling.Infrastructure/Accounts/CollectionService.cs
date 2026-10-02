using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Accounts;

/// <summary>
/// Collection set-up and the collector's day (spec sections 14-15): routes, each debtor's collection plan, visits a
/// manager assigns, promises to pay, absences (a backup collector covers), and the list of parties to visit on a day
/// with their balances.
/// </summary>
public sealed class CollectionService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    // Routes

    public async Task<IReadOnlyList<RouteDto>> RoutesAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsView, businessId, cancellationToken).ConfigureAwait(false);
        var parties = await db.CollectionPlans.AsNoTracking().Where(p => p.BusinessId == businessId && p.RouteId != null).GroupBy(p => p.RouteId)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var routes = await db.Routes.AsNoTracking().Where(r => r.BusinessId == businessId).OrderBy(r => r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        return routes.Select(r => new RouteDto(r.Id, r.Code, r.Name, r.Description, r.IsActive, parties.FirstOrDefault(p => p.Key == r.Id)?.Count ?? 0, r.RowVersion)).ToList();
    }

    public async Task<RouteDto> CreateRouteAsync(Guid businessId, CreateRouteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var route = Valid(() => Route.Create(businessId, request.Code, request.Name, request.Description, clock.GetUtcNow()));
        if (await db.Routes.AnyAsync(r => r.BusinessId == businessId && r.Code == route.Code, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("route.code_taken", $"Route code {route.Code} is already used.");
        }

        db.Routes.Add(route);
        audit.Record("route.created", "route", route.Id, businessId, details: new { route.Code, route.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new RouteDto(route.Id, route.Code, route.Name, route.Description, route.IsActive, 0, route.RowVersion);
    }

    public async Task<RouteDto> UpdateRouteAsync(Guid businessId, Guid routeId, UpdateRouteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == routeId && r.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Route");
        db.Entry(route).Property(r => r.RowVersion).OriginalValue = request.RowVersion;
        Valid(() => { route.Update(request.Name, request.Description, request.IsActive); return route; });
        audit.Record("route.updated", "route", route.Id, businessId, details: new { route.Code, route.Name, route.IsActive });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await RoutesAsync(businessId, cancellationToken).ConfigureAwait(false)).First(r => r.Id == routeId);
    }

    /// <summary>People who can collect: an active role granting <c>collections.collect</c> anywhere in the business.</summary>
    public async Task<IReadOnlyList<CollectorDto>> CollectorsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsView, businessId, cancellationToken).ConfigureAwait(false);
        return await CollectorsCoreAsync(businessId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<CollectorDto>> CollectorsCoreAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var codes = Roles.All.Where(r => r.Permissions.Contains(Permissions.CollectionsCollect)).Select(r => r.Code).ToList();
        var users = await (from a in db.RoleAssignments.AsNoTracking()
                           join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                           where a.BusinessId == businessId && a.RevokedAtUtc == null && u.IsActive && codes.Contains(a.RoleCode)
                           select new { u.Id, u.DisplayName, u.Username })
            .Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return users.OrderBy(u => u.DisplayName).Select(u => new CollectorDto(u.Id, u.DisplayName, u.Username)).ToList();
    }

    // Plans

    public async Task<CollectionPlanDto> PlanAsync(Guid businessId, Guid debtorId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsView, businessId, cancellationToken).ConfigureAwait(false);
        var debtor = await DebtorAsync(businessId, debtorId, cancellationToken).ConfigureAwait(false);
        var plan = await db.CollectionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.DebtorId == debtorId, cancellationToken).ConfigureAwait(false)
            ?? CollectionPlan.For(businessId, debtorId);
        return await PlanDtoAsync(debtor, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CollectionPlanDto>> PlansAsync(Guid businessId, Guid? routeId, Guid? collectorUserId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.CollectionPlans.AsNoTracking().Where(p => p.BusinessId == businessId);
        query = routeId is { } r ? query.Where(p => p.RouteId == r) : query;
        query = collectorUserId is { } c ? query.Where(p => p.PrimaryCollectorUserId == c || p.BackupCollectorUserId == c) : query;
        var plans = await query.Take(1000).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = plans.Select(p => p.DebtorId).ToList();
        var debtors = await db.Debtors.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken).ConfigureAwait(false);
        var result = new List<CollectionPlanDto>();
        foreach (var plan in plans)
        {
            result.Add(await PlanDtoAsync(debtors[plan.DebtorId], plan, cancellationToken).ConfigureAwait(false));
        }

        return result.OrderBy(p => p.RouteCode).ThenBy(p => p.VisitSequence).ThenBy(p => p.DebtorName).ToList();
    }

    public async Task<CollectionPlanDto> SetPlanAsync(Guid businessId, Guid debtorId, SetCollectionPlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var debtor = await DebtorAsync(businessId, debtorId, cancellationToken).ConfigureAwait(false);
        if (request.RouteId is { } routeId && !await db.Routes.AnyAsync(r => r.Id == routeId && r.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Route");
        }

        var collectors = (await CollectorsCoreAsync(businessId, cancellationToken).ConfigureAwait(false)).Select(c => c.UserId).ToHashSet();
        foreach (var user in new[] { request.PrimaryCollectorUserId, request.BackupCollectorUserId }.OfType<Guid>())
        {
            if (!collectors.Contains(user))
            {
                throw AppException.Validation("plan.not_a_collector", "Choose someone who can collect in this business.");
            }
        }

        var weekdays = new List<DayOfWeek>();
        foreach (var name in request.Weekdays ?? [])
        {
            weekdays.Add(Enum.TryParse<DayOfWeek>(name, ignoreCase: true, out var day)
                ? day
                : throw AppException.Validation("plan.weekday_invalid", $"'{name}' is not a weekday."));
        }

        var plan = await db.CollectionPlans.FirstOrDefaultAsync(p => p.DebtorId == debtorId, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            plan = CollectionPlan.For(businessId, debtorId);
            db.CollectionPlans.Add(plan);
        }

        Valid(() =>
        {
            plan.Set(new CollectionPlan.Assignment(request.RouteId, request.VisitSequence, request.PrimaryCollectorUserId, request.BackupCollectorUserId,
                    request.PreferredFrom, request.PreferredTo),
                new CollectionPlan.Schedule(request.ScheduleType, weekdays, request.AnchorDate, request.MonthDay, request.DueOffsetDays), clock.GetUtcNow());
            return plan;
        });
        audit.Record("collection_plan.set", "debtor", debtorId, businessId, details: new
        {
            request.RouteId, request.VisitSequence, request.PrimaryCollectorUserId, request.BackupCollectorUserId, request.ScheduleType, request.Weekdays,
            request.AnchorDate, request.MonthDay, request.DueOffsetDays,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await PlanDtoAsync(debtor, plan, cancellationToken).ConfigureAwait(false);
    }

    // Visits, promises, absences

    public async Task<VisitDto> AssignVisitAsync(Guid businessId, AssignVisitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        await DebtorAsync(businessId, request.DebtorId, cancellationToken).ConfigureAwait(false);
        if ((await CollectorsCoreAsync(businessId, cancellationToken).ConfigureAwait(false)).All(c => c.UserId != request.CollectorUserId))
        {
            throw AppException.Validation("plan.not_a_collector", "Choose someone who can collect in this business.");
        }

        var visit = Valid(() => CollectionVisit.Assign(businessId, request.DebtorId, request.CollectorUserId, request.VisitDate, BusinessCalendar.Today(clock), request.Note,
            currentUser.UserId, clock.GetUtcNow()));
        db.CollectionVisits.Add(visit);
        audit.Record("collection_visit.assigned", "debtor", request.DebtorId, businessId, details: new { visit.Id, visit.CollectorUserId, visit.VisitDate, visit.Note });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await VisitsCoreAsync(businessId, v => v.Id == visit.Id, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<IReadOnlyList<VisitDto>> VisitsAsync(Guid businessId, DateOnly date, Guid? collectorUserId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsView, businessId, cancellationToken).ConfigureAwait(false);
        return await VisitsCoreAsync(businessId, v => v.VisitDate == date && (collectorUserId == null || v.CollectorUserId == collectorUserId), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CancelVisitAsync(Guid businessId, Guid visitId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var visit = await db.CollectionVisits.FirstOrDefaultAsync(v => v.Id == visitId && v.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Visit");
        visit.Cancel();
        audit.Record("collection_visit.cancelled", "debtor", visit.DebtorId, businessId, details: new { visit.Id, visit.VisitDate });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A promise to pay: recorded by the office, or by the collector the party is planned for.</summary>
    public async Task<PromiseDto> RecordPromiseAsync(Guid businessId, Guid debtorId, RecordPromiseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await DebtorAsync(businessId, debtorId, cancellationToken).ConfigureAwait(false);
        if (!await CanAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false) &&
            !await IsCollectorOfAsync(businessId, debtorId, cancellationToken).ConfigureAwait(false))
        {
            await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        }

        var promise = Valid(() => PaymentPromise.Record(businessId, debtorId, request.Amount, request.PromisedDate, BusinessCalendar.Today(clock), request.Note,
            currentUser.UserId, clock.GetUtcNow()));
        db.PaymentPromises.Add(promise);
        audit.Record("payment_promise.recorded", "debtor", debtorId, businessId, details: new { promise.Id, promise.Amount, promise.PromisedDate, promise.Note });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await PromisesCoreAsync(debtorId, cancellationToken).ConfigureAwait(false)).First(p => p.Id == promise.Id);
    }

    public async Task<IReadOnlyList<PromiseDto>> PromisesAsync(Guid businessId, Guid debtorId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsView, businessId, cancellationToken).ConfigureAwait(false);
        await DebtorAsync(businessId, debtorId, cancellationToken).ConfigureAwait(false);
        return await PromisesCoreAsync(debtorId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelPromiseAsync(Guid businessId, Guid promiseId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var promise = await db.PaymentPromises.FirstOrDefaultAsync(p => p.Id == promiseId && p.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Promise");
        promise.Cancel();
        audit.Record("payment_promise.cancelled", "debtor", promise.DebtorId, businessId, details: new { promise.Id });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AbsenceDto> RecordAbsenceAsync(Guid businessId, RecordAbsenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var collector = (await CollectorsCoreAsync(businessId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.UserId == request.CollectorUserId)
            ?? throw AppException.Validation("plan.not_a_collector", "Choose someone who can collect in this business.");
        if (await db.CollectorAbsences.AnyAsync(a => a.BusinessId == businessId && a.CollectorUserId == request.CollectorUserId && a.AbsentOn == request.AbsentOn,
                cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("absence.exists", $"{collector.DisplayName} is already marked away on {request.AbsentOn:dd-MM-yyyy}.");
        }

        var absence = Valid(() => CollectorAbsence.Record(businessId, request.CollectorUserId, request.AbsentOn, request.Reason, currentUser.UserId, clock.GetUtcNow()));
        db.CollectorAbsences.Add(absence);
        audit.Record("collector.absent", "user", request.CollectorUserId, businessId, details: new { absence.AbsentOn, absence.Reason });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new AbsenceDto(absence.Id, absence.CollectorUserId, collector.DisplayName, absence.AbsentOn, absence.Reason);
    }

    public async Task<IReadOnlyList<AbsenceDto>> AbsencesAsync(Guid businessId, DateOnly since, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsView, businessId, cancellationToken).ConfigureAwait(false);
        return await (from a in db.CollectorAbsences.AsNoTracking()
                      join u in db.Users.AsNoTracking() on a.CollectorUserId equals u.Id
                      where a.BusinessId == businessId && a.AbsentOn >= since
                      orderby a.AbsentOn, u.DisplayName
                      select new AbsenceDto(a.Id, a.CollectorUserId, u.DisplayName, a.AbsentOn, a.Reason)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAbsenceAsync(Guid businessId, Guid absenceId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var absence = await db.CollectorAbsences.FirstOrDefaultAsync(a => a.Id == absenceId && a.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Absence");
        db.CollectorAbsences.Remove(absence);
        audit.Record("collector.absence_removed", "user", absence.CollectorUserId, businessId, details: new { absence.AbsentOn });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    // The day

    /// <summary>
    /// The parties a collector visits on a day: scheduled by their plan (or a due date it follows), assigned visits,
    /// promises falling due, the parties of absent collectors they back up, and optionally every party of theirs with
    /// something overdue. A collector sees only their own day; managers any collector's.
    /// </summary>
    public async Task<DayListDto> DayAsync(Guid businessId, Guid? collectorUserId, DateOnly? date, bool includeOverdue, CancellationToken cancellationToken)
    {
        var collector = collectorUserId ?? currentUser.UserId;
        if (collector != currentUser.UserId || !await CanAsync(Permissions.CollectionsCollect, businessId, cancellationToken).ConfigureAwait(false))
        {
            await RequireAsync(Permissions.CollectionsView, businessId, cancellationToken).ConfigureAwait(false);
        }

        var day = date ?? BusinessCalendar.Today(clock);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == collector).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Collector");
        var absent = await db.CollectorAbsences.AsNoTracking().Where(a => a.BusinessId == businessId && a.AbsentOn == day).Select(a => a.CollectorUserId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var away = absent.Contains(collector);

        // Candidates: own plans (unless away), backed-up plans of absent collectors, assigned visits, and promises of these parties.
        var plans = await db.CollectionPlans.AsNoTracking()
            .Where(p => p.BusinessId == businessId &&
                        ((p.PrimaryCollectorUserId == collector && !away) || (p.BackupCollectorUserId == collector && p.PrimaryCollectorUserId != null && absent.Contains(p.PrimaryCollectorUserId.Value))))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var visits = await db.CollectionVisits.AsNoTracking().Where(v => v.BusinessId == businessId && v.CollectorUserId == collector && v.VisitDate == day && !v.IsCancelled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = plans.Select(p => p.DebtorId).Concat(visits.Select(v => v.DebtorId)).Distinct().ToList();
        var debtors = await db.Debtors.AsNoTracking().Where(d => ids.Contains(d.Id) && d.Status != DebtorStatus.Closed).ToDictionaryAsync(d => d.Id, cancellationToken)
            .ConfigureAwait(false);
        ids = ids.Where(debtors.ContainsKey).ToList();

        // Unpaid charges and balances of all candidates at once.
        var charges = await (from e in db.DebtorLedger.AsNoTracking()
                             where ids.Contains(e.PartyId) && e.Amount > 0
                             select new
                             {
                                 e.PartyId, e.DocumentNumber, e.EntryType, e.DueDate,
                                 Remaining = e.Amount - (db.DebtorSettlements.Where(s => s.ChargeEntryId == e.Id).Sum(s => (decimal?)s.Amount) ?? 0),
                             }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var unpaid = charges.Where(c => c.Remaining > 0).ToList();
        var balances = await db.DebtorLedger.AsNoTracking().Where(e => ids.Contains(e.PartyId)).GroupBy(e => e.PartyId)
            .Select(g => new { g.Key, Balance = g.Sum(e => e.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Balance, cancellationToken).ConfigureAwait(false);
        var receipts = await db.DebtorReceipts.AsNoTracking().Where(r => ids.Contains(r.DebtorId))
            .Select(r => new { r.DebtorId, r.ReceiptDate, r.Amount, r.CreatedAtUtc }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var promises = await db.PaymentPromises.AsNoTracking().Where(p => ids.Contains(p.DebtorId) && !p.IsCancelled && p.PromisedDate >= day.AddDays(-7))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var routeIds = plans.Where(p => p.RouteId != null).Select(p => p.RouteId!.Value).Distinct().ToList();
        var routes = await db.Routes.AsNoTracking().Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);

        var parties = new List<DayPartyDto>();
        foreach (var id in ids)
        {
            var debtor = debtors[id];
            var plan = plans.FirstOrDefault(p => p.DebtorId == id);
            var open = unpaid.Where(c => c.PartyId == id).ToList();
            var reasons = new List<string>();
            if (plan is not null && plan.IsScheduledOn(day, open.Where(c => c.DueDate != null).Select(c => c.DueDate!.Value)))
            {
                reasons.Add(plan.ScheduleType == ScheduleTypes.DueDate ? VisitReasons.DueDate : VisitReasons.Schedule);
            }

            if (visits.Any(v => v.DebtorId == id))
            {
                reasons.Add(VisitReasons.Assigned);
            }

            var promise = promises.Where(p => p.DebtorId == id).OrderByDescending(p => p.RecordedAtUtc).FirstOrDefault();
            if (promise is not null && promise.PromisedDate == day)
            {
                reasons.Add(VisitReasons.Promise);
            }

            var overdue = open.Where(c => c.DueDate < day).Sum(c => c.Remaining);
            if (includeOverdue && overdue > 0 && plan is not null && reasons.Count == 0)
            {
                reasons.Add(VisitReasons.Overdue);
            }

            if (reasons.Count == 0)
            {
                continue;
            }

            if (plan is not null && plan.PrimaryCollectorUserId != collector)
            {
                reasons.Add(VisitReasons.Backup);
            }

            var oldest = open.Where(c => c.DueDate != null).OrderBy(c => c.DueDate).FirstOrDefault();
            var last = receipts.Where(r => r.DebtorId == id).OrderByDescending(r => r.CreatedAtUtc).FirstOrDefault();
            var collectedToday = receipts.Where(r => r.DebtorId == id && r.ReceiptDate == day).Sum(r => r.Amount);
            var route = plan?.RouteId is { } rid ? routes.GetValueOrDefault(rid) : null;
            parties.Add(new DayPartyDto(
                id, debtor.Code, debtor.DisplayName, route?.Code, route?.Name, plan?.VisitSequence, debtor.Address, debtor.Phone, debtor.WhatsAppNumber,
                plan?.PreferredFrom, plan?.PreferredTo, reasons, visits.Where(v => v.DebtorId == id && v.Note.Length > 0).Select(v => v.Note).ToList(),
                balances.GetValueOrDefault(id), open.Where(c => c.DueDate <= day).Sum(c => c.Remaining), overdue, open.Where(c => c.DueDate > day).Sum(c => c.Remaining),
                oldest is null ? null : oldest.DocumentNumber ?? (oldest.EntryType == LedgerEntryTypes.Opening ? "Opening balance" : oldest.EntryType),
                oldest?.DueDate, oldest?.DueDate is { } due && due < day ? day.DayNumber - due.DayNumber : 0, last?.ReceiptDate, last?.Amount,
                promise?.Amount, promise?.PromisedDate, collectedToday, collectedToday > 0 ? "COLLECTED" : "PENDING"));
        }

        // On a route first (by route, then sequence), then the rest by name.
        var ordered = parties.OrderBy(p => p.RouteCode is null).ThenBy(p => p.RouteCode, StringComparer.Ordinal).ThenBy(p => p.VisitSequence ?? int.MaxValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new DayListDto(collector, name, day, away, ordered, ordered.Sum(p => p.DueBalance), ordered.Sum(p => p.OverdueBalance), ordered.Sum(p => p.CollectedToday));
    }

    // Helpers

    private async Task<List<PromiseDto>> PromisesCoreAsync(Guid debtorId, CancellationToken cancellationToken)
    {
        var today = BusinessCalendar.Today(clock);
        var promises = await (from p in db.PaymentPromises.AsNoTracking()
                              join u in db.Users.AsNoTracking() on p.RecordedByUserId equals u.Id
                              where p.DebtorId == debtorId
                              orderby p.RecordedAtUtc descending
                              select new { Promise = p, By = u.DisplayName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var receipts = await db.DebtorReceipts.AsNoTracking().Where(r => r.DebtorId == debtorId).Select(r => new { r.ReceiptDate, r.Amount, r.CreatedAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return promises.Select(x =>
        {
            var p = x.Promise;
            var paid = receipts.Where(r => r.CreatedAtUtc >= p.RecordedAtUtc && r.ReceiptDate <= p.PromisedDate).Sum(r => r.Amount);
            var status = p.IsCancelled ? "CANCELLED" : paid >= p.Amount ? "KEPT" : today > p.PromisedDate ? "BROKEN" : "PENDING";
            return new PromiseDto(p.Id, p.DebtorId, p.Amount, p.PromisedDate, p.Note, status, paid, x.By, p.RecordedAtUtc);
        }).ToList();
    }

    private async Task<List<VisitDto>> VisitsCoreAsync(Guid businessId, System.Linq.Expressions.Expression<Func<CollectionVisit, bool>> filter, CancellationToken cancellationToken) =>
        await (from v in db.CollectionVisits.AsNoTracking().Where(v => v.BusinessId == businessId).Where(filter)
               join d in db.Debtors.AsNoTracking() on v.DebtorId equals d.Id
               join c in db.Users.AsNoTracking() on v.CollectorUserId equals c.Id
               join a in db.Users.AsNoTracking() on v.AssignedByUserId equals a.Id
               orderby v.VisitDate, c.DisplayName
               select new VisitDto(v.Id, v.DebtorId, d.TradeName ?? d.LegalName, v.CollectorUserId, c.DisplayName, v.VisitDate, v.Note, v.IsCancelled, a.DisplayName))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<CollectionPlanDto> PlanDtoAsync(Debtor debtor, CollectionPlan plan, CancellationToken cancellationToken)
    {
        var route = plan.RouteId is { } r ? await db.Routes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r, cancellationToken).ConfigureAwait(false) : null;
        var userIds = new[] { plan.PrimaryCollectorUserId, plan.BackupCollectorUserId }.OfType<Guid>().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
        var weekdays = plan.Weekdays.Select(d => d.ToString().ToUpperInvariant()).ToList();
        return new CollectionPlanDto(debtor.Id, debtor.Code, debtor.DisplayName, plan.RouteId, route?.Code, plan.VisitSequence, plan.PrimaryCollectorUserId,
            plan.PrimaryCollectorUserId is { } p ? names.GetValueOrDefault(p) : null, plan.BackupCollectorUserId,
            plan.BackupCollectorUserId is { } b ? names.GetValueOrDefault(b) : null, plan.PreferredFrom, plan.PreferredTo, plan.ScheduleType, weekdays, plan.AnchorDate,
            plan.MonthDay, plan.DueOffsetDays, Describe(plan, weekdays));
    }

    private static string Describe(CollectionPlan plan, List<string> weekdays)
    {
        string Days() => string.Join(", ", weekdays.Select(d => d[..1] + d[1..3].ToLowerInvariant()));
        return plan.ScheduleType switch
        {
            ScheduleTypes.Weekdays => $"Every {Days()}",
            ScheduleTypes.Fortnightly => $"Every second {Days()} (from the week of {plan.AnchorDate:dd-MM-yyyy})",
            ScheduleTypes.Monthly => $"Monthly on day {plan.MonthDay}",
            ScheduleTypes.DueDate => plan.DueOffsetDays switch
            {
                0 => "On the due date",
                < 0 => $"{-plan.DueOffsetDays} days before the due date",
                _ => $"{plan.DueOffsetDays} days after the due date",
            },
            ScheduleTypes.SpecificDate => $"On {plan.AnchorDate:dd-MM-yyyy}",
            _ => "Only when a visit is assigned",
        };
    }

    private async Task<Debtor> DebtorAsync(Guid businessId, Guid debtorId, CancellationToken cancellationToken) =>
        await db.Debtors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == debtorId && d.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.NotFound("Debtor");

    private async Task<bool> IsCollectorOfAsync(Guid businessId, Guid debtorId, CancellationToken cancellationToken) =>
        await CanAsync(Permissions.CollectionsCollect, businessId, cancellationToken).ConfigureAwait(false) &&
        await db.CollectionPlans.AnyAsync(p => p.DebtorId == debtorId && (p.PrimaryCollectorUserId == currentUser.UserId || p.BackupCollectorUserId == currentUser.UserId),
            cancellationToken).ConfigureAwait(false);

    private async Task<bool> CanAsync(string permission, Guid businessId, CancellationToken cancellationToken) =>
        (await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false)).Contains(businessId);

    private async Task RequireAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(permission, businessId, cancellationToken).ConfigureAwait(false))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static T Valid<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }
    }
}
