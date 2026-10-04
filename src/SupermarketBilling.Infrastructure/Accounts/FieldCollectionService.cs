using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Accounts;

internal sealed record ReceiptReversalPayload(Guid ReceiptId, string Reason);

/// <summary>
/// Collecting in the field and custody of what is collected (spec sections 15 and 17): the collector's round, field
/// receipts (only from the collector's own parties), handing over the cash and instruments, the receiver's count
/// (maker-checker), the cheque register with bounce and cancellation reversals, approved corrections, and visit outcomes.
/// </summary>
public sealed class FieldCollectionService(
    SupermarketBillingDbContext db,
    DebtorReceiptService receipts,
    OrganisationService organisation,
    IAccessControl access,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public const string ReversalApprovalType = "receipt.reverse";

    // The collector's round

    public async Task<CollectorSessionDto?> MySessionAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsCollect, businessId, cancellationToken).ConfigureAwait(false);
        var id = await db.CollectorSessions.AsNoTracking()
            .Where(s => s.BusinessId == businessId && s.CollectorUserId == currentUser.UserId && s.Status != CollectorSessionStatus.Confirmed)
            .OrderByDescending(s => s.OpenedAtUtc).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return id is { } sessionId ? await SessionDtoAsync(sessionId, showExpected: false, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task<CollectorSessionDto> OpenSessionAsync(Guid businessId, OpenCollectorSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsCollect, businessId, cancellationToken).ConfigureAwait(false);
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StoreId && s.BusinessId == businessId && s.IsActive, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Store");
        if (await db.CollectorSessions.AnyAsync(s => s.BusinessId == businessId && s.CollectorUserId == currentUser.UserId && s.Status == CollectorSessionStatus.Open,
                cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("session.already_open", "You already have a collection round open. Hand it over before starting another.");
        }

        var session = CollectorSession.Open(businessId, store.Id, currentUser.UserId, BusinessCalendar.Today(clock, store.TimeZone), clock.GetUtcNow());
        db.CollectorSessions.Add(session);
        audit.Record("collection.round_opened", "collector_session", session.Id, businessId, store.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await SessionDtoAsync(session.Id, showExpected: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A collection in the field, in the collector's open round, from a party on their plan or assigned to them today.</summary>
    public async Task<DebtorReceiptDto> CollectAsync(Guid businessId, FieldCollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsCollect, businessId, cancellationToken).ConfigureAwait(false);
        var today = BusinessCalendar.Today(clock);
        var mine = await db.CollectionPlans.AnyAsync(p => p.DebtorId == request.DebtorId &&
                                                          (p.PrimaryCollectorUserId == currentUser.UserId || p.BackupCollectorUserId == currentUser.UserId), cancellationToken)
                       .ConfigureAwait(false)
                   || await db.CollectionVisits.AnyAsync(v => v.DebtorId == request.DebtorId && v.CollectorUserId == currentUser.UserId && v.VisitDate == today && !v.IsCancelled,
                       cancellationToken).ConfigureAwait(false);
        if (!mine)
        {
            throw AppException.Forbidden("This party is not on your collection plan or visits.");
        }

        if (request.Allocations is { Count: > 0 } && !await CanAsync(Permissions.CollectionsAllocate, businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden("Only those allowed to allocate may choose which bills a collection pays; it pays the oldest due first.");
        }

        var session = await db.CollectorSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BusinessId == businessId && s.CollectorUserId == currentUser.UserId && s.Status == CollectorSessionStatus.Open, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.Conflict("session.not_open", "Start your collection round first.");
        var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == session.StoreId, cancellationToken).ConfigureAwait(false);
        return await receipts.CreateInFieldAsync(businessId, store, session.Id,
            new DebtorReceiptRequest(request.DebtorId, request.Method, request.Amount, store.Id, request.Reference, request.Note, request.Allocations, request.IdempotencyKey,
                request.BankName, request.ChequeDate),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The collector counts their cash and hands it over with the instruments; the round is then closed for collections.</summary>
    public async Task<CollectorSessionDto> HandOverAsync(Guid businessId, Guid sessionId, HandOverRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsCollect, businessId, cancellationToken).ConfigureAwait(false);
        var counts = Counts(request.Counts);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Exclusive: waits for receipts being saved in the round, and stops new ones.
        var session = (await db.CollectorSessions.FromSql($"SELECT *, xmin FROM collector_sessions WHERE id = {sessionId} FOR UPDATE").ToListAsync(cancellationToken)
            .ConfigureAwait(false)).SingleOrDefault(s => s.BusinessId == businessId) ?? throw AppException.NotFound("Collection round");
        if (session.CollectorUserId != currentUser.UserId)
        {
            throw AppException.Forbidden("Only the collector hands over their own round.");
        }

        var now = clock.GetUtcNow();
        var expected = await ExpectedCashAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Valid(() => session.HandOver(expected, Denominations.Total(counts), now));
        db.CollectorSessionCounts.AddRange(CollectorSessionCount.From(businessId, sessionId, CollectorSessionCount.Declared, counts, now));
        audit.Record("collection.handed_over", "collector_session", sessionId, businessId, session.StoreId, details: new { session.ExpectedCash, session.DeclaredCash });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await SessionDtoAsync(sessionId, showExpected: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Another person counts what was handed over; the difference from the expected cash is recorded (and must be explained).</summary>
    public async Task<CollectorSessionDto> ConfirmAsync(Guid businessId, Guid sessionId, ConfirmHandoverRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var counts = Counts(request.Counts);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var session = (await db.CollectorSessions.FromSql($"SELECT *, xmin FROM collector_sessions WHERE id = {sessionId} FOR UPDATE").ToListAsync(cancellationToken)
            .ConfigureAwait(false)).SingleOrDefault(s => s.BusinessId == businessId) ?? throw AppException.NotFound("Collection round");
        await organisation.RequireAsync(Permissions.CollectionsReceive, businessId, session.StoreId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        Valid(() => session.Confirm(currentUser.UserId, Denominations.Total(counts), request.Note, now));
        db.CollectorSessionCounts.AddRange(CollectorSessionCount.From(businessId, sessionId, CollectorSessionCount.Counted, counts, now));
        audit.Record("collection.handover_confirmed", "collector_session", sessionId, businessId, session.StoreId, details: new
        {
            session.ExpectedCash, session.DeclaredCash, session.CountedCash, session.Variance, session.Note, collector = session.CollectorUserId,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await SessionDtoAsync(sessionId, showExpected: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CollectorSessionDto>> SessionsAsync(Guid businessId, Guid storeId, string? status, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.CollectionsReceive, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.CollectorSessions.AsNoTracking().Where(s => s.StoreId == storeId);
        query = string.IsNullOrWhiteSpace(status) ? query : query.Where(s => s.Status == status);
        var ids = await query.OrderByDescending(s => s.OpenedAtUtc).Take(100).Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CollectorSessionDto>();
        foreach (var id in ids)
        {
            result.Add(await SessionDtoAsync(id, showExpected: true, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    // Visits

    public async Task RecordOutcomeAsync(Guid businessId, VisitOutcomeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsCollect, businessId, cancellationToken).ConfigureAwait(false);
        if (!await db.Debtors.AnyAsync(d => d.Id == request.DebtorId && d.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Debtor");
        }

        var outcome = Valid(() => VisitOutcome.Record(businessId, request.DebtorId, currentUser.UserId, BusinessCalendar.Today(clock), request.Outcome, request.Note,
            clock.GetUtcNow()));
        db.VisitOutcomes.Add(outcome);
        audit.Record("collection.visit_outcome", "debtor", request.DebtorId, businessId, details: new { outcome.Outcome, outcome.Note });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    // Cheques

    public async Task<IReadOnlyList<ChequeDto>> ChequesAsync(Guid businessId, string? status, Guid? debtorId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.DebtorsView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.Cheques.AsNoTracking().Where(c => c.BusinessId == businessId);
        query = string.IsNullOrWhiteSpace(status) ? query : query.Where(c => c.Status == status);
        query = debtorId is { } d ? query.Where(c => c.DebtorId == d) : query;
        var ids = await query.OrderByDescending(c => c.ReceivedAtUtc).Take(300).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ChequeDto>();
        foreach (var id in ids)
        {
            result.Add(await ChequeDtoAsync(id, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Moves a cheque along (deposited, cleared, bounced, cancelled, replaced). A bounce or cancellation reverses its
    /// receipt in the same transaction, so the debtor owes the amount again; the history keeps who did what and when.
    /// </summary>
    public async Task<ChequeDto> MoveChequeAsync(Guid businessId, Guid chequeId, MoveChequeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.ReceivablesManage, businessId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var cheque = (await db.Cheques.FromSql($"SELECT *, xmin FROM cheques WHERE id = {chequeId} FOR UPDATE").ToListAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(c => c.BusinessId == businessId) ?? throw AppException.NotFound("Cheque");
        var to = (request.To ?? string.Empty).Trim().ToUpperInvariant();
        if (to == ChequeStatus.Replaced && request.ReplacedByReceiptId is { } replacement &&
            !await db.DebtorReceipts.AnyAsync(r => r.Id == replacement && r.DebtorId == cheque.DebtorId && r.Id != cheque.ReceiptId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Validation("cheque.replacement_invalid", "The replacement must be another receipt from the same debtor.");
        }

        var from = cheque.Status;
        Valid(() => { cheque.Move(to, request.ReplacedByReceiptId); return cheque; });
        var now = clock.GetUtcNow();
        var date = request.Date ?? BusinessCalendar.Today(clock);
        db.ChequeEvents.Add(Valid(() => ChequeEvent.Record(businessId, cheque.Id, to, date, request.Note, currentUser.UserId, now)));
        if (to is ChequeStatus.Bounced or ChequeStatus.Cancelled)
        {
            var reason = string.IsNullOrWhiteSpace(request.Note) ? $"{cheque.Kind.Replace('_', ' ').ToLowerInvariant()} {cheque.Number} {to.ToLowerInvariant()}" : request.Note.Trim();
            await receipts.ReverseCoreAsync(businessId, cheque.ReceiptId, to == ChequeStatus.Bounced ? ReversalKinds.Bounced : ReversalKinds.Cancelled, reason, null,
                currentUser.UserId, now, cancellationToken).ConfigureAwait(false);
        }

        audit.Record("cheque.moved", "cheque", cheque.Id, businessId, details: new { cheque.Number, from, to, date, request.Note, request.ReplacedByReceiptId });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ChequeDtoAsync(chequeId, cancellationToken).ConfigureAwait(false);
    }

    // Corrections

    /// <summary>Asks for a receipt to be reversed (recorded in error). Another person must approve; cheques are reversed by bouncing or cancelling them instead.</summary>
    public async Task<ReverseReceiptResponse> RequestReversalAsync(Guid businessId, Guid receiptId, ReverseReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.ReceivablesManage, businessId, cancellationToken).ConfigureAwait(false);
        var receipt = await db.DebtorReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId && r.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Receipt");
        if (ReceiptMethods.IsInstrument(receipt.Method))
        {
            throw AppException.Conflict("reversal.use_cheque", "A cheque or draft receipt is reversed by marking the cheque bounced or cancelled.");
        }

        if (await db.ReceiptReversals.AnyAsync(r => r.ReceiptId == receiptId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("reversal.exists", $"Receipt {receipt.Number} is already reversed.");
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length is < 5 or > 300)
        {
            throw AppException.Validation("reversal.reason_required", "Explain why the receipt is reversed (5 to 300 characters).");
        }

        var others = await ApprovalService.OtherUsersGrantsAsync(db, businessId, [currentUser.UserId], cancellationToken).ConfigureAwait(false);
        if (!others.Any(g => LedgerAdjustmentHandler.IsEligible(g, businessId)))
        {
            throw AppException.Conflict("ledger.no_approver", "Nobody else in the business can approve the reversal, so it cannot be made.");
        }

        var approval = ApprovalRequest.Create(businessId, ReversalApprovalType, $"Reverse receipt {receipt.Number} of Rs. {receipt.Amount:0.00}",
            JsonSerializer.Serialize(new ReceiptReversalPayload(receiptId, reason), UserAdminService.Json), reason, currentUser.UserId, clock.GetUtcNow(),
            TimeSpan.FromDays(options.Value.ApprovalLifetimeDays));
        db.ApprovalRequests.Add(approval);
        audit.Record("approval.requested", "approval_request", approval.Id, businessId, receipt.StoreId, details: new { approval.Type, approval.Summary });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new ReverseReceiptResponse(approval.Id, "The reversal waits for another authorised person to approve it.");
    }

    // Helpers

    private async Task<decimal> ExpectedCashAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.DebtorReceipts.Where(r => r.CollectorSessionId == sessionId && r.Method == ReceiptMethods.Cash).SumAsync(r => (decimal?)r.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0;

    private async Task<CollectorSessionDto> SessionDtoAsync(Guid sessionId, bool showExpected, CancellationToken cancellationToken)
    {
        var row = await (from x in db.CollectorSessions.AsNoTracking()
                         join c in db.Users.AsNoTracking() on x.CollectorUserId equals c.Id
                         from r in db.Users.AsNoTracking().Where(u => u.Id == x.ReceivedByUserId).DefaultIfEmpty()
                         where x.Id == sessionId
                         select new { Session = x, Collector = c.DisplayName, Receiver = r == null ? null : r.DisplayName }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var s = row.Session;
        var totals = await db.DebtorReceipts.AsNoTracking().Where(r => r.CollectorSessionId == sessionId).GroupBy(r => r.Method)
            .Select(g => new { Method = g.Key, Amount = g.Sum(r => r.Amount), Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var instruments = await (from q in db.Cheques.AsNoTracking()
                                 join r in db.DebtorReceipts.AsNoTracking() on q.ReceiptId equals r.Id
                                 join d in db.Debtors.AsNoTracking() on q.DebtorId equals d.Id
                                 where r.CollectorSessionId == sessionId
                                 orderby q.ReceivedAtUtc
                                 select new SessionInstrumentDto(q.Id, q.Kind, q.Number, q.BankName, q.Amount, d.TradeName ?? d.LegalName, q.Status))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The collector does not see the expected cash before the count is confirmed (a blind handover).
        var reveal = showExpected || s.Status == CollectorSessionStatus.Confirmed;
        var expected = reveal ? s.ExpectedCash ?? await ExpectedCashAsync(sessionId, cancellationToken).ConfigureAwait(false) : (decimal?)null;
        return new CollectorSessionDto(s.Id, s.StoreId, s.CollectorUserId, row.Collector, s.BusinessDate, s.Status, s.OpenedAtUtc, totals.Sum(t => t.Count),
            totals.OrderBy(t => t.Method).Select(t => new MethodTotalDto(t.Method, t.Amount)).ToList(), instruments, expected, s.DeclaredCash, s.CountedCash, s.Variance,
            row.Receiver, s.Note, s.HandedOverAtUtc, s.ConfirmedAtUtc, s.RowVersion);
    }

    private async Task<ChequeDto> ChequeDtoAsync(Guid chequeId, CancellationToken cancellationToken)
    {
        var row = await (from x in db.Cheques.AsNoTracking()
                         join r in db.DebtorReceipts.AsNoTracking() on x.ReceiptId equals r.Id
                         join d in db.Debtors.AsNoTracking() on x.DebtorId equals d.Id
                         from n in db.DebtorReceipts.AsNoTracking().Where(y => y.Id == x.ReplacedByReceiptId).DefaultIfEmpty()
                         where x.Id == chequeId
                         select new { Cheque = x, Receipt = r.Number, Debtor = d.TradeName ?? d.LegalName, Replacement = n == null ? null : n.Number })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var history = await (from e in db.ChequeEvents.AsNoTracking()
                             join u in db.Users.AsNoTracking() on e.RecordedByUserId equals u.Id
                             where e.ChequeId == chequeId
                             orderby e.RecordedAtUtc
                             select new ChequeEventDto(e.Status, e.EventDate, e.Note, u.DisplayName, e.RecordedAtUtc)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var q = row.Cheque;
        return new ChequeDto(q.Id, q.Kind, q.Number, q.BankName, q.ChequeDate, q.Amount, q.Status, q.ReceiptId, row.Receipt, q.DebtorId, row.Debtor, row.Replacement,
            history, q.RowVersion);
    }

    private static Dictionary<decimal, int> Counts(IReadOnlyList<CashCount>? counts)
    {
        var result = new Dictionary<decimal, int>();
        foreach (var c in counts ?? [])
        {
            if (!result.TryAdd(c.Denomination, c.Count))
            {
                throw AppException.Validation("count.duplicate", $"Rs. {c.Denomination} is counted twice.");
            }
        }

        try
        {
            Denominations.Total(result);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        return result;
    }

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

    private static void Valid(Action action) => Valid(() => { action(); return 0; });
}

/// <summary>An approved correction reverses the receipt, recorded as done by the person who asked for it.</summary>
internal sealed class ReceiptReversalHandler(DebtorReceiptService receipts) : IApprovalHandler
{
    public string Type => FieldCollectionService.ReversalApprovalType;

    public bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, Guid actorUserId, ApprovalRequest request) => LedgerAdjustmentHandler.IsEligible(actorGrants, request.BusinessId);

    public async Task ApplyAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ReceiptReversalPayload>(request.PayloadJson, UserAdminService.Json)
            ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");
        await receipts.ReverseCoreAsync(request.BusinessId, payload.ReceiptId, ReversalKinds.Correction, payload.Reason, request.Id, request.RequestedByUserId, now,
            cancellationToken).ConfigureAwait(false);
    }

    public Task ClosedWithoutApprovalAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
}
