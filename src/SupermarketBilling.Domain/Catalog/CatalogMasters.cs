using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Catalog;

/// <summary>A unit of measure (PCS, KG, L, BOX ...) within a business.</summary>
public sealed partial class Unit : ITenantOwned
{
    private Unit()
    {
        Code = Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    /// <summary>How many decimal places a quantity in this unit may have (0 for pieces, 3 for kilograms).</summary>
    public int DecimalPlaces { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Units every new business starts with.</summary>
    public static IReadOnlyList<(string Code, string Name, int DecimalPlaces)> Defaults { get; } =
    [
        ("PCS", "Pieces", 0), ("KG", "Kilogram", 3), ("G", "Gram", 0), ("L", "Litre", 3), ("ML", "Millilitre", 0),
        ("M", "Metre", 2), ("BOX", "Box", 0), ("PKT", "Packet", 0), ("DZN", "Dozen", 0), ("BAG", "Bag", 0),
    ];

    public static Unit Create(Guid businessId, string code, string name, int decimalPlaces, DateTimeOffset now)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            throw new DomainException("unit.code_invalid", "Unit code must be 1-10 letters or digits.");
        }

        if (decimalPlaces is < 0 or > 3)
        {
            throw new DomainException("unit.decimals_invalid", "A unit may have 0 to 3 decimal places.");
        }

        return new Unit
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Code = normalized,
            Name = Business.Required(name, "unit.name_required", "Unit name is required (max 50 characters).", 50),
            DecimalPlaces = decimalPlaces,
            IsActive = true,
        };
    }

    /// <summary>Rejects quantities with more decimal places than the unit allows (for example 1.5 PCS).</summary>
    public void ValidateQuantity(decimal quantity)
    {
        if (decimal.Round(quantity, DecimalPlaces) != quantity)
        {
            throw new DomainException("quantity.too_precise", $"{Code} quantities may have at most {DecimalPlaces} decimal places.");
        }
    }

    [GeneratedRegex("^[A-Z0-9]{1,10}$")]
    private static partial Regex CodePattern();
}

public sealed class Category : ITenantOwned
{
    private Category()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid? ParentId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public static Category Create(Guid businessId, Guid? parentId, string name, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        ParentId = parentId,
        Name = Business.Required(name, "category.name_required", "Category name is required (max 80 characters).", 80),
        IsActive = true,
    };

    public void Rename(string name) => Name = Business.Required(name, "category.name_required", "Category name is required (max 80 characters).", 80);

    public void SetActive(bool isActive) => IsActive = isActive;
}

public sealed class Brand : ITenantOwned
{
    private Brand()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public static Brand Create(Guid businessId, string name, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        Name = Business.Required(name, "brand.name_required", "Brand name is required (max 80 characters).", 80),
        IsActive = true,
    };
}

/// <summary>A group of customers with their own prices (for example "Hotels" or "Retailers - Route 3").</summary>
public sealed partial class CustomerGroup : ITenantOwned
{
    private CustomerGroup()
    {
        Code = Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public static CustomerGroup Create(Guid businessId, string code, string name, DateTimeOffset now)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            throw new DomainException("customer_group.code_invalid", "Group code must be 2-20 letters, digits or hyphens.");
        }

        return new CustomerGroup
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Code = normalized,
            Name = Business.Required(name, "customer_group.name_required", "Group name is required (max 80 characters).", 80),
            IsActive = true,
        };
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,19}$")]
    private static partial Regex CodePattern();
}
