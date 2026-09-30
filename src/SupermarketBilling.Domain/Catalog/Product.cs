using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Catalog;

/// <summary>GST nature of a supply. Decided by the product and the law, never by how the stock was purchased.</summary>
public static class SupplyTypes
{
    public const string Taxable = "TAXABLE";
    public const string Exempt = "EXEMPT";
    public const string NilRated = "NIL_RATED";
    public const string NonGst = "NON_GST";

    public static readonly IReadOnlyList<string> All = [Taxable, Exempt, NilRated, NonGst];
}

/// <summary>
/// A product as the business knows it (for example "Aashirvaad Whole Wheat Atta"), carrying tax classification.
/// What is actually sold, scanned and priced is a <see cref="ProductVariant"/>.
/// </summary>
public sealed partial class Product : ITenantOwned
{
    private Product()
    {
        Code = Name = PrintName = HsnSac = SupplyType = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    /// <summary>Short name for receipts (max 40 characters).</summary>
    public string PrintName { get; private set; }

    public Guid? CategoryId { get; private set; }

    public Guid? BrandId { get; private set; }

    /// <summary>The unit stock is counted in (every pack converts to it).</summary>
    public Guid BaseUnitId { get; private set; }

    public string HsnSac { get; private set; }

    public string SupplyType { get; private set; }

    /// <summary>Total GST rate in percent (CGST+SGST or IGST). Zero unless taxable.</summary>
    public decimal GstRatePercent { get; private set; }

    public decimal CessRatePercent { get; private set; }

    /// <summary>Sold by weight from a scale (weight-embedded barcodes, fractional quantities).</summary>
    public bool IsWeighed { get; private set; }

    public bool TracksBatches { get; private set; }

    public bool TracksExpiry { get; private set; }

    public bool TracksSerials { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static Product Create(
        Guid businessId, string code, string name, string? printName, Guid? categoryId, Guid? brandId, Guid baseUnitId,
        string hsnSac, string supplyType, decimal gstRatePercent, decimal cessRatePercent,
        bool isWeighed, bool tracksBatches, bool tracksExpiry, bool tracksSerials, DateTimeOffset now)
    {
        var product = new Product
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Code = ValidateCode(code, "product.code_invalid"),
            BaseUnitId = baseUnitId,
            IsActive = true,
            CreatedAtUtc = now,
        };
        product.Update(name, printName, categoryId, brandId, hsnSac, supplyType, gstRatePercent, cessRatePercent, isWeighed, tracksBatches, tracksExpiry, tracksSerials, isActive: true);
        return product;
    }

    /// <summary>
    /// Updates the master data. Past invoices are unaffected: each invoice line keeps its own copy of the tax
    /// classification and rate used at the time of sale.
    /// </summary>
    public void Update(
        string name, string? printName, Guid? categoryId, Guid? brandId, string hsnSac, string supplyType,
        decimal gstRatePercent, decimal cessRatePercent, bool isWeighed, bool tracksBatches, bool tracksExpiry, bool tracksSerials, bool isActive)
    {
        Name = Business.Required(name, "product.name_required", "Product name is required (max 150 characters).", 150);
        PrintName = string.IsNullOrWhiteSpace(printName)
            ? (Name.Length > 40 ? Name[..40] : Name)
            : Business.Required(printName, "product.print_name_invalid", "Receipt name must be at most 40 characters.", 40);
        CategoryId = categoryId;
        BrandId = brandId;
        HsnSac = ValidateHsnSac(hsnSac);
        SupplyType = SupplyTypes.All.Contains(supplyType)
            ? supplyType
            : throw new DomainException("product.supply_type_invalid", $"Unknown supply type '{supplyType}'.");
        (GstRatePercent, CessRatePercent) = ValidateRates(SupplyType, gstRatePercent, cessRatePercent);
        IsWeighed = isWeighed;
        TracksBatches = tracksBatches || tracksExpiry; // expiry is recorded per batch
        TracksExpiry = tracksExpiry;
        TracksSerials = tracksSerials;
        IsActive = isActive;
    }

    internal static string ValidateCode(string code, string errorCode)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            throw new DomainException(errorCode, "Codes must be 1-30 letters, digits, hyphens, dots or slashes.");
        }

        return normalized;
    }

    private static string ValidateHsnSac(string hsnSac)
    {
        var normalized = (hsnSac ?? string.Empty).Trim();
        if (!HsnPattern().IsMatch(normalized))
        {
            throw new DomainException("product.hsn_invalid", "HSN/SAC must be 4, 6 or 8 digits.");
        }

        return normalized;
    }

    private static (decimal Gst, decimal Cess) ValidateRates(string supplyType, decimal gst, decimal cess)
    {
        if (gst < 0 || cess < 0 || gst > 100 || cess > 400 || decimal.Round(gst, 3) != gst || decimal.Round(cess, 3) != cess)
        {
            throw new DomainException("product.rate_invalid", "Tax rates must be between 0 and 100 percent (cess up to 400) with at most 3 decimals.");
        }

        if (supplyType == SupplyTypes.Taxable && gst == 0)
        {
            throw new DomainException("product.rate_required", "A taxable product needs a GST rate. Use Exempt or Nil-rated for 0%.");
        }

        if (supplyType != SupplyTypes.Taxable && (gst != 0 || cess != 0))
        {
            throw new DomainException("product.rate_not_allowed", "Exempt, nil-rated and non-GST products carry no GST or cess.");
        }

        return (gst, cess);
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9./-]{0,29}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^([0-9]{4}|[0-9]{6}|[0-9]{8})$")]
    private static partial Regex HsnPattern();
}

/// <summary>A sellable item of a product (for example "5 kg bag"). Stock, barcodes, MRPs and prices belong here.</summary>
public sealed class ProductVariant : ITenantOwned
{
    private ProductVariant()
    {
        Code = Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>SKU, unique within the business.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public static ProductVariant Create(Guid businessId, Guid productId, string code, string name, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        ProductId = productId,
        Code = Product.ValidateCode(code, "variant.code_invalid"),
        Name = Business.Required(name, "variant.name_required", "Variant name is required (max 150 characters).", 150),
        IsActive = true,
    };

    public void Update(string name, bool isActive)
    {
        Name = Business.Required(name, "variant.name_required", "Variant name is required (max 150 characters).", 150);
        IsActive = isActive;
    }
}

/// <summary>
/// A unit a variant is bought or sold in, with its conversion to the product's base unit
/// (for example BOX = 12 PCS, or BAG = 25 KG). Exactly one unit per variant is the base unit (factor 1).
/// </summary>
public sealed class VariantUnit : ITenantOwned
{
    public const int FactorScale = 6;

    private VariantUnit()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid UnitId { get; private set; }

    /// <summary>How many base units one of this unit contains.</summary>
    public decimal FactorToBase { get; private set; }

    public bool IsBase { get; private set; }

    public bool IsActive { get; private set; }

    public static VariantUnit Create(Guid businessId, Guid variantId, Guid unitId, decimal factorToBase, bool isBase, DateTimeOffset now)
    {
        if (factorToBase <= 0 || decimal.Round(factorToBase, FactorScale) != factorToBase)
        {
            throw new DomainException("variant_unit.factor_invalid", "The conversion factor must be positive with at most 6 decimals.");
        }

        if (isBase && factorToBase != 1)
        {
            throw new DomainException("variant_unit.base_factor", "The base unit's factor must be 1.");
        }

        return new VariantUnit
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            VariantId = variantId,
            UnitId = unitId,
            FactorToBase = factorToBase,
            IsBase = isBase,
            IsActive = true,
        };
    }

    /// <summary>Converts a quantity in this unit to base units, exactly (no floating point).</summary>
    public decimal ToBase(decimal quantity) => quantity * FactorToBase;
}

public static class BarcodeTypes
{
    /// <summary>GS1 barcodes: EAN-8, UPC-A (12), EAN-13, GTIN-14. The check digit is validated.</summary>
    public const string Gs1 = "GS1";

    /// <summary>Codes the store prints itself.</summary>
    public const string Internal = "INTERNAL";
}

/// <summary>A barcode that identifies one pack (unit) of a variant. Unique within the business.</summary>
public sealed partial class VariantBarcode : ITenantOwned
{
    private VariantBarcode()
    {
        Code = Type = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid VariantUnitId { get; private set; }

    public string Code { get; private set; }

    public string Type { get; private set; }

    public bool IsActive { get; private set; }

    public static VariantBarcode Create(Guid businessId, Guid variantId, Guid variantUnitId, string code, DateTimeOffset now)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        string type;
        if (Gs1Pattern().IsMatch(normalized))
        {
            if (!HasValidGs1CheckDigit(normalized))
            {
                throw new DomainException("barcode.check_digit", $"{normalized} is not a valid EAN/UPC barcode (wrong check digit). Re-scan it.");
            }

            type = BarcodeTypes.Gs1;
        }
        else if (InternalPattern().IsMatch(normalized) && !normalized.All(char.IsAsciiDigit))
        {
            type = BarcodeTypes.Internal;
        }
        else
        {
            throw new DomainException("barcode.invalid", "A barcode must be an EAN-8/UPC-A/EAN-13/GTIN-14 number or an internal code of 3-20 letters and digits.");
        }

        return new VariantBarcode
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            VariantId = variantId,
            VariantUnitId = variantUnitId,
            Code = normalized,
            Type = type,
            IsActive = true,
        };
    }

    public void Deactivate() => IsActive = false;

    /// <summary>GS1 mod-10: weights 3 and 1 alternate from the rightmost data digit.</summary>
    public static bool HasValidGs1CheckDigit(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            var weight = (digits.Length - 2 - i) % 2 == 0 ? 3 : 1;
            sum += (digits[i] - '0') * weight;
        }

        return (10 - (sum % 10)) % 10 == digits[^1] - '0';
    }

    [GeneratedRegex("^([0-9]{8}|[0-9]{12,14})$")]
    private static partial Regex Gs1Pattern();

    [GeneratedRegex("^[A-Z0-9]{3,20}$")]
    private static partial Regex InternalPattern();
}

/// <summary>
/// A maximum retail price printed on a pack. A variant can have several at once (older stock at an older MRP), so
/// MRPs are never overwritten; a new MRP is a new row.
/// </summary>
public sealed class VariantMrp : ITenantOwned
{
    private VariantMrp()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid VariantUnitId { get; private set; }

    public decimal Mrp { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public bool IsActive { get; private set; }

    public static VariantMrp Create(Guid businessId, Guid variantId, Guid variantUnitId, decimal mrp, DateOnly effectiveFrom, DateTimeOffset now)
    {
        if (mrp <= 0 || decimal.Round(mrp, 2) != mrp)
        {
            throw new DomainException("mrp.invalid", "MRP must be a positive amount in rupees and paise.");
        }

        return new VariantMrp
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            VariantId = variantId,
            VariantUnitId = variantUnitId,
            Mrp = mrp,
            EffectiveFrom = effectiveFrom,
            IsActive = true,
        };
    }

    public void Deactivate() => IsActive = false;
}
