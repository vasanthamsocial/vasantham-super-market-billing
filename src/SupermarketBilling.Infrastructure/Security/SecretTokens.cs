using System.Security.Cryptography;
using System.Text;

namespace SupermarketBilling.Infrastructure.Security;

/// <summary>Random bearer secrets (session, CSRF, reset, recovery codes) and their stored hashes.</summary>
public static class SecretTokens
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>256-bit URL-safe random token.</summary>
    public static string NewToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Human-typeable code such as <c>K7QD-3MXA-9PLT</c> (no 0/O/1/I confusion), for codes read aloud or typed
    /// from paper: password reset codes and MFA recovery codes.
    /// </summary>
    public static string NewHumanCode(int groups = 3, int groupLength = 4)
    {
        var builder = new StringBuilder();
        for (var g = 0; g < groups; g++)
        {
            if (g > 0)
            {
                builder.Append('-');
            }

            for (var i = 0; i < groupLength; i++)
            {
                builder.Append(CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
            }
        }

        return builder.ToString();
    }

    /// <summary>SHA-256 of the token. Tokens are high-entropy, so a fast hash is appropriate (unlike passwords).</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Hash of a human code, ignoring case, spaces and hyphens so "k7qd 3mxa 9plt" matches.</summary>
    public static byte[] HashHumanCode(string code)
    {
        var normalized = new string((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return Hash(normalized);
    }

    public static bool FixedTimeEquals(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
