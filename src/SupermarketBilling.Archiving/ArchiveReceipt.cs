using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupermarketBilling.Archiving;

/// <summary>
/// What an archive receipt states (D-044): the archive server holds this exact package of this business's month
/// (package id and file SHA-256), verified and approved by the accountant and then the owner.
/// </summary>
public sealed record ArchiveReceiptPayload(
    int Version, Guid ReceiptId, Guid PackageId, Guid BusinessId, string BusinessCode, string Month, string FileSha256, long Records,
    DateTimeOffset AccountantApprovedAtUtc, DateTimeOffset OwnerApprovedAtUtc, DateTimeOffset IssuedAtUtc);

/// <summary>
/// A signed archive receipt (a small JSON file): the payload, the archive's receipt key id and an ECDSA P-256 signature
/// over the payload's exact bytes. The store server accepts it only with the archive receipt key it registered, and only
/// then may it remove the month (retention, spec section 22).
/// </summary>
public static class ArchiveReceipt
{
    public const string Format = "sb-archive-receipt/1";
    public const int Version = 1;

    /// <summary>A receipt is a few hundred bytes; anything much larger is not one.</summary>
    public const int MaxSize = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Envelope(string Format, string KeyId, string Payload, string Signature);

    public static byte[] Write(ArchiveReceiptPayload payload, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256);
        var envelope = new Envelope(Format, ArchivePackage.KeyId(key.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(bytes), Convert.ToBase64String(signature));
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Indented);
    }

    /// <summary>The key id the receipt says it was signed with (not yet verified).</summary>
    public static string KeyId(byte[] file) => Open(file).KeyId;

    /// <summary>The payload, if the receipt is well formed and signed by <paramref name="trusted"/>; otherwise an exception saying why.</summary>
    public static ArchiveReceiptPayload Read(byte[] file, ECDsa trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        var envelope = Open(file);
        if (envelope.KeyId != ArchivePackage.KeyId(trusted.ExportSubjectPublicKeyInfo()))
        {
            throw new ArchivePackageException($"The receipt was signed with a key this server has not registered (key {envelope.KeyId}).");
        }

        byte[] payload, signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            throw new ArchivePackageException("The receipt is damaged.");
        }

        if (!trusted.VerifyData(payload, signature, HashAlgorithmName.SHA256))
        {
            throw new ArchivePackageException("The receipt's signature is not valid: it was changed after the archive made it.");
        }

        ArchiveReceiptPayload? result;
        try
        {
            result = JsonSerializer.Deserialize<ArchiveReceiptPayload>(payload, Json);
        }
        catch (JsonException)
        {
            result = null;
        }

        if (result is null || result.Version != Version || result.FileSha256.Length != 64 || result.Month.Length != 7)
        {
            throw new ArchivePackageException("The receipt's content is not understood by this version.");
        }

        return result;
    }

    private static Envelope Open(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length is 0 or > MaxSize)
        {
            throw new ArchivePackageException("This is not an archive receipt.");
        }

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(file), Json);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null || envelope.Format != Format || string.IsNullOrEmpty(envelope.KeyId) || string.IsNullOrEmpty(envelope.Payload)
            || string.IsNullOrEmpty(envelope.Signature))
        {
            throw new ArchivePackageException("This is not an archive receipt.");
        }

        return envelope;
    }
}
