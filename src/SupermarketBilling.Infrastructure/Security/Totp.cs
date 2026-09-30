using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SupermarketBilling.Infrastructure.Security;

/// <summary>
/// Time-based one-time passwords (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits), compatible with Google
/// Authenticator, Microsoft Authenticator and similar apps. Works fully offline.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 TOTP is defined over HMAC-SHA1 and authenticator apps expect it; HMAC-SHA1 is not affected by SHA-1 collision attacks.")]
    public static string Compute(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = (BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFFFFFF) % 1_000_000;
        return binary.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Accepts the code for the current step or one step either side (clock drift), but never a step at or before
    /// <paramref name="lastUsedStep"/>, so an intercepted code cannot be replayed.
    /// </summary>
    /// <returns>The matched step, or null.</returns>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var digits = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (lastUsedStep is { } last && step <= last)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, step)), Encoding.ASCII.GetBytes(digits)))
            {
                return step;
            }
        }

        return null;
    }

    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    public static byte[] Base32Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var value = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"'{c}' is not a Base32 character.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }

    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 / 5) + 1);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }
}
