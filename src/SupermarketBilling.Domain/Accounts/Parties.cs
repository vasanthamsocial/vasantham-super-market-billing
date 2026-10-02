using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Accounts;

/// <summary>
/// What suppliers and debtors have in common (spec section 13): names, GST identity, contacts, the numbers and
/// consent for WhatsApp and SMS, and the credit period. Balances are never stored here; they come from the ledgers.
/// </summary>
public sealed partial record PartyDetails(
    string LegalName,
    string? TradeName,
    string? Gstin,
    string StateCode,
    string? Address,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? WhatsAppNumber,
    string? SmsNumber,
    bool WhatsAppConsent,
    bool SmsConsent,
    int CreditPeriodDays)
{
    public const int MaxCreditPeriodDays = 365;

    /// <summary>Checks and tidies the details; throws a <see cref="DomainException"/> naming the first problem.</summary>
    public PartyDetails Normalize(string kind)
    {
        var legal = Business.Required(LegalName, $"{kind}.name_required", $"Give the {kind}'s legal name (max 200 characters).", 200);
        var state = (StateCode ?? string.Empty).Trim() is { Length: 2 } s && s.All(char.IsAsciiDigit)
            ? s
            : throw new DomainException($"{kind}.state_invalid", $"The {kind}'s state is a two-digit GST state code.");
        var whatsApp = Mobile(WhatsAppNumber, $"{kind}.whatsapp_invalid", "The WhatsApp number");
        var sms = Mobile(SmsNumber, $"{kind}.sms_invalid", "The SMS number");
        if (WhatsAppConsent && whatsApp is null)
        {
            throw new DomainException($"{kind}.whatsapp_required", "Consent to WhatsApp messages needs a WhatsApp number.");
        }

        if (SmsConsent && sms is null)
        {
            throw new DomainException($"{kind}.sms_required", "Consent to SMS needs an SMS number.");
        }

        var email = Optional(Email, 200);
        if (email is not null && !EmailPattern().IsMatch(email))
        {
            throw new DomainException($"{kind}.email_invalid", "The email address is not valid.");
        }

        return new PartyDetails(
            legal,
            Optional(TradeName, 200),
            string.IsNullOrWhiteSpace(Gstin) ? null : Tax.Gstin.Validate(Gstin, state),
            state,
            Optional(Address, 500),
            Optional(ContactPerson, 100),
            Optional(Phone, 20),
            email,
            whatsApp,
            sms,
            WhatsAppConsent,
            SmsConsent,
            CreditPeriodDays is >= 0 and <= MaxCreditPeriodDays
                ? CreditPeriodDays
                : throw new DomainException($"{kind}.credit_period_invalid", $"A credit period is 0 to {MaxCreditPeriodDays} days."));
    }

    /// <summary>An Indian mobile number as +91 and ten digits (starting 6-9), or null when empty.</summary>
    public static string? Mobile(string? value, string code, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (value.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-' or '+' or '(' or ')')))
        {
            digits = string.Empty;
        }

        digits = digits.Length switch
        {
            12 when digits.StartsWith("91", StringComparison.Ordinal) => digits[2..],
            11 when digits.StartsWith('0') => digits[1..],
            _ => digits,
        };
        return digits.Length == 10 && digits[0] is >= '6' and <= '9'
            ? "+91" + digits
            : throw new DomainException(code, $"{what} must be an Indian mobile number (10 digits starting 6-9).");
    }

    private static string? Optional(string? value, int max) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var v && v.Length <= max ? v : throw new DomainException("party.text_too_long", $"'{v[..20]}...' is longer than {max} characters.");

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}

public static class DebtorStatus
{
    /// <summary>Can buy on credit (within the limit).</summary>
    public const string Active = "ACTIVE";

    /// <summary>No new credit sales; cash sales and payments continue.</summary>
    public const string OnHold = "ON_HOLD";

    /// <summary>Account closed (only with nothing outstanding); can be reopened.</summary>
    public const string Closed = "CLOSED";

    public static readonly IReadOnlyList<string> All = [Active, OnHold, Closed];
}

/// <summary>A customer who may buy on credit (spec sections 13-14). The balance comes from the debtor ledger.</summary>
public sealed partial class Debtor : ITenantOwned
{
    private Debtor()
    {
        Code = LegalName = StateCode = Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Code { get; private set; }

    public string LegalName { get; private set; }

    public string? TradeName { get; private set; }

    public string? Gstin { get; private set; }

    public string StateCode { get; private set; }

    public string? Address { get; private set; }

    public string? ContactPerson { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? WhatsAppNumber { get; private set; }

    public string? SmsNumber { get; private set; }

    public bool WhatsAppConsent { get; private set; }

    public bool SmsConsent { get; private set; }

    /// <summary>When either consent last changed (consent is evidence for messaging later).</summary>
    public DateTimeOffset? ConsentChangedAtUtc { get; private set; }

    /// <summary>Days allowed after an invoice date. Each credit invoice keeps the due date it was given.</summary>
    public int CreditPeriodDays { get; private set; }

    /// <summary>The most the debtor may owe; 0 means no credit (cash only).</summary>
    public decimal CreditLimit { get; private set; }

    public Guid? CustomerGroupId { get; private set; }

    public string Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public string DisplayName => TradeName ?? LegalName;

    public static Debtor Create(Guid businessId, string code, PartyDetails details, decimal creditLimit, Guid? customerGroupId, DateTimeOffset now)
    {
        var debtor = new Debtor
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Code = Codes.Validate(code, "debtor"),
            Status = DebtorStatus.Active,
            CreatedAtUtc = now,
        };
        debtor.Update(details, creditLimit, customerGroupId, now);
        return debtor;
    }

    public void Update(PartyDetails details, decimal creditLimit, Guid? customerGroupId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        var d = details.Normalize("debtor");
        if (creditLimit < 0 || decimal.Round(creditLimit, 2) != creditLimit)
        {
            throw new DomainException("debtor.credit_limit_invalid", "A credit limit is zero or more, in rupees and paise.");
        }

        if (d.WhatsAppConsent != WhatsAppConsent || d.SmsConsent != SmsConsent)
        {
            ConsentChangedAtUtc = now;
        }

        (LegalName, TradeName, Gstin, StateCode, Address, ContactPerson, Phone, Email) =
            (d.LegalName, d.TradeName, d.Gstin, d.StateCode, d.Address, d.ContactPerson, d.Phone, d.Email);
        (WhatsAppNumber, SmsNumber, WhatsAppConsent, SmsConsent, CreditPeriodDays) = (d.WhatsAppNumber, d.SmsNumber, d.WhatsAppConsent, d.SmsConsent, d.CreditPeriodDays);
        CreditLimit = creditLimit;
        CustomerGroupId = customerGroupId;
    }

    /// <param name="balance">The current ledger balance: an account can only be closed with nothing owed either way.</param>
    public void SetStatus(string status, decimal balance)
    {
        if (!DebtorStatus.All.Contains(status))
        {
            throw new DomainException("debtor.status_invalid", $"Unknown account status '{status}'.");
        }

        if (status == DebtorStatus.Closed && balance != 0)
        {
            throw new DomainException("debtor.balance_not_zero", $"The account still has a balance of Rs. {balance:0.00}; it can be closed only at zero.");
        }

        Status = status;
    }
}

internal static partial class Codes
{
    public static string Validate(string code, string kind) =>
        (code ?? string.Empty).Trim().ToUpperInvariant() is var c && Pattern().IsMatch(c)
            ? c
            : throw new DomainException($"{kind}.code_invalid", $"A {kind} code is 1-20 letters, digits or hyphens.");

    [GeneratedRegex("^[A-Z0-9-]{1,20}$")]
    private static partial Regex Pattern();
}
