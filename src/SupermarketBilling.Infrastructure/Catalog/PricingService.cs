using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Catalog;

internal sealed record PriceApprovalPayload(Guid PriceRuleId, Guid? StoreId);

/// <summary>
/// Price rules and price quotes. A new price needs approval when the business requires it, or when it would sell
/// below the pack's current minimum selling price; otherwise it applies immediately. Prices are never edited.
/// </summary>
public sealed class PricingService(
    SupermarketBillingDbContext db,
    CatalogService catalog,
    OrganisationService organisation,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public const string ApprovalType = "price_rule.activate";

    public async Task<IReadOnlyList<PriceRuleDto>> ListAsync(Guid businessId, Guid variantId, bool includeClosed, CancellationToken cancellationToken)
    {
        await catalog.RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.PriceRules.AsNoTracking().Where(r => r.BusinessId == businessId && r.VariantId == variantId);
        if (!includeClosed)
        {
            query = query.Where(r => r.Status == PriceRuleStatus.Active || r.Status == PriceRuleStatus.PendingApproval);
        }

        var rows = await (
                from r in query
                join vu in db.VariantUnits.AsNoTracking() on r.VariantUnitId equals vu.Id
                join u in db.Units.AsNoTracking() on vu.UnitId equals u.Id
                orderby r.Status, r.Priority descending, r.CreatedAtUtc descending
                select new { r, UnitCode = u.Code })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(x => ToDto(x.r, x.UnitCode)).ToList();
    }

    public async Task<CreatePriceRuleResponse> CreateAsync(Guid businessId, Guid variantId, CreatePriceRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.PricesManage, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        var response = await CreateCoreAsync(businessId, variantId, request, currentUser.UserId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Adds a price under the business's approval rules without checking the caller's permission or saving: for a goods
    /// receipt that updates the selling price, whose receiver was checked for price management when it was entered.
    /// </summary>
    internal async Task<CreatePriceRuleResponse> CreateCoreAsync(Guid businessId, Guid variantId, CreatePriceRuleRequest request, Guid createdBy, CancellationToken cancellationToken)
    {
        var pack = await (
                from vu in db.VariantUnits.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on vu.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                join u in db.Units.AsNoTracking() on vu.UnitId equals u.Id
                where vu.Id == request.VariantUnitId && v.Id == variantId && v.BusinessId == businessId
                select new { Variant = v, Product = p, UnitCode = u.Code })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Pack unit");
        await EnsureScopeAsync(businessId, request.StoreId, request.CustomerGroupId, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var taxRate = pack.Product.GstRatePercent + pack.Product.CessRatePercent;
        var business = await db.Businesses.AsNoTracking().FirstAsync(b => b.Id == businessId, cancellationToken).ConfigureAwait(false);

        // Build once without approval to validate and compare against the current floor.
        var probe = PriceRule.Create(businessId, variantId, request.VariantUnitId, request.RateType, request.Channel, request.Price, request.TaxInclusive,
            request.Mrp, request.StoreId, request.CustomerGroupId, request.MembersOnly, request.MinQuantity, request.MaxQuantity,
            request.ValidFromUtc ?? now, request.ValidToUtc, request.Priority, request.Note, requiresApproval: false, createdBy, now);
        await EnsureWithinMrpAsync(probe, taxRate, cancellationToken).ConfigureAwait(false);
        var belowMinimum = !probe.IsMinimum && await IsBelowMinimumAsync(probe, taxRate, now, cancellationToken).ConfigureAwait(false);

        var needsApproval = business.RequirePriceApproval || belowMinimum;
        var waived = false;
        if (needsApproval && !await AnyOtherApproverAsync(businessId, request.StoreId, createdBy, cancellationToken).ConfigureAwait(false))
        {
            if (belowMinimum)
            {
                throw AppException.Conflict("price.below_minimum_needs_approver",
                    "This price is below the minimum selling price and needs approval, but nobody else can approve prices in this business.");
            }

            needsApproval = false;
            waived = true;
        }

        var rule = PriceRule.Create(businessId, variantId, request.VariantUnitId, request.RateType, request.Channel, request.Price, request.TaxInclusive,
            request.Mrp, request.StoreId, request.CustomerGroupId, request.MembersOnly, request.MinQuantity, request.MaxQuantity,
            request.ValidFromUtc ?? now, request.ValidToUtc, request.Priority, request.Note, needsApproval, createdBy, now);
        db.PriceRules.Add(rule);

        Guid? approvalId = null;
        string message;
        if (needsApproval)
        {
            var approval = ApprovalRequest.Create(
                businessId, ApprovalType,
                $"{Describe(rule.RateType)} price {rule.Price:0.00} for {pack.Variant.Name} ({pack.UnitCode}){(belowMinimum ? " - below minimum selling price" : string.Empty)}",
                JsonSerializer.Serialize(new PriceApprovalPayload(rule.Id, rule.StoreId), UserAdminService.Json),
                request.Note, createdBy, now, TimeSpan.FromDays(options.Value.ApprovalLifetimeDays));
            db.ApprovalRequests.Add(approval);
            approvalId = approval.Id;
            audit.Record("approval.requested", "approval_request", approval.Id, businessId, request.StoreId, details: new { approval.Type, approval.Summary, belowMinimum });
            message = belowMinimum
                ? "This price is below the minimum selling price, so it waits for approval before it can be used."
                : "Prices need approval in this business. The price will apply once another authorised person approves it.";
        }
        else
        {
            message = waived ? "Price is active. Nobody else could approve it, so the approval was waived and recorded." : "Price is active.";
        }

        audit.Record("price.created", "price_rule", rule.Id, businessId, request.StoreId, details: new
        {
            variant = pack.Variant.Code, pack = pack.UnitCode, rule.RateType, rule.Channel, rule.Price, rule.TaxInclusive, rule.Mrp,
            rule.StoreId, rule.CustomerGroupId, rule.MembersOnly, rule.MinQuantity, rule.MaxQuantity, rule.ValidFromUtc, rule.ValidToUtc, rule.Status,
            approval = waived ? "waived_no_other_approver" : needsApproval ? "pending" : "not_required",
        });
        return new CreatePriceRuleResponse(ToDto(rule, pack.UnitCode), approvalId, message);
    }

    public async Task RetireAsync(Guid businessId, Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await db.PriceRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Price");
        await organisation.RequireAsync(Permissions.PricesManage, businessId, rule.StoreId, cancellationToken).ConfigureAwait(false);
        rule.Retire(currentUser.UserId, clock.GetUtcNow());
        audit.Record("price.retired", "price_rule", rule.Id, businessId, rule.StoreId, details: new { rule.Price, rule.RateType });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The price that would apply to a sale line right now (or at <paramref name="at"/>).</summary>
    public async Task<PriceQuoteDto> QuoteAsync(
        Guid businessId, Guid variantUnitId, decimal quantity, string channel, Guid? storeId, Guid? customerGroupId, bool isMember, decimal? mrp,
        DateTimeOffset? at, CancellationToken cancellationToken)
    {
        await catalog.RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        var product = await (
                from vu in db.VariantUnits.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on vu.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                where vu.Id == variantUnitId && vu.BusinessId == businessId
                select p)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Pack unit");
        var rules = await db.PriceRules.AsNoTracking()
            .Where(r => r.VariantUnitId == variantUnitId && r.Status == PriceRuleStatus.Active)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var taxRate = product.GstRatePercent + product.CessRatePercent;
        var quote = PriceResolver.Resolve(rules, new PriceQuery(variantUnitId, quantity, channel, storeId, customerGroupId, isMember, mrp, taxRate, at ?? clock.GetUtcNow()));
        return new PriceQuoteDto(quote.Rule?.Id, quote.Rule?.RateType, quote.UnitPrice, quote.TaxInclusive, quote.UnitPriceInclusive,
            quote.MinimumPriceInclusive, quote.BelowMinimum, quote.AboveMrp, taxRate);
    }

    internal static PriceRuleDto ToDto(PriceRule r, string unitCode) =>
        new(r.Id, r.VariantId, r.VariantUnitId, unitCode, r.RateType, r.Channel, r.Price, r.TaxInclusive, r.Mrp, r.StoreId, r.CustomerGroupId,
            r.MembersOnly, r.MinQuantity, r.MaxQuantity, r.ValidFromUtc, r.ValidToUtc, r.Priority, r.Status, r.Note, r.CreatedAtUtc,
            r.ApprovalRequestId, r.RetiredAtUtc);

    private static string Describe(string rateType) => rateType switch
    {
        RateTypes.Standard => "Standard",
        RateTypes.StoreSpecific => "Store",
        RateTypes.QuantitySlab => "Quantity-slab",
        RateTypes.Member => "Member",
        RateTypes.CustomerGroup => "Customer-group",
        RateTypes.Promotional => "Promotional",
        RateTypes.MinimumSellingPrice => "Minimum selling",
        _ => rateType,
    };

    /// <summary>A price may not exceed the MRP it is tied to, nor any active MRP of the pack when it is not tied to one.</summary>
    private async Task EnsureWithinMrpAsync(PriceRule rule, decimal taxRate, CancellationToken cancellationToken)
    {
        if (rule.Mrp is { } mrp)
        {
            rule.EnsureNotAboveMrp(mrp, taxRate);
            return;
        }

        var lowestMrp = await db.VariantMrps.AsNoTracking().Where(m => m.VariantUnitId == rule.VariantUnitId && m.IsActive)
            .Select(m => (decimal?)m.Mrp).MinAsync(cancellationToken).ConfigureAwait(false);
        if (lowestMrp is { } lowest)
        {
            rule.EnsureNotAboveMrp(lowest, taxRate);
        }
    }

    private async Task<bool> IsBelowMinimumAsync(PriceRule rule, decimal taxRate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var floors = await db.PriceRules.AsNoTracking()
            .Where(r => r.VariantUnitId == rule.VariantUnitId && r.Status == PriceRuleStatus.Active && r.RateType == RateTypes.MinimumSellingPrice)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var floor = floors.Where(f => f.IsInForceAt(rule.ValidFromUtc > now ? rule.ValidFromUtc : now) && (f.StoreId is null || f.StoreId == rule.StoreId))
            .Select(f => (decimal?)PriceMath.InclusiveOf(f.Price, f.TaxInclusive, taxRate)).Max();
        return floor is { } f2 && PriceMath.InclusiveOf(rule.Price, rule.TaxInclusive, taxRate) < f2;
    }

    private async Task<bool> AnyOtherApproverAsync(Guid businessId, Guid? storeId, Guid createdBy, CancellationToken cancellationToken) =>
        (await ApprovalService.OtherUsersGrantsAsync(db, businessId, [createdBy], cancellationToken).ConfigureAwait(false))
            .Any(g => PriceApprovalHandler.IsEligible(g, businessId, storeId));

    private async Task EnsureScopeAsync(Guid businessId, Guid? storeId, Guid? customerGroupId, CancellationToken cancellationToken)
    {
        if (storeId is { } s && !await db.Stores.AnyAsync(x => x.Id == s && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Store");
        }

        if (customerGroupId is { } g && !await db.CustomerGroups.AnyAsync(x => x.Id == g && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Customer group");
        }
    }
}

/// <summary>Approving a price: needs approvals and price management for the rule's scope.</summary>
internal sealed class PriceApprovalHandler(SupermarketBillingDbContext db, AuditRecorder audit) : IApprovalHandler
{
    public string Type => PricingService.ApprovalType;

    internal static bool IsEligible(IReadOnlyCollection<ActiveGrant> grants, Guid businessId, Guid? storeId) =>
        AccessControl.Covers(grants, Permissions.ApprovalsDecide, businessId, null)
        && AccessControl.Covers(grants, Permissions.PricesManage, businessId, storeId);

    public bool CanDecide(IReadOnlyCollection<ActiveGrant> actorGrants, Guid actorUserId, ApprovalRequest request) =>
        IsEligible(actorGrants, request.BusinessId, Payload(request).StoreId);

    public async Task ApplyAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rule = await RuleAsync(request, cancellationToken).ConfigureAwait(false);
        rule.Activate(request.Id);
        audit.Record("price.activated", "price_rule", rule.Id, request.BusinessId, rule.StoreId, details: new { rule.Price, approval = request.Id });
    }

    public async Task ClosedWithoutApprovalAsync(ApprovalRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rule = await RuleAsync(request, cancellationToken).ConfigureAwait(false);
        if (rule.Status == PriceRuleStatus.PendingApproval)
        {
            rule.Reject(request.Id);
            audit.Record("price.rejected", "price_rule", rule.Id, request.BusinessId, rule.StoreId, details: new { rule.Price, approval = request.Id });
        }
    }

    private static PriceApprovalPayload Payload(ApprovalRequest request) =>
        JsonSerializer.Deserialize<PriceApprovalPayload>(request.PayloadJson, UserAdminService.Json)
        ?? throw AppException.Validation("approval.payload_invalid", "The approval request is damaged.");

    private async Task<PriceRule> RuleAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        await db.PriceRules.FirstOrDefaultAsync(r => r.Id == Payload(request).PriceRuleId, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.NotFound("Price");
}
