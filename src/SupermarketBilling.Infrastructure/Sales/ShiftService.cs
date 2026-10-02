using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Sales;

/// <summary>
/// Cashier shifts: opening float, cash moved in and out, the blind closing count against the expected cash, and the
/// manager's review of any difference. A bill or return holds a shared lock on the open shift while it is saved and
/// closing takes an exclusive one, so nothing lands in a shift after its cash was counted.
/// </summary>
public sealed class ShiftService(
    SupermarketBillingDbContext db,
    CounterService counters,
    IAccessControl access,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    /// <summary>The cashier's open shift on this counter, locked for sharing until the caller's transaction ends.</summary>
    public async Task<Shift> RequireOpenShiftAsync(PosDevice pos, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The shift must be checked inside the posting transaction.");
        }

        var shift = (await db.Shifts.FromSql($"SELECT *, xmin FROM shifts WHERE counter_id = {pos.Counter.Id} AND status = 'OPEN' FOR SHARE")
                .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault() ?? throw AppException.Conflict("shift.not_open", "No shift is open on this counter. Open a shift (count the opening cash) first.");
        return shift.CashierUserId == currentUser.UserId
            ? shift
            : throw AppException.Conflict("shift.not_yours", "Another cashier's shift is open on this counter. They must close it first.");
    }

    public async Task<ShiftSummaryDto?> CurrentAsync(string? deviceToken, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var id = await db.Shifts.AsNoTracking().Where(s => s.CounterId == pos.Counter.Id && s.Status == ShiftStatus.Open).Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return id is { } shiftId ? await SummaryAsync(shiftId, showExpected: false, parkedCleared: 0, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task<ShiftSummaryDto> OpenAsync(string? deviceToken, OpenShiftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var counts = Counts(request.Counts);
        var openingFloat = Total(counts);
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // One open shift per counter and per cashier: checked under a lock, and enforced by unique indexes.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"shift-open|" + pos.Counter.BusinessId}))", cancellationToken).ConfigureAwait(false);
        var open = await db.Shifts.AsNoTracking().Where(s => s.Status == ShiftStatus.Open && (s.CounterId == pos.Counter.Id || s.CashierUserId == currentUser.UserId))
            .Select(s => new { s.CounterId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (open.Any(s => s.CounterId == pos.Counter.Id))
        {
            throw AppException.Conflict("shift.already_open", "A shift is already open on this counter.");
        }

        if (open.Count > 0)
        {
            throw AppException.Conflict("shift.open_elsewhere", "You already have a shift open on another counter. Close it first.");
        }

        var shift = Shift.Open(pos.Counter.BusinessId, pos.Store.Id, pos.Counter.Id, currentUser.UserId, BusinessCalendar.Today(clock, pos.Store.TimeZone), openingFloat, now);
        db.Shifts.Add(shift);
        db.ShiftCounts.AddRange(ShiftCount.From(shift.BusinessId, shift.Id, ShiftCount.Opening, counts, now));
        audit.Record("shift.opened", "shift", shift.Id, shift.BusinessId, shift.StoreId, details: new { counter = pos.Counter.Code, shift.OpeningFloat });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await SummaryAsync(shift.Id, showExpected: false, parkedCleared: 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cash into or out of the drawer. A pay-out by a cashier needs a supervisor's approval up to the amount.</summary>
    public async Task<ShiftSummaryDto> MoveCashAsync(string? deviceToken, CashMovementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var shift = await RequireOpenShiftAsync(pos, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        SupervisorApproval? approval = null;
        if (request.Kind == CashMovementKinds.PayOut
            && !await access.HasPermissionAsync(Permissions.ShiftsManage, pos.Counter.BusinessId, pos.Store.Id, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(request.ApprovalToken))
            {
                throw AppException.Forbidden("Paying cash out of the drawer needs a supervisor's approval.");
            }

            var hash = SecretTokens.Hash(request.ApprovalToken);
            approval = (await db.SupervisorApprovals.FromSql($"SELECT * FROM supervisor_approvals WHERE token_hash = {hash} FOR UPDATE")
                    .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            if (approval is null || approval.Kind != SupervisorApprovalKinds.PayOut || request.Amount > approval.MaxAmount)
            {
                throw AppException.Forbidden("The supervisor's approval does not cover this pay-out. Ask the supervisor again.");
            }
        }

        CashMovement movement;
        try
        {
            movement = CashMovement.Record(shift.BusinessId, shift.Id, request.Kind, request.Amount, request.Reason, currentUser.UserId, approval?.Id, now);
            approval?.Use(pos.Counter.Id, currentUser.UserId, movement.Id, now);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.CashMovements.Add(movement);
        audit.Record("shift.cash_moved", "cash_movement", movement.Id, shift.BusinessId, shift.StoreId,
            details: new { counter = pos.Counter.Code, movement.Kind, movement.Amount, movement.Reason, approval = approval?.Id });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await SummaryAsync(shift.Id, showExpected: false, parkedCleared: 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The cashier closes their own shift with the counted cash (blind: the expected cash is shown only now).</summary>
    public async Task<ShiftSummaryDto> CloseAsync(string? deviceToken, CloseShiftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var shiftId = await db.Shifts.AsNoTracking().Where(s => s.CounterId == pos.Counter.Id && s.Status == ShiftStatus.Open && s.CashierUserId == currentUser.UserId)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw AppException.Conflict("shift.not_open", "You have no open shift on this counter.");
        return await CloseCoreAsync(shiftId, request, "shift.closed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A manager closes a shift for a cashier who has left, with the manager's own count.</summary>
    public async Task<ShiftSummaryDto> ForceCloseAsync(Guid businessId, Guid shiftId, CloseShiftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var storeId = await StoreOfAsync(businessId, shiftId, cancellationToken).ConfigureAwait(false);
        await RequireAsync(Permissions.ShiftsManage, businessId, storeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Note))
        {
            throw AppException.Validation("shift.note_required", "Say why the shift is being closed for the cashier.");
        }

        return await CloseCoreAsync(shiftId, request, "shift.closed_by_manager", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShiftSummaryDto> ReviewAsync(Guid businessId, Guid shiftId, ReviewShiftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var storeId = await StoreOfAsync(businessId, shiftId, cancellationToken).ConfigureAwait(false);
        await RequireAsync(Permissions.ShiftsManage, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var shift = await db.Shifts.FirstAsync(s => s.Id == shiftId, cancellationToken).ConfigureAwait(false);
        try
        {
            shift.Review(currentUser.UserId, request.Note, clock.GetUtcNow());
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        audit.Record("shift.difference_reviewed", "shift", shift.Id, shift.BusinessId, shift.StoreId, details: new { shift.Difference, shift.CloseNote, shift.ReviewNote });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await SummaryAsync(shiftId, showExpected: true, parkedCleared: 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShiftSummaryDto>> ListAsync(Guid businessId, Guid storeId, DateOnly? date, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.SalesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var manager = await access.HasPermissionAsync(Permissions.ShiftsManage, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.Shifts.AsNoTracking().Where(s => s.StoreId == storeId);
        query = date is { } d ? query.Where(s => s.BusinessDate == d) : query.Where(s => s.Status == ShiftStatus.Open || (s.ReviewedByUserId == null && s.Difference != 0));
        var ids = await query.OrderByDescending(s => s.OpenedAtUtc).Select(s => s.Id).Take(100).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ShiftSummaryDto>();
        foreach (var id in ids)
        {
            result.Add(await SummaryAsync(id, showExpected: manager, parkedCleared: 0, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<ShiftSummaryDto> GetAsync(Guid businessId, Guid shiftId, CancellationToken cancellationToken)
    {
        var storeId = await StoreOfAsync(businessId, shiftId, cancellationToken).ConfigureAwait(false);
        await RequireAsync(Permissions.SalesView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var manager = await access.HasPermissionAsync(Permissions.ShiftsManage, businessId, storeId, cancellationToken).ConfigureAwait(false);
        return await SummaryAsync(shiftId, showExpected: manager, parkedCleared: 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ShiftSummaryDto> CloseCoreAsync(Guid shiftId, CloseShiftRequest request, string auditEvent, CancellationToken cancellationToken)
    {
        var counts = Counts(request.Counts);
        var counted = Total(counts);
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Exclusive: waits for bills being saved in this shift, and stops new ones until the close is committed.
        var shift = (await db.Shifts.FromSql($"SELECT *, xmin FROM shifts WHERE id = {shiftId} FOR UPDATE").ToListAsync(cancellationToken).ConfigureAwait(false)).Single();
        var totals = await TotalsAsync(shiftId, cancellationToken).ConfigureAwait(false);
        var expected = ShiftCash.Expected(shift.OpeningFloat, totals.CashTendered, totals.ChangeGiven, totals.CashRefunded, totals.PayIns, totals.PayOuts, totals.Drops);
        try
        {
            shift.Close(expected, counted, request.Note, currentUser.UserId, now);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.ShiftCounts.AddRange(ShiftCount.From(shift.BusinessId, shift.Id, ShiftCount.Closing, counts, now));

        // Parked bills do not outlive the shift (they are carts only: nothing is lost from the books).
        var parked = await db.ParkedBills.Where(p => p.CounterId == shift.CounterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        audit.Record(auditEvent, "shift", shift.Id, shift.BusinessId, shift.StoreId,
            details: new { shift.ExpectedCash, shift.CountedCash, shift.Difference, shift.CloseNote, parkedBillsCleared = parked, cashier = shift.CashierUserId });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await SummaryAsync(shiftId, showExpected: true, parkedCleared: parked, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Totals(decimal CashTendered, decimal ChangeGiven, decimal CashRefunded, decimal PayIns, decimal PayOuts, decimal Drops);

    private async Task<Totals> TotalsAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        var cashTendered = await (from p in db.SalesInvoicePayments join i in db.SalesInvoices on p.InvoiceId equals i.Id
                                  where i.ShiftId == shiftId && p.Method == PaymentMethods.Cash select (decimal?)p.Amount).SumAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var change = await db.SalesInvoices.Where(i => i.ShiftId == shiftId).SumAsync(i => (decimal?)i.ChangeDue, cancellationToken).ConfigureAwait(false) ?? 0;
        var refunded = await (from f in db.SalesReturnRefunds join r in db.SalesReturns on f.ReturnId equals r.Id
                              where r.ShiftId == shiftId && f.Method == RefundMethods.Cash select (decimal?)f.Amount).SumAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var movements = await db.CashMovements.Where(m => m.ShiftId == shiftId).GroupBy(m => m.Kind).Select(g => new { g.Key, Sum = g.Sum(m => m.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        decimal Movement(string kind) => movements.FirstOrDefault(m => m.Key == kind)?.Sum ?? 0;
        return new Totals(cashTendered, change, refunded, Movement(CashMovementKinds.PayIn), Movement(CashMovementKinds.PayOut), Movement(CashMovementKinds.Drop));
    }

    private async Task<ShiftSummaryDto> SummaryAsync(Guid shiftId, bool showExpected, int parkedCleared, CancellationToken cancellationToken)
    {
        var row = await (
                from s in db.Shifts.AsNoTracking()
                join c in db.Counters.AsNoTracking() on s.CounterId equals c.Id
                join u in db.Users.AsNoTracking() on s.CashierUserId equals u.Id
                from r in db.Users.AsNoTracking().Where(x => x.Id == s.ReviewedByUserId).DefaultIfEmpty()
                where s.Id == shiftId
                select new { Shift = s, c.Code, Cashier = u.DisplayName, Reviewer = r == null ? null : r.DisplayName })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var shift = row.Shift;
        var invoices = await db.SalesInvoices.AsNoTracking().Where(i => i.ShiftId == shiftId).GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(i => i.GrandTotal), Change = g.Sum(i => i.ChangeDue) }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var payments = await (from p in db.SalesInvoicePayments.AsNoTracking() join i in db.SalesInvoices.AsNoTracking() on p.InvoiceId equals i.Id
                              where i.ShiftId == shiftId group p.Amount by p.Method into g select new { Method = g.Key, Amount = g.Sum() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var returns = await db.SalesReturns.AsNoTracking().Where(r => r.ShiftId == shiftId).GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(r => r.GrandTotal) }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var refunds = await (from f in db.SalesReturnRefunds.AsNoTracking() join r in db.SalesReturns.AsNoTracking() on f.ReturnId equals r.Id
                             where r.ShiftId == shiftId group f.Amount by f.Method into g select new { Method = g.Key, Amount = g.Sum() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var movements = await (from m in db.CashMovements.AsNoTracking() join u in db.Users.AsNoTracking() on m.RecordedByUserId equals u.Id
                               where m.ShiftId == shiftId orderby m.RecordedAtUtc
                               select new CashMovementDto(m.Kind, m.Amount, m.Reason, u.DisplayName, m.RecordedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Cash is shown net of the change handed back.
        var paymentTotals = payments.Select(p => new MethodTotalDto(p.Method, p.Method == PaymentMethods.Cash ? p.Amount - (invoices?.Change ?? 0) : p.Amount))
            .OrderBy(p => p.Method).ToList();
        var reveal = showExpected || shift.Status == ShiftStatus.Closed;
        return new ShiftSummaryDto(
            shift.Id, shift.StoreId, shift.CounterId, row.Code, shift.CashierUserId, row.Cashier, shift.Status, shift.BusinessDate, shift.OpenedAtUtc, shift.ClosedAtUtc, shift.OpeningFloat,
            invoices?.Count ?? 0, invoices?.Total ?? 0, returns?.Count ?? 0, returns?.Total ?? 0, paymentTotals,
            refunds.Select(r => new MethodTotalDto(r.Method, r.Amount)).OrderBy(r => r.Method).ToList(), movements,
            reveal ? shift.ExpectedCash ?? await OpenExpectedAsync(shift, cancellationToken).ConfigureAwait(false) : null,
            shift.CountedCash, shift.Difference, shift.CloseNote, shift.NeedsReview, row.Reviewer, shift.ReviewNote, parkedCleared, shift.RowVersion);
    }

    /// <summary>The expected cash of a shift that is still open (managers only, as of now).</summary>
    private async Task<decimal> OpenExpectedAsync(Shift shift, CancellationToken cancellationToken)
    {
        var t = await TotalsAsync(shift.Id, cancellationToken).ConfigureAwait(false);
        return ShiftCash.Expected(shift.OpeningFloat, t.CashTendered, t.ChangeGiven, t.CashRefunded, t.PayIns, t.PayOuts, t.Drops);
    }

    private static Dictionary<decimal, int> Counts(IReadOnlyList<DenominationCount>? counts)
    {
        var result = new Dictionary<decimal, int>();
        foreach (var c in counts ?? [])
        {
            if (!result.TryAdd(c.Denomination, c.Count))
            {
                throw AppException.Validation("count.duplicate", $"Rs. {c.Denomination} is counted twice.");
            }
        }

        return result;
    }

    private static decimal Total(Dictionary<decimal, int> counts)
    {
        try
        {
            return Denominations.Total(counts);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }
    }

    private async Task<Guid> StoreOfAsync(Guid businessId, Guid shiftId, CancellationToken cancellationToken) =>
        await db.Shifts.AsNoTracking().Where(s => s.Id == shiftId && s.BusinessId == businessId).Select(s => (Guid?)s.StoreId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Shift");

    private async Task RequireAsync(string permission, Guid businessId, Guid storeId, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(permission, businessId, storeId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden();
        }
    }
}
