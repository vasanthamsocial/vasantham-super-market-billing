using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Organisation;

/// <summary>A legal business (one PAN/GST registration family). Stores belong to exactly one business.</summary>
public sealed partial class Business : ITenantOwned
{
    private Business()
    {
        Code = LegalName = TradeName = StateCode = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Short unique code, for example <c>SMKT</c>. Used in document number prefixes later.</summary>
    public string Code { get; private set; }

    public string LegalName { get; private set; }

    public string TradeName { get; private set; }

    public string? Gstin { get; private set; }

    /// <summary>Two-digit GST state code of the principal place of business.</summary>
    public string StateCode { get; private set; }

    public string? Address { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>When true, users holding privileged roles in this business must use MFA.</summary>
    public bool RequireMfaForPrivilegedUsers { get; private set; }

    /// <summary>When true, every new price needs a second person's approval before it can be used.</summary>
    public bool RequirePriceApproval { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static Business Create(
        string code, string legalName, string? tradeName, string stateCode, string? gstin, string? address, DateTimeOffset now)
    {
        var business = new Business
        {
            Id = Guid.CreateVersion7(now),
            Code = ValidateCode(code),
            CreatedAtUtc = now,
            IsActive = true,
        };
        business.Update(legalName, tradeName, stateCode, gstin, address, requireMfaForPrivilegedUsers: false);
        return business;
    }

    public void Update(string legalName, string? tradeName, string stateCode, string? gstin, string? address, bool requireMfaForPrivilegedUsers)
    {
        LegalName = Required(legalName, "business.legal_name_required", "Legal name is required.", 200);
        TradeName = string.IsNullOrWhiteSpace(tradeName) ? LegalName : Required(tradeName, "business.trade_name_invalid", "Trade name is too long.", 200);
        StateCode = ValidateStateCode(stateCode);
        Gstin = string.IsNullOrWhiteSpace(gstin) ? null : Tax.Gstin.Validate(gstin, StateCode);
        Address = string.IsNullOrWhiteSpace(address) ? null : Required(address, "business.address_invalid", "Address is too long.", 500);
        RequireMfaForPrivilegedUsers = requireMfaForPrivilegedUsers;
    }

    public void SetPriceApprovalPolicy(bool requirePriceApproval) => RequirePriceApproval = requirePriceApproval;

    public static string ValidateCode(string code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            throw new DomainException("business.code_invalid", "Business code must be 2-12 letters or digits.");
        }

        return normalized;
    }

    public static string ValidateStateCode(string stateCode)
    {
        var normalized = (stateCode ?? string.Empty).Trim();
        if (!StateCodePattern().IsMatch(normalized) || normalized == "00")
        {
            throw new DomainException("state_code.invalid", "State code must be the two-digit GST state code, for example 33.");
        }

        return normalized;
    }

    internal static string Required(string? value, string code, string message, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            throw new DomainException(code, message);
        }

        return trimmed;
    }

    [GeneratedRegex("^[A-Z0-9]{2,12}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[0-9]{2}$")]
    private static partial Regex StateCodePattern();
}
