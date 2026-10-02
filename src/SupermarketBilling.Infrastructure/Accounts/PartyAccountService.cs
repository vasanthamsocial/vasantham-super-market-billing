using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Accounts;

internal sealed record LedgerAdjustmentPayload(string PartyType, Guid PartyId, decimal Amount, string Reason);

/// <summary>
/// Statements, open items and ageing of supplier and debtor accounts; opening balances (once, as the first entry);
/// corrections, which always need another person's approval; and applying unapplied payments.
/// </summary>
public sealed class PartyAccountService(
    SupermarketBillingDbContext db,
    PartyLedgerService ledger,
    OrganisationService organisation,
    IAccessControl access,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public const string AdjustmentApprovalType = "ledger.adjust";

    public async Task<StatementDto> StatementAsync(string partyType, Guid businessId, Guid partyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        await RequireViewAsync(partyType, businessId, cancellationToken).ConfigureAwait(false);
        var name = await PartyNameAsync(partyType, businessId, partyId, cancellationToken).ConfigureAwait(false);
        var all = await ledger.AllWithRemainingAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        var users = await UserNamesAsync(all.Select(x => x.Entry.CreatedByUserId), cancellationToken).ConfigureAwait(false);
        var before = all.Where(x => from is { } f && x.Entry.EntryDate < f).Sum(x => x.Entry.Amount);
        var shown = all.Where(x => (from is not { } f || x.Entry.EntryDate >= f) && (to is not { } t || x.Entry.EntryDate <= t))
            .OrderBy(x => x.Entry.EntryDate).ThenBy(x => x.Entry.Sequence).ToList();

        // Within the period the running balance is recomputed in date order (entries can be dated before later postings).
        var running = before;
        var entries = shown.Select(x =>
        {
            running += x.Entry.Amount;
            return new AccountEntryDto(x.Entry.Id, x.Entry.Sequence, x.Entry.EntryType, x.Entry.StoreId, x.Entry.DocumentId, x.Entry.DocumentNumber, x.Entry.EntryDate,
                x.Entry.DueDate, x.Entry.Amount, running, x.Entry.Narration, x.Remaining, users.GetValueOrDefault(x.Entry.CreatedByUserId, "-"), x.Entry.CreatedAtUtc);
        }).ToList();
        return new StatementDto(partyType, partyId, name, from, to, before, entries, running);
    }

    public async Task<OpenItemsDto> OpenItemsAsync(string partyType, Guid businessId, Guid partyId, CancellationToken cancellationToken)
    {
        await RequireViewAsync(partyType, businessId, cancellationToken).ConfigureAwait(false);
        await PartyNameAsync(partyType, businessId, partyId, cancellationToken).ConfigureAwait(false);
        return await OpenItemsCoreAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<OpenItemsDto> OpenItemsCoreAsync(string partyType, Guid partyId, CancellationToken cancellationToken)
    {
        var today = BusinessCalendar.Today(clock);
        var all = await ledger.AllWithRemainingAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        OpenItemDto Item((PartyLedgerEntry Entry, decimal Remaining) x) => new(
            x.Entry.Id, x.Entry.EntryType, x.Entry.DocumentNumber, x.Entry.DocumentId, x.Entry.EntryDate, x.Entry.DueDate, Math.Abs(x.Entry.Amount), x.Remaining,
            x.Entry.DueDate is { } due && due < today ? today.DayNumber - due.DayNumber : 0);
        var charges = all.Where(x => x.Entry.Amount > 0 && x.Remaining > 0).Select(Item).OrderBy(i => i.DueDate).ToList();
        var payments = all.Where(x => x.Entry.Amount < 0 && x.Remaining > 0).Select(Item).OrderBy(i => i.EntryDate).ToList();
        var ageing = new AgeingDto(
            charges.Where(c => c.DaysOverdue == 0).Sum(c => c.Remaining),
            charges.Where(c => c.DaysOverdue is >= 1 and <= 30).Sum(c => c.Remaining),
            charges.Where(c => c.DaysOverdue is >= 31 and <= 60).Sum(c => c.Remaining),
            charges.Where(c => c.DaysOverdue is >= 61 and <= 90).Sum(c => c.Remaining),
            charges.Where(c => c.DaysOverdue > 90).Sum(c => c.Remaining));
        return new OpenItemsDto(partyType, partyId, all.Sum(x => x.Entry.Amount), charges.Where(c => c.DaysOverdue > 0).Sum(c => c.Remaining), charges, payments, ageing);
    }

    /// <summary>Balances and overdue amounts of many accounts at once (for lists).</summary>
    internal async Task<Dictionary<Guid, (decimal Balance, decimal Overdue)>> BalancesAsync(string partyType, IReadOnlyCollection<Guid> partyIds, CancellationToken cancellationToken)
    {
        var today = BusinessCalendar.Today(clock);
        var balances = await ledger.Entries(partyType).AsNoTracking().Where(e => partyIds.Contains(e.PartyId))
            .GroupBy(e => e.PartyId).Select(g => new { g.Key, Balance = g.Sum(e => e.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var settlements = ledger.Settlements(partyType);
        var overdueCharges = await ledger.Entries(partyType).AsNoTracking()
            .Where(e => partyIds.Contains(e.PartyId) && e.Amount > 0 && e.DueDate < today)
            .Select(e => new
            {
                e.PartyId,
                Remaining = e.Amount - (settlements.Where(s => s.ChargeEntryId == e.Id).Sum(s => (decimal?)s.Amount) ?? 0),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var overdue = overdueCharges.GroupBy(x => x.PartyId).ToDictionary(g => g.Key, g => g.Sum(x => x.Remaining));
        return balances.ToDictionary(b => b.Key, b => (b.Balance, overdue.GetValueOrDefault(b.Key)));
    }

    public async Task<OpenItemsDto> SetOpeningBalanceAsync(string partyType, Guid businessId, Guid partyId, OpeningBalanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.LedgersAdjust, businessId, null, cancellationToken).ConfigureAwait(false);
        await PartyNameAsync(partyType, businessId, partyId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostOpeningAsync(partyType, businessId, partyId, request, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await OpenItemsCoreAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Posts the opening balance (the caller checks permission and runs the transaction).</summary>
    internal async Task PostOpeningAsync(string partyType, Guid businessId, Guid partyId, OpeningBalanceRequest request, CancellationToken cancellationToken)
    {
        await ledger.LockAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
        if (await ledger.HasEntriesAsync(partyType, partyId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("ledger.opening_not_first", "An opening balance can only be the first entry of an account. Ask for a correction instead.");
        }

        if (request.AsOf > BusinessCalendar.Today(clock))
        {
            throw AppException.Validation("ledger.opening_date_invalid", "An opening balance cannot be dated in the future.");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? "Opening balance" : $"Opening balance: {request.Note.Trim()}";
        var entry = await ledger.PostAsync(partyType, businessId, partyId,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Opening, null, null, null, request.AsOf, request.DueDate, request.Amount, note.Length <= 300 ? note : note[..300]),
            currentUser.UserId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        audit.Record("ledger.opening_balance", PartyAuditType(partyType), partyId, businessId, details: new { entry.Amount, entry.EntryDate, entry.DueDate });
    }

    public async Task<LedgerAdjustmentResponse> RequestAdjustmentAsync(string partyType, Guid businessId, Guid partyId, LedgerAdjustmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.LedgersAdjust, businessId, null, cancellationToken).ConfigureAwait(false);
        var name = await PartyNameAsync(partyType, businessId, partyId, cancellationToken).ConfigureAwait(false);
        if (request.Amount == 0 || decimal.Round(request.Amount, 2) != request.Amount)
        {
            throw AppException.Validation("ledger.amount_invalid", "A correction is a non-zero amount in rupees and paise.");
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length is < 5 or > 200)
        {
            throw AppException.Validation("ledger.reason_required", "Explain the correction (5 to 200 characters).");
        }

        // Money corrections are never waived: someone else must be able to approve.
        var others = await ApprovalService.OtherUsersGrantsAsync(db, businessId, [currentUser.UserId], cancellationToken).ConfigureAwait(false);
        if (!others.Any(g => LedgerAdjustmentHandler.IsEligible(g, businessId)))
        {
            throw AppException.Conflict("ledger.no_approver", "Nobody else in the business can approve account corrections, so a correction cannot be made.");
        }

        var now = clock.GetUtcNow();
        var approval = ApprovalRequest.Create(businessId, AdjustmentApprovalType,
            $"Correct {(partyType == PartyTypes.Supplier ? "supplier" : "debtor")} account {name} by Rs. {request.Amount:+0.00;-0.00}",
            JsonSerializer.Serialize(new LedgerAdjustmentPayload(partyType, partyId, request.Amount, reason), UserAdminService.Json), reason,
            currentUser.UserId, now, TimeSpan.FromDays(options.Value.ApprovalLifetimeDays));
        db.ApprovalRequests.Add(approval);
        audit.Record("approval.requested", "approval_request", approval.Id, businessId, details: new { approval.Type, approval.Summary });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new LedgerAdjustmentResponse(approval.Id, "The correction waits for another authorised person to approve it.");
    }

    /// <summary>Applies the account's unapplied payments to its open charges, oldest first.</summary>
    public async Task<OpenItemsDto> ApplyUnappliedAsync(string partyType, Guid businessId, Guid partyId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(partyType == PartyTypes.Supplier ? Permissions.PayablesManage : Permissions.ReceivablesManage, businessId, null, cancellationToken)
            .ConfigureAwait(false);
        await PartyNameAsync(partyType, businessId, partyId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var applied = await ledger.ApplyUnappliedAsync(partyType, businessId, partyId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (applied > 0)
        {
            audit.Record("ledger.payments_applied", PartyAuditType(partyType), partyId, businessId, details: new { applied });
        }

        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await OpenItemsCoreAsync(partyType, partyId, cancellationToken).ConfigureAwait(false);
    }

    internal static string PartyAuditType(string partyType) => partyType == PartyTypes.Supplier ? "supplier" : "debtor";

    internal async Task RequireViewAsync(string partyType, Guid businessId, CancellationToken cancellationToken)
    {
        var permission = partyType == PartyTypes.Supplier ? Permissions.PurchasesView : Permissions.DebtorsView;
        if (!(await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false)).Contains(businessId))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> PartyNameAsync(string partyType, Guid businessId, Guid partyId, CancellationToken cancellationToken) =>
        (partyType == PartyTypes.Supplier
            ? await db.Suppliers.AsNoTracking().Where(s => s.Id == partyId && s.BusinessId == businessId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await db.Debtors.AsNoTracking().Where(d => d.Id == partyId && d.BusinessId == businessId).Select(d => d.TradeName ?? d.LegalName)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false))
        ?? throw AppException.NotFound(partyType == PartyTypes.Supplier ? "Supplier" : "Debtor");

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>An approved correction is posted as an ADJUSTMENT entry, recorded as made by the person who asked for it.</summary>
internal sealed class LedgerAdjustmentHandler(PartyLedgerService ledger, AuditRecorder audit, TimeProvider clock) : IApprovalHandler
{
    public string Type => PartyAccountService.AdjustmentApprovalType;

    internal static bool IsEligible(IReadOnlyCollection<ActiveGrant> grants, Guid businessId) =>
        AccessControl.Covers(grants, Permissions.ApprovalsDecide, businessId, null) && AccessControl.Covers(grants, Permissions.LedgersApprove, businessId, null);

    public bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, Guid actorUserId, ApprovalRequest request) => IsEligible(actorGrants, request.BusinessId);

    public async Task ApplyAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<LedgerAdjustmentPayload>(request.PayloadJson, UserAdminService.Json)
            ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");
        var reason = $"Correction: {payload.Reason}";
        var entry = await ledger.PostAsync(payload.PartyType, request.BusinessId, payload.PartyId,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Adjustment, null, request.Id, null, BusinessCalendar.Today(clock), null, payload.Amount,
                reason.Length <= 300 ? reason : reason[..300]),
            request.RequestedByUserId, now, cancellationToken).ConfigureAwait(false);
        audit.Record("ledger.adjusted", PartyAccountService.PartyAuditType(payload.PartyType), payload.PartyId, request.BusinessId,
            details: new { entry.Amount, entry.BalanceAfter, approval = request.Id });
    }

    public Task ClosedWithoutApprovalAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
}
