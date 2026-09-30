using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.Domain.Tax;

/// <summary>
/// Validates a GST Identification Number: 2-digit state code, 10-character PAN, entity number, 'Z', and a
/// mod-36 check character.
/// </summary>
public static partial class Gstin
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToUpperInvariant();
    }

    public static bool IsValid(string? value)
    {
        if (value is null || !Shape().IsMatch(value))
        {
            return false;
        }

        return value[14] == ComputeCheckCharacter(value.AsSpan(0, 14));
    }

    /// <summary>Appends the correct check character to the first 14 characters of a GSTIN.</summary>
    public static string Complete(string first14)
    {
        ArgumentNullException.ThrowIfNull(first14);
        return first14.Length == 14 ? first14 + ComputeCheckCharacter(first14) : throw new ArgumentException("Expected 14 characters.", nameof(first14));
    }

    /// <summary>The first two digits: the GST state code (for example 33 = Tamil Nadu).</summary>
    public static string StateCode(string gstin) => gstin[..2];

    public static string Validate(string value, string expectedStateCode)
    {
        var normalized = Normalize(value);
        if (!IsValid(normalized))
        {
            throw new DomainException("gstin.invalid", $"'{normalized}' is not a valid GSTIN (format or check digit is wrong).");
        }

        if (StateCode(normalized) != expectedStateCode)
        {
            throw new DomainException(
                "gstin.state_mismatch",
                $"GSTIN {normalized} belongs to state {StateCode(normalized)}, but the state code entered is {expectedStateCode}.");
        }

        return normalized;
    }

    internal static char ComputeCheckCharacter(ReadOnlySpan<char> first14)
    {
        var sum = 0;
        for (var i = 0; i < first14.Length; i++)
        {
            var value = Alphabet.IndexOf(first14[i], StringComparison.Ordinal);
            var product = value * ((i % 2) + 1);
            sum += (product / 36) + (product % 36);
        }

        return Alphabet[(36 - (sum % 36)) % 36];
    }

    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$")]
    private static partial Regex Shape();
}
