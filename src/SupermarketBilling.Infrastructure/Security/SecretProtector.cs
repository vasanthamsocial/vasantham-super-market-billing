using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SupermarketBilling.Infrastructure.Security;

/// <summary>
/// Encrypts small secrets stored in the database (for example MFA secrets) with AES-256-GCM.
/// Format: <c>v1.</c> + base64(nonce | ciphertext | tag). The purpose string is bound as associated data, so a
/// value encrypted for one purpose cannot be substituted for another.
/// <para>
/// The key lives in the environment (.env / service configuration), not the database, so a copy of the database
/// or a backup alone does not reveal these secrets. Restoring on a new server needs the same key.
/// </para>
/// </summary>
public sealed class SecretProtector
{
    private const string Version = "v1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(IOptions<SecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.DataProtectionKey;
        byte[]? key = null;
        try
        {
            key = string.IsNullOrWhiteSpace(configured) ? null : Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            // Reported below.
        }

        if (key is not { Length: 32 })
        {
            throw new InvalidOperationException(
                "Security:DataProtectionKey must be a base64-encoded 32-byte key. Run scripts/setup-dev.ps1 to generate one.");
        }

        _key = key;
    }

    public string Protect(byte[] plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(NonceSize, plaintext.Length),
            output.AsSpan(NonceSize + plaintext.Length, TagSize),
            Encoding.UTF8.GetBytes(purpose));
        nonce.CopyTo(output, 0);
        return Version + Convert.ToBase64String(output);
    }

    public byte[] Unprotect(string protectedValue, string purpose)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        if (!protectedValue.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unsupported protected value format.");
        }

        var data = Convert.FromBase64String(protectedValue[Version.Length..]);
        if (data.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected value is too short.");
        }

        var plaintextLength = data.Length - NonceSize - TagSize;
        var plaintext = new byte[plaintextLength];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Decrypt(
            data.AsSpan(0, NonceSize),
            data.AsSpan(NonceSize, plaintextLength),
            data.AsSpan(NonceSize + plaintextLength, TagSize),
            plaintext,
            Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }
}
