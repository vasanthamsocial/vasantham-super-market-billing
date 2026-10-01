using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Inventory;

internal sealed record NegativeStockPayload(Guid RuleId, Guid? StoreId);

/// <summary>Stock reports, inventory settings, negative-stock rules and reorder levels.</summary>
public sealed class InventoryService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public const string NegativeStockApprovalType = "negative_stock.change";

    public async Task<IReadOnlyList<StockOnHandDto>> OnHandAsync(Guid businessId, Guid storeId, string? search, bool lowOnly, CancellationToken cancellationToken)
    {
        await RequireStoreAsync(Permissions.StockView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var method = await ValuationMethodAsync(businessId, cancellationToken).ConfigureAwait(false);
        var query =
            from b in db.StockBalances.AsNoTracking()
            join v in db.ProductVariants.AsNoTracking() on b.VariantId equals v.Id
            join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
            join u in db.Units.AsNoTracking() on p.BaseUnitId equals u.Id
            from r in db.ReorderLevels.AsNoTracking().Where(r => r.StoreId == b.StoreId && r.VariantId == b.VariantId).DefaultIfEmpty()
            where b.StoreId == storeId
            select new { Balance = b, v.Code, v.Name, UnitCode = u.Code, Minimum = r == null ? (decimal?)null : r.MinimumQuantity, Reorder = r == null ? (decimal?)null : r.ReorderQuantity };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = "%" + search.Trim() + "%";
            query = query.Where(x => EF.Functions.ILike(x.Name, term) || EF.Functions.ILike(x.Code, term));
        }

        var rows = await query.OrderBy(x => x.Name).Take(2000).ToListAsync(cancellationToken).ConfigureAwait(false);
        var layerValues = await LayerValuesAsync(storeId, cancellationToken).ConfigureAwait(false);
        return rows
            .Select(x =>
            {
                var value = Valuation(method, x.Balance, layerValues.GetValueOrDefault(x.Balance.VariantId));
                var isLow = x.Minimum is { } min && x.Balance.Quantity <= min;
                return new StockOnHandDto(x.Balance.VariantId, x.Code, x.Name, x.UnitCode, x.Balance.Quantity, x.Balance.AverageCost, value,
                    x.Minimum, x.Reorder, isLow, x.Balance.Quantity < 0);
            })
            .Where(x => !lowOnly || x.IsLow || x.IsNegative)
            .ToList();
    }

    public async Task<IReadOnlyList<LedgerEntryDto>> LedgerAsync(Guid businessId, Guid storeId, Guid variantId, CancellationToken cancellationToken)
    {
        await RequireStoreAsync(Permissions.StockView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var rows = await (
                from e in db.StockLedger.AsNoTracking()
                join u in db.Users.AsNoTracking() on e.CreatedByUserId equals u.Id
                from d in db.StockDocuments.AsNoTracking().Where(d => d.Id == e.DocumentId).DefaultIfEmpty()
                from bt in db.Batches.AsNoTracking().Where(bt => bt.Id == e.BatchId).DefaultIfEmpty()
                where e.StoreId == storeId && e.VariantId == variantId
                orderby e.Sequence descending
                select new LedgerEntryDto(e.Sequence, e.OccurredAtUtc, e.BusinessDate, e.MovementType, e.Quantity, e.UnitCost, e.Value, e.BalanceAfter,
                    bt == null ? null : bt.BatchNumber, e.DocumentType, e.DocumentId, d == null ? null : d.Number, u.DisplayName))
            .Take(1000)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    /// <summary>Stock by batch, soonest expiry first. <paramref name="expiringWithinDays"/> limits to batches expiring by then (expired included).</summary>
    public async Task<IReadOnlyList<BatchStockDto>> BatchesAsync(Guid businessId, Guid storeId, int? expiringWithinDays, CancellationToken cancellationToken)
    {
        await RequireStoreAsync(Permissions.StockView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var today = await TodayAsync(storeId, cancellationToken).ConfigureAwait(false);
        var rows = await (
                from l in db.CostLayers.AsNoTracking()
                join bt in db.Batches.AsNoTracking() on l.BatchId equals bt.Id
                join v in db.ProductVariants.AsNoTracking() on l.VariantId equals v.Id
                where l.StoreId == storeId && l.RemainingQuantity > 0
                group l.RemainingQuantity by new { bt.Id, bt.VariantId, v.Name, bt.BatchNumber, bt.ExpiresOn } into g
                select new { g.Key.Id, g.Key.VariantId, g.Key.Name, g.Key.BatchNumber, g.Key.ExpiresOn, Quantity = g.Sum() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Select(r =>
            {
                int? days = r.ExpiresOn is { } e ? e.DayNumber - today.DayNumber : null;
                return new BatchStockDto(r.Id, r.VariantId, r.Name, r.BatchNumber, r.ExpiresOn, days, days < 0, r.Quantity);
            })
            .Where(r => expiringWithinDays is not { } within || (r.DaysToExpiry is { } d && d <= within))
            .OrderBy(r => r.ExpiresOn ?? DateOnly.MaxValue).ThenBy(r => r.VariantName)
            .ToList();
    }

    /// <summary>Remaining stock by how long ago it was received (0-30, 31-60, 61-90, over 90 days).</summary>
    public async Task<IReadOnlyList<StockAgeingDto>> AgeingAsync(Guid businessId, Guid storeId, CancellationToken cancellationToken)
    {
        await RequireStoreAsync(Permissions.StockView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var layers = await (
                from l in db.CostLayers.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on l.VariantId equals v.Id
                where l.StoreId == storeId && l.RemainingQuantity > 0
                select new { l.VariantId, v.Name, l.RemainingQuantity, l.UnitCost, l.ReceivedAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return layers
            .GroupBy(l => (l.VariantId, l.Name))
            .Select(g =>
            {
                decimal Bucket(int from, int to) => g.Where(l => (now - l.ReceivedAtUtc).Days >= from && (now - l.ReceivedAtUtc).Days <= to).Sum(l => l.RemainingQuantity);
                return new StockAgeingDto(g.Key.VariantId, g.Key.Name, Bucket(0, 30), Bucket(31, 60), Bucket(61, 90), Bucket(91, int.MaxValue),
                    g.Sum(l => StockMath.Value(l.RemainingQuantity, l.UnitCost)));
            })
            .OrderBy(a => a.VariantName)
            .ToList();
    }

    /// <summary>Total stock value of each store the user can see.</summary>
    public async Task<IReadOnlyList<StockValuationDto>> ValuationAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAnyScopeAsync(Permissions.StockView, businessId, cancellationToken).ConfigureAwait(false);
        var method = await ValuationMethodAsync(businessId, cancellationToken).ConfigureAwait(false);
        var stores = await db.Stores.AsNoTracking().Where(s => s.BusinessId == businessId).OrderBy(s => s.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<StockValuationDto>();
        foreach (var store in stores)
        {
            if (!await access.HasPermissionAsync(Permissions.StockView, businessId, store.Id, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var balances = await db.StockBalances.AsNoTracking().Where(b => b.StoreId == store.Id && b.Quantity != 0).ToListAsync(cancellationToken).ConfigureAwait(false);
            var layerValues = await LayerValuesAsync(store.Id, cancellationToken).ConfigureAwait(false);
            result.Add(new StockValuationDto(store.Id, store.Name, method, balances.Count, balances.Count(b => b.Quantity < 0),
                balances.Sum(b => Valuation(method, b, layerValues.GetValueOrDefault(b.VariantId)))));
        }

        return result;
    }

    public async Task<IReadOnlyList<StockDocumentSummaryDto>> DocumentsAsync(Guid businessId, Guid storeId, string? type, CancellationToken cancellationToken)
    {
        await RequireStoreAsync(Permissions.StockView, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var query = db.StockDocuments.AsNoTracking().Where(d => d.StoreId == storeId || d.TargetStoreId == storeId);
        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(d => d.Type == type);
        }

        return await (
                from d in query
                join u in db.Users.AsNoTracking() on d.PostedByUserId equals u.Id
                orderby d.PostedAtUtc descending
                select new StockDocumentSummaryDto(d.Id, d.Type, d.Number, d.StoreId, d.TargetStoreId, d.BusinessDate, d.Reason, u.DisplayName, d.PostedAtUtc))
            .Take(200)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StockDocumentDto> DocumentAsync(Guid businessId, Guid documentId, CancellationToken cancellationToken)
    {
        var document = await db.StockDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.NotFound("Stock document");
        var visible = await access.HasPermissionAsync(Permissions.StockView, businessId, document.StoreId, cancellationToken).ConfigureAwait(false)
            || (document.TargetStoreId is { } target && await access.HasPermissionAsync(Permissions.StockView, businessId, target, cancellationToken).ConfigureAwait(false));
        if (!visible)
        {
            throw AppException.NotFound("Stock document");
        }

        return await InventoryQueries.DocumentAsync(db, documentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InventorySettingsDto> SettingsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAnyScopeAsync(Permissions.StockView, businessId, cancellationToken).ConfigureAwait(false);
        var settings = await db.InventorySettings.AsNoTracking().FirstOrDefaultAsync(s => s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Inventory settings");
        return new InventorySettingsDto(settings.ValuationMethod, await AnyMovementAsync(businessId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<InventorySettingsDto> UpdateSettingsAsync(Guid businessId, UpdateInventorySettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.StockSettings, businessId, null, cancellationToken).ConfigureAwait(false);
        var settings = await db.InventorySettings.FirstOrDefaultAsync(s => s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Inventory settings");
        var from = settings.ValuationMethod;
        settings.ChangeValuationMethod(request.ValuationMethod, await AnyMovementAsync(businessId, cancellationToken).ConfigureAwait(false));
        audit.Record("stock.valuation_changed", "inventory_settings", businessId, businessId, details: new { from, to = settings.ValuationMethod });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new InventorySettingsDto(settings.ValuationMethod, false);
    }

    public async Task<IReadOnlyList<NegativeStockRuleDto>> NegativeRulesAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAnyScopeAsync(Permissions.StockView, businessId, cancellationToken).ConfigureAwait(false);
        return await (
                from r in db.NegativeStockRules.AsNoTracking()
                join u in db.Users.AsNoTracking() on r.CreatedByUserId equals u.Id
                where r.BusinessId == businessId
                orderby r.CreatedAtUtc descending
                select new NegativeStockRuleDto(r.Id, r.StoreId, r.ProductId, r.Mode, r.LimitQuantity, r.Reason, u.DisplayName, r.CreatedAtUtc, r.IsActive,
                    r.ApprovalRequestId, r.SupersededAtUtc))
            .Take(500)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the negative-stock rule for a scope. Tightening applies at once; loosening (allowing more negative stock than
    /// now applies at that scope) needs independent approval, waived only when nobody else could approve.
    /// </summary>
    public async Task<SetNegativeStockRuleResponse> SetNegativeRuleAsync(Guid businessId, SetNegativeStockRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.StockSettings, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        if (request.StoreId is { } s && !await db.Stores.AnyAsync(x => x.Id == s && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Store");
        }

        if (request.ProductId is { } p && !await db.Products.AnyAsync(x => x.Id == p && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Product");
        }

        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // One change per business at a time, so two people cannot both loosen against the same "current" rule.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"negative_stock|" + businessId}))", cancellationToken).ConfigureAwait(false);
        var rules = await db.NegativeStockRules.Where(r => r.BusinessId == businessId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var sameScope = rules.Where(r => r.StoreId == request.StoreId && r.ProductId == request.ProductId).ToList();
        if (sameScope.Any(r => !r.IsActive && r.SupersededAtUtc is null))
        {
            throw AppException.Conflict("negative_stock.pending", "A change to this negative-stock rule is already waiting for approval.");
        }

        var current = Inherited(rules, request.StoreId, request.ProductId);
        var loosening = IsLoosening(current, request.Mode, request.LimitQuantity);
        var waived = loosening && !await AnyOtherApproverAsync(businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        var needsApproval = loosening && !waived;

        var rule = NegativeStockRule.Create(businessId, request.StoreId, request.ProductId, request.Mode, request.LimitQuantity, request.Reason,
            currentUser.UserId, null, active: !needsApproval, now);
        if (!needsApproval)
        {
            // The old rule must be inactive in the database before the new one is inserted (one active rule per scope).
            foreach (var old in sameScope.Where(r => r.IsActive))
            {
                old.Supersede(now);
            }

            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        }

        db.NegativeStockRules.Add(rule);

        Guid? approvalId = null;
        string message;
        if (needsApproval)
        {
            var approval = ApprovalRequest.Create(businessId, NegativeStockApprovalType, await DescribeAsync(rule, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new NegativeStockPayload(rule.Id, rule.StoreId), UserAdminService.Json), rule.Reason, currentUser.UserId, now,
                TimeSpan.FromDays(options.Value.ApprovalLifetimeDays));
            db.ApprovalRequests.Add(approval);
            approvalId = approval.Id;
            audit.Record("approval.requested", "approval_request", approval.Id, businessId, request.StoreId, details: new { approval.Type, approval.Summary });
            message = "Allowing more negative stock needs approval by another manager. The current rule stays in force until then.";
        }
        else
        {
            message = waived
                ? "Rule is active. Nobody else could approve it, so the approval was waived and recorded."
                : "Rule is active.";
        }

        audit.Record("negative_stock.rule_set", "negative_stock_rule", rule.Id, businessId, request.StoreId, details: new
        {
            rule.ProductId,
            from = current.Mode,
            fromLimit = current.Limit,
            to = rule.Mode,
            toLimit = rule.LimitQuantity,
            rule.Reason,
            approval = waived ? "waived_no_other_approver" : needsApproval ? "pending" : "not_required",
        });

        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SetNegativeStockRuleResponse(needsApproval ? "pending_approval" : "active", rule.Id, approvalId, message);
    }

    public async Task SetReorderLevelAsync(Guid businessId, SetReorderLevelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.StockAdjust, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        if (!await db.Stores.AnyAsync(x => x.Id == request.StoreId && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Store");
        }

        if (!await db.ProductVariants.AnyAsync(x => x.Id == request.VariantId && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Item");
        }

        var level = await db.ReorderLevels.FirstOrDefaultAsync(r => r.StoreId == request.StoreId && r.VariantId == request.VariantId, cancellationToken)
            .ConfigureAwait(false);
        if (level is null)
        {
            level = ReorderLevel.Create(businessId, request.StoreId, request.VariantId, request.MinimumQuantity, request.ReorderQuantity, clock.GetUtcNow());
            db.ReorderLevels.Add(level);
        }
        else
        {
            level.Update(request.MinimumQuantity, request.ReorderQuantity);
        }

        audit.Record("stock.reorder_level_set", "reorder_level", level.Id, businessId, request.StoreId,
            details: new { request.VariantId, level.MinimumQuantity, level.ReorderQuantity });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The rule that applies at a scope from the rules covering it (the same scope or broader).</summary>
    internal static NegativeStockPolicy Inherited(IEnumerable<NegativeStockRule> rules, Guid? storeId, Guid? productId)
    {
        var rule = rules
            .Where(r => r.IsActive && (r.StoreId is null || r.StoreId == storeId) && (r.ProductId is null || r.ProductId == productId))
            .OrderByDescending(r => r.Specificity)
            .ThenByDescending(r => r.CreatedAtUtc)
            .FirstOrDefault();
        return rule is null ? NegativeStockPolicy.Default : new NegativeStockPolicy(rule.Mode, rule.LimitQuantity);
    }

    internal static bool IsLoosening(NegativeStockPolicy current, string mode, decimal? limit)
    {
        var from = NegativeStockModes.Strictness(current.Mode);
        var to = NegativeStockModes.Strictness(mode);
        if (to != from)
        {
            return to < from;
        }

        return mode == NegativeStockModes.EnabledWithLimit && (limit ?? 0) > (current.Limit ?? 0);
    }

    internal static bool IsEligibleApprover(IReadOnlyCollection<ActiveGrant> grants, Guid businessId, Guid? storeId) =>
        AccessControl.Covers(grants, Permissions.ApprovalsDecide, businessId, null)
        && AccessControl.Covers(grants, Permissions.StockSettings, businessId, storeId);

    internal async Task<string> DescribeAsync(NegativeStockRule rule, CancellationToken cancellationToken)
    {
        var store = rule.StoreId is { } s ? await db.Stores.AsNoTracking().Where(x => x.Id == s).Select(x => x.Name).FirstAsync(cancellationToken).ConfigureAwait(false) : "all stores";
        var product = rule.ProductId is { } p ? await db.Products.AsNoTracking().Where(x => x.Id == p).Select(x => x.Name).FirstAsync(cancellationToken).ConfigureAwait(false) : "all products";
        var mode = rule.Mode switch
        {
            NegativeStockModes.Disabled => "not allowed",
            NegativeStockModes.WarnWithOverride => "allowed with manager override",
            _ => $"allowed down to -{rule.LimitQuantity}",
        };
        return $"Negative stock {mode} for {product} in {store}";
    }

    private async Task<bool> AnyOtherApproverAsync(Guid businessId, Guid? storeId, CancellationToken cancellationToken) =>
        (await ApprovalService.OtherUsersGrantsAsync(db, businessId, [currentUser.UserId], cancellationToken).ConfigureAwait(false))
            .Any(g => IsEligibleApprover(g, businessId, storeId));

    private Task<bool> AnyMovementAsync(Guid businessId, CancellationToken cancellationToken) =>
        db.StockLedger.AnyAsync(e => e.BusinessId == businessId, cancellationToken);

    private async Task<string> ValuationMethodAsync(Guid businessId, CancellationToken cancellationToken) =>
        await db.InventorySettings.AsNoTracking().Where(s => s.BusinessId == businessId).Select(s => s.ValuationMethod)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? ValuationMethods.Fifo;

    private async Task<DateOnly> TodayAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var timeZone = await db.Stores.AsNoTracking().Where(s => s.Id == storeId).Select(s => s.TimeZone).FirstAsync(cancellationToken).ConfigureAwait(false);
        return BusinessCalendar.Today(clock, timeZone);
    }

    /// <summary>Value of the open cost layers per item: the FIFO/FEFO value of what is on the shelf.</summary>
    private async Task<Dictionary<Guid, decimal>> LayerValuesAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var layers = await db.CostLayers.AsNoTracking().Where(l => l.StoreId == storeId && l.RemainingQuantity > 0)
            .Select(l => new { l.VariantId, l.RemainingQuantity, l.UnitCost })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return layers.GroupBy(l => l.VariantId).ToDictionary(g => g.Key, g => g.Sum(l => StockMath.Value(l.RemainingQuantity, l.UnitCost)));
    }

    /// <summary>
    /// FIFO/FEFO: the open layers at their own costs. Weighted average: quantity at the average cost.
    /// Negative stock is valued at the last cost (a liability to be covered by the next receipt).
    /// </summary>
    private static decimal Valuation(string method, StockBalance balance, decimal layerValue) =>
        balance.Quantity < 0
            ? StockMath.Value(balance.Quantity, balance.LastCost)
            : method == ValuationMethods.WeightedAverage
                ? StockMath.Value(balance.Quantity, balance.AverageCost)
                : layerValue;

    private Task RequireStoreAsync(string permission, Guid businessId, Guid storeId, CancellationToken cancellationToken) =>
        organisation.RequireAsync(permission, businessId, storeId, cancellationToken);

    private async Task RequireAnyScopeAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        var businesses = await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false);
        if (!businesses.Contains(businessId))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Reads a posted stock document with its movements.</summary>
internal static class InventoryQueries
{
    public static async Task<StockDocumentDto> DocumentAsync(SupermarketBillingDbContext db, Guid documentId, CancellationToken cancellationToken)
    {
        var header = await (
                from d in db.StockDocuments.AsNoTracking()
                join u in db.Users.AsNoTracking() on d.PostedByUserId equals u.Id
                where d.Id == documentId
                select new { Document = d, PostedBy = u.DisplayName })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var movements = await (
                from e in db.StockLedger.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on e.VariantId equals v.Id
                from bt in db.Batches.AsNoTracking().Where(bt => bt.Id == e.BatchId).DefaultIfEmpty()
                where e.DocumentId == documentId
                orderby e.Sequence
                select new StockMovementDto(e.Sequence, e.StoreId, e.VariantId, v.Name, bt == null ? null : bt.BatchNumber, e.MovementType, e.Quantity,
                    e.UnitCost, e.Value, e.BalanceAfter))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var doc = header.Document;
        return new StockDocumentDto(doc.Id, doc.Type, doc.Number, doc.StoreId, doc.TargetStoreId, doc.BusinessDate, doc.Reason, doc.Note,
            doc.NegativeStockOverride, header.PostedBy, doc.PostedAtUtc, movements);
    }
}

/// <summary>Approving a looser negative-stock rule: needs approvals and stock settings for the rule's scope.</summary>
internal sealed class NegativeStockApprovalHandler(SupermarketBillingDbContext db, AuditRecorder audit) : IApprovalHandler
{
    public string Type => InventoryService.NegativeStockApprovalType;

    public bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, Guid actorUserId, ApprovalRequest request) =>
        InventoryService.IsEligibleApprover(actorGrants, request.BusinessId, Payload(request).StoreId);

    public async Task ApplyAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rule = await RuleAsync(request, cancellationToken).ConfigureAwait(false);
        var previous = await db.NegativeStockRules
            .Where(r => r.BusinessId == rule.BusinessId && r.StoreId == rule.StoreId && r.ProductId == rule.ProductId && r.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var old in previous)
        {
            old.Supersede(now);
        }

        // The old rule must be inactive in the database before the new one becomes active (one active rule per scope).
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        rule.Activate(request.Id);
        audit.Record("negative_stock.rule_activated", "negative_stock_rule", rule.Id, rule.BusinessId, rule.StoreId,
            details: new { rule.ProductId, rule.Mode, rule.LimitQuantity, approval = request.Id });
    }

    public async Task ClosedWithoutApprovalAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rule = await RuleAsync(request, cancellationToken).ConfigureAwait(false);
        rule.Supersede(now);
    }

    private async Task<NegativeStockRule> RuleAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        await db.NegativeStockRules.FirstOrDefaultAsync(r => r.Id == Payload(request).RuleId, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");

    private static NegativeStockPayload Payload(ApprovalRequest request) =>
        JsonSerializer.Deserialize<NegativeStockPayload>(request.PayloadJson, UserAdminService.Json)
        ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");
}
