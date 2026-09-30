namespace SupermarketBilling.Application.Contracts;

public sealed record UnitDto(Guid Id, string Code, string Name, int DecimalPlaces, bool IsActive);

public sealed record CreateUnitRequest(string Code, string Name, int DecimalPlaces);

public sealed record CategoryDto(Guid Id, Guid? ParentId, string Name, bool IsActive);

public sealed record CreateCategoryRequest(string Name, Guid? ParentId);

public sealed record BrandDto(Guid Id, string Name, bool IsActive);

public sealed record CreateBrandRequest(string Name);

public sealed record CustomerGroupDto(Guid Id, string Code, string Name, bool IsActive);

public sealed record CreateCustomerGroupRequest(string Code, string Name);

public sealed record ProductSummaryDto(
    Guid Id, string Code, string Name, string? CategoryName, string? BrandName, string HsnSac, string SupplyType,
    decimal GstRatePercent, int VariantCount, bool IsActive);

public sealed record ProductDetailDto(
    Guid Id, string Code, string Name, string PrintName, Guid? CategoryId, Guid? BrandId, Guid BaseUnitId, string BaseUnitCode,
    string HsnSac, string SupplyType, decimal GstRatePercent, decimal CessRatePercent,
    bool IsWeighed, bool TracksBatches, bool TracksExpiry, bool TracksSerials, bool IsActive, uint RowVersion,
    IReadOnlyList<VariantDto> Variants);

public sealed record VariantDto(
    Guid Id, string Code, string Name, bool IsActive,
    IReadOnlyList<VariantUnitDto> Units, IReadOnlyList<BarcodeDto> Barcodes, IReadOnlyList<MrpDto> Mrps);

public sealed record VariantUnitDto(Guid Id, Guid UnitId, string UnitCode, decimal FactorToBase, bool IsBase);

public sealed record BarcodeDto(Guid Id, Guid VariantUnitId, string Code, string Type, bool IsActive);

public sealed record MrpDto(Guid Id, Guid VariantUnitId, decimal Mrp, DateOnly EffectiveFrom, bool IsActive);

/// <param name="Variant">The first sellable variant; defaults to the product's own code and name.</param>
public sealed record CreateProductRequest(
    string Code, string Name, string? PrintName, Guid? CategoryId, Guid? BrandId, Guid BaseUnitId,
    string HsnSac, string SupplyType, decimal GstRatePercent, decimal CessRatePercent,
    bool IsWeighed, bool TracksBatches, bool TracksExpiry, bool TracksSerials,
    CreateVariantRequest? Variant = null);

public sealed record UpdateProductRequest(
    string Name, string? PrintName, Guid? CategoryId, Guid? BrandId, string HsnSac, string SupplyType,
    decimal GstRatePercent, decimal CessRatePercent, bool IsWeighed, bool TracksBatches, bool TracksExpiry, bool TracksSerials,
    bool IsActive, uint RowVersion);

/// <param name="Barcode">Optional barcode for the base unit.</param>
/// <param name="Mrp">Optional MRP for the base unit.</param>
public sealed record CreateVariantRequest(string? Code, string? Name, string? Barcode = null, decimal? Mrp = null);

public sealed record AddVariantUnitRequest(Guid UnitId, decimal FactorToBase);

public sealed record AddBarcodeRequest(Guid VariantUnitId, string Code);

public sealed record AddMrpRequest(Guid VariantUnitId, decimal Mrp, DateOnly? EffectiveFrom);

/// <summary>What a scanned barcode identifies: the variant, the pack, and the MRPs in use.</summary>
public sealed record BarcodeLookupDto(
    Guid ProductId, string ProductName, string PrintName, Guid VariantId, string VariantName, Guid VariantUnitId, string UnitCode,
    decimal FactorToBase, string Barcode, string SupplyType, decimal GstRatePercent, decimal CessRatePercent, bool IsWeighed,
    IReadOnlyList<decimal> Mrps);

public sealed record PriceRuleDto(
    Guid Id, Guid VariantId, Guid VariantUnitId, string UnitCode, string RateType, string Channel, decimal Price, bool TaxInclusive,
    decimal? Mrp, Guid? StoreId, Guid? CustomerGroupId, bool MembersOnly, decimal MinQuantity, decimal? MaxQuantity,
    DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, int Priority, string Status, string? Note,
    DateTimeOffset CreatedAtUtc, Guid? ApprovalRequestId, DateTimeOffset? RetiredAtUtc);

public sealed record CreatePriceRuleRequest(
    Guid VariantUnitId, string RateType, string Channel, decimal Price, bool TaxInclusive, decimal? Mrp, Guid? StoreId,
    Guid? CustomerGroupId, bool MembersOnly, decimal MinQuantity, decimal? MaxQuantity, DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ValidToUtc, int? Priority, string? Note);

public sealed record CreatePriceRuleResponse(PriceRuleDto Rule, Guid? ApprovalRequestId, string Message);

public sealed record PriceQuoteDto(
    Guid? RuleId, string? RateType, decimal? UnitPrice, bool TaxInclusive, decimal? UnitPriceInclusive,
    decimal? MinimumPriceInclusive, bool BelowMinimum, bool AboveMrp, decimal TaxRatePercent);

public sealed record TaxRegistrationDto(
    Guid Id, string Mode, DateOnly EffectiveFrom, string? Gstin, string Reason, string? EvidenceReference,
    string RecordedBy, Guid? ApprovalRequestId, DateTimeOffset RecordedAtUtc, bool IsCurrent);

/// <param name="BackupConfirmed">The person requesting confirms a verified backup was taken today (spec requirement).</param>
public sealed record TaxRegistrationChangeRequest(
    string Mode, string? Gstin, DateOnly EffectiveFrom, string Reason, string? EvidenceReference, bool BackupConfirmed);

public sealed record TaxRegistrationChangeResponse(Guid ApprovalRequestId, string Message);
