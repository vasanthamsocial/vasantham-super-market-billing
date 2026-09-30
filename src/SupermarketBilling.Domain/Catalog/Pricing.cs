using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Catalog;

public static class RateTypes
{
    public const string Standard = "STANDARD";
    public const string StoreSpecific = "STORE";
    public const string QuantitySlab = "QUANTITY_SLAB";
    public const string Member = "MEMBER";
    public const string CustomerGroup = "CUSTOMER_GROUP";
    public const string Promotional = "PROMOTIONAL";

    /// <summary>A floor, not a selling price: selling below it needs an authorised override.</summary>
    public const string MinimumSellingPrice = "MINIMUM";

    public static readonly IReadOnlyList<string> All = [Standard, StoreSpecific, QuantitySlab, Member, CustomerGroup, Promotional, MinimumSellingPrice];

    /// <summary>Default priority: more specific offers win over general prices.</summary>
    public static int DefaultPriority(string rateType) => rateType switch
    {
        Standard => 10,
        StoreSpecific => 20,
        QuantitySlab => 30,
        Member => 40,
        CustomerGroup => 50,
        Promotional => 60,
        _ => 0,
    };
}

public static class SalesChannels
{
    public const string Retail = "RETAIL";
    public const string Wholesale = "WHOLESALE";
    public const string Any = "ANY";

    public static readonly IReadOnlyList<string> All = [Retail, Wholesale, Any];
}

public static class PriceRuleStatus
{
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Active = "ACTIVE";
    public const string Rejected = "REJECTED";
    public const string Retired = "RETIRED";
}

/// <summary>
/// One price for one pack of a variant, with the conditions under which it applies. A rule's price and conditions
/// never change after creation: a new price is a new rule and the old one is retired. Invoice lines store the rule
/// id they were priced with, so an invoice can always show where its price came from.
/// </summary>
public sealed class PriceRule : ITenantOwned
{
    private PriceRule()
    {
        RateType = Channel = Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid VariantUnitId { get; private set; }

    public string RateType { get; private set; }

    public string Channel { get; private set; }

    public decimal Price { get; private set; }

    /// <summary>True when <see cref="Price"/> includes GST and cess (normal for retail and MRP goods).</summary>
    public bool TaxInclusive { get; private set; }

    /// <summary>Applies only to stock carrying this MRP (null: any MRP).</summary>
    public decimal? Mrp { get; private set; }

    public Guid? StoreId { get; private set; }

    public Guid? CustomerGroupId { get; private set; }

    public bool MembersOnly { get; private set; }

    public decimal MinQuantity { get; private set; }

    public decimal? MaxQuantity { get; private set; }

    public DateTimeOffset ValidFromUtc { get; private set; }

    public DateTimeOffset? ValidToUtc { get; private set; }

    public int Priority { get; private set; }

    public string Status { get; private set; }

    public string? Note { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Guid? ApprovalRequestId { get; private set; }

    public Guid? RetiredByUserId { get; private set; }

    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public bool IsMinimum => RateType == RateTypes.MinimumSellingPrice;

    public int Specificity =>
        (StoreId is null ? 0 : 1) + (CustomerGroupId is null ? 0 : 1) + (MembersOnly ? 1 : 0) +
        (MinQuantity > 0 || MaxQuantity is not null ? 1 : 0) + (Mrp is null ? 0 : 1);

    public static PriceRule Create(
        Guid businessId, Guid variantId, Guid variantUnitId, string rateType, string channel, decimal price, bool taxInclusive,
        decimal? mrp, Guid? storeId, Guid? customerGroupId, bool membersOnly, decimal minQuantity, decimal? maxQuantity,
        DateTimeOffset validFromUtc, DateTimeOffset? validToUtc, int? priority, string? note, bool requiresApproval,
        Guid createdBy, DateTimeOffset now)
    {
        if (!RateTypes.All.Contains(rateType))
        {
            throw new DomainException("price.rate_type_invalid", $"Unknown rate type '{rateType}'.");
        }

        if (!SalesChannels.All.Contains(channel))
        {
            throw new DomainException("price.channel_invalid", $"Unknown sales channel '{channel}'.");
        }

        if (price <= 0 || decimal.Round(price, 4) != price)
        {
            throw new DomainException("price.invalid", "Price must be positive with at most 4 decimals.");
        }

        if (mrp is { } m && (m <= 0 || decimal.Round(m, 2) != m))
        {
            throw new DomainException("mrp.invalid", "MRP must be a positive amount in rupees and paise.");
        }

        if (minQuantity < 0 || (maxQuantity is { } max && max <= minQuantity))
        {
            throw new DomainException("price.quantity_range_invalid", "The quantity range is invalid (maximum must be above minimum).");
        }

        if (validToUtc is { } to && to <= validFromUtc)
        {
            throw new DomainException("price.validity_invalid", "The end of validity must be after its start.");
        }

        switch (rateType)
        {
            case RateTypes.StoreSpecific when storeId is null:
                throw new DomainException("price.store_required", "A store price needs a store.");
            case RateTypes.CustomerGroup when customerGroupId is null:
                throw new DomainException("price.group_required", "A customer-group price needs a customer group.");
            case RateTypes.Member when !membersOnly:
                throw new DomainException("price.members_only", "A member price must be limited to members.");
            case RateTypes.QuantitySlab when minQuantity == 0 && maxQuantity is null:
                throw new DomainException("price.slab_required", "A quantity-slab price needs a quantity range.");
            case RateTypes.Promotional when validToUtc is null:
                throw new DomainException("price.promotion_end_required", "A promotional price needs an end date.");
        }

        return new PriceRule
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            VariantId = variantId,
            VariantUnitId = variantUnitId,
            RateType = rateType,
            Channel = channel,
            Price = price,
            TaxInclusive = taxInclusive,
            Mrp = mrp,
            StoreId = storeId,
            CustomerGroupId = customerGroupId,
            MembersOnly = membersOnly,
            MinQuantity = minQuantity,
            MaxQuantity = maxQuantity,
            ValidFromUtc = validFromUtc,
            ValidToUtc = validToUtc,
            Priority = priority ?? RateTypes.DefaultPriority(rateType),
            Status = requiresApproval ? PriceRuleStatus.PendingApproval : PriceRuleStatus.Active,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedByUserId = createdBy,
            CreatedAtUtc = now,
        };
    }

    /// <summary>Selling above MRP is not allowed. MRP includes all taxes, so exclusive prices are grossed up first.</summary>
    public void EnsureNotAboveMrp(decimal mrp, decimal taxRatePercent)
    {
        if (!IsMinimum && PriceMath.InclusiveOf(Price, TaxInclusive, taxRatePercent) > mrp)
        {
            throw new DomainException("price.above_mrp", $"The price ({PriceMath.InclusiveOf(Price, TaxInclusive, taxRatePercent):0.00} including tax) is above the MRP ({mrp:0.00}).");
        }
    }

    public void Activate(Guid? approvalRequestId)
    {
        RequireStatus(PriceRuleStatus.PendingApproval);
        Status = PriceRuleStatus.Active;
        ApprovalRequestId = approvalRequestId;
    }

    public void Reject(Guid? approvalRequestId)
    {
        RequireStatus(PriceRuleStatus.PendingApproval);
        Status = PriceRuleStatus.Rejected;
        ApprovalRequestId = approvalRequestId;
    }

    public void Retire(Guid retiredBy, DateTimeOffset now)
    {
        if (Status is not (PriceRuleStatus.Active or PriceRuleStatus.PendingApproval))
        {
            throw new DomainException("price.not_retirable", $"This price is already {Status.ToLowerInvariant().Replace('_', ' ')}.");
        }

        Status = PriceRuleStatus.Retired;
        RetiredByUserId = retiredBy;
        RetiredAtUtc = now;
    }

    public bool IsInForceAt(DateTimeOffset at) =>
        Status == PriceRuleStatus.Active && ValidFromUtc <= at && (ValidToUtc is null || at < ValidToUtc);

    private void RequireStatus(string status)
    {
        if (Status != status)
        {
            throw new DomainException("price.status_invalid", $"This price is {Status.ToLowerInvariant().Replace('_', ' ')}.");
        }
    }
}

public static class PriceMath
{
    /// <summary>Tax-inclusive equivalent of a price, rounded to paise (away from zero, as for invoices).</summary>
    public static decimal InclusiveOf(decimal price, bool taxInclusive, decimal taxRatePercent) =>
        taxInclusive ? price : decimal.Round(price * (100 + taxRatePercent) / 100, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Everything that determines which price applies to a sale line.</summary>
public sealed record PriceQuery(
    Guid VariantUnitId,
    decimal Quantity,
    string Channel,
    Guid? StoreId,
    Guid? CustomerGroupId,
    bool IsMember,
    decimal? Mrp,
    decimal TaxRatePercent,
    DateTimeOffset At);

public sealed record PriceQuote(
    PriceRule? Rule,
    decimal? UnitPrice,
    bool TaxInclusive,
    decimal? UnitPriceInclusive,
    decimal? MinimumPriceInclusive,
    bool BelowMinimum,
    bool AboveMrp);

/// <summary>
/// Chooses the price for a sale line from a variant's rules. Among rules in force whose conditions all match, the
/// highest priority wins; ties go to the more specific rule, then the most recently started, then the lower price.
/// The quote also reports whether the price is below the minimum selling price or above the MRP, which the POS must
/// not accept without an authorised decision. Nothing here changes a price silently.
/// </summary>
public static class PriceResolver
{
    public static PriceQuote Resolve(IEnumerable<PriceRule> rules, PriceQuery query)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(query);
        var applicable = rules.Where(r => Applies(r, query)).ToList();

        var floor = applicable.Where(r => r.IsMinimum)
            .Select(r => (decimal?)PriceMath.InclusiveOf(r.Price, r.TaxInclusive, query.TaxRatePercent))
            .Max();

        var chosen = applicable.Where(r => !r.IsMinimum)
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.Specificity)
            .ThenByDescending(r => r.ValidFromUtc)
            .ThenBy(r => PriceMath.InclusiveOf(r.Price, r.TaxInclusive, query.TaxRatePercent))
            .FirstOrDefault();

        if (chosen is null)
        {
            return new PriceQuote(null, null, false, null, floor, false, false);
        }

        var inclusive = PriceMath.InclusiveOf(chosen.Price, chosen.TaxInclusive, query.TaxRatePercent);
        return new PriceQuote(
            chosen,
            chosen.Price,
            chosen.TaxInclusive,
            inclusive,
            floor,
            BelowMinimum: floor is { } f && inclusive < f,
            AboveMrp: query.Mrp is { } mrp && inclusive > mrp);
    }

    private static bool Applies(PriceRule rule, PriceQuery query) =>
        rule.IsInForceAt(query.At)
        && rule.VariantUnitId == query.VariantUnitId
        && (rule.Channel == SalesChannels.Any || rule.Channel == query.Channel)
        && (rule.StoreId is null || rule.StoreId == query.StoreId)
        && (rule.CustomerGroupId is null || rule.CustomerGroupId == query.CustomerGroupId)
        && (!rule.MembersOnly || query.IsMember)
        && query.Quantity >= rule.MinQuantity
        && (rule.MaxQuantity is null || query.Quantity <= rule.MaxQuantity)
        && (rule.Mrp is null || rule.Mrp == query.Mrp);
}
