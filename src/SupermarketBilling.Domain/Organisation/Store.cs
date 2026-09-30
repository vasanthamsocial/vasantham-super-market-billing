using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.Domain.Organisation;

/// <summary>A physical store (branch) of a business. Counters, stock and shifts belong to a store.</summary>
public sealed partial class Store
{
    public const string DefaultTimeZone = "Asia/Kolkata";

    private Store()
    {
        Code = Name = StateCode = TimeZone = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    /// <summary>Code unique within the business, for example <c>MAIN</c> or <c>S02</c>.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public string StateCode { get; private set; }

    /// <summary>Separate GSTIN when the store is registered in a different state from the business.</summary>
    public string? Gstin { get; private set; }

    public string? Address { get; private set; }

    /// <summary>IANA time zone used to derive business dates (invoice date, shift date).</summary>
    public string TimeZone { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static Store Create(Guid businessId, string code, string name, string stateCode, string? gstin, string? address, DateTimeOffset now)
    {
        var store = new Store
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Code = ValidateCode(code),
            TimeZone = DefaultTimeZone,
            IsActive = true,
            CreatedAtUtc = now,
        };
        store.Update(name, stateCode, gstin, address, isActive: true);
        return store;
    }

    public void Update(string name, string stateCode, string? gstin, string? address, bool isActive)
    {
        Name = Business.Required(name, "store.name_required", "Store name is required (max 120 characters).", 120);
        StateCode = Business.ValidateStateCode(stateCode);
        Gstin = string.IsNullOrWhiteSpace(gstin) ? null : Tax.Gstin.Validate(gstin, StateCode);
        Address = string.IsNullOrWhiteSpace(address) ? null : Business.Required(address, "store.address_invalid", "Address is too long.", 500);
        IsActive = isActive;
    }

    public static string ValidateCode(string code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            throw new DomainException("store.code_invalid", "Store code must be 1-12 letters, digits or hyphens.");
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,11}$")]
    private static partial Regex CodePattern();
}
