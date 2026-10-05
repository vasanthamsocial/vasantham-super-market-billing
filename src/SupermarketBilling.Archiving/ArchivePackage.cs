using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupermarketBilling.Archiving;

/// <summary>A package that cannot be trusted or opened, with why.</summary>
public sealed class ArchivePackageException(string message) : Exception(message);

/// <summary>One kind of record in a package: how many, their SHA-256, and totals of named amount columns.</summary>
public sealed record ArchiveDataset(string Name, long Records, string Sha256, IReadOnlyDictionary<string, decimal> Totals);

/// <summary>What a package holds: the business, the month, where it came from, and its datasets.</summary>
public sealed record ArchiveManifest(
    int Format, Guid PackageId, Guid BusinessId, string BusinessCode, string BusinessName, string Month, DateTimeOffset CreatedAtUtc,
    Guid SourceInstallationId, IReadOnlyList<ArchiveDataset> Datasets);

/// <summary>
/// The readable outside of a package: enough to identify it, check who signed it and for whom it was encrypted, before
/// opening it. The signature covers all of it, including the hash of the encrypted content.
/// </summary>
public sealed record ArchiveHeader(
    int Format, Guid PackageId, Guid BusinessId, string BusinessCode, string Month, DateTimeOffset CreatedAtUtc, string SignerKeyId, string RecipientKeyId,
    string EphemeralPublicKey, string Nonce, string Tag, string ManifestSha256, string CiphertextSha256);

/// <summary>A dataset to put in a package: its rows as JSON (one per line) and the columns whose sums are recorded.</summary>
public sealed record ArchiveDatasetInput(string Name, IReadOnlyList<string> Rows, IReadOnlyList<string> TotalColumns);

/// <summary>An opened, verified package: the manifest and each dataset's rows.</summary>
public sealed record ArchiveContents(ArchiveHeader Header, ArchiveManifest Manifest, IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> Datasets);

/// <summary>
/// The monthly archive package (spec section 22, D-040). A zip of <c>manifest.json</c> and one JSON-lines file per
/// dataset, encrypted for the archive server (ECDH P-256 with an ephemeral key, HKDF-SHA256, AES-256-GCM) and signed by
/// the store server (ECDSA P-256, SHA-256). Layout: the line <c>SBARC1</c>, the header (JSON), the signature (base64),
/// then the ciphertext. Opening checks, in order: a trusted signer, the signature, the ciphertext hash, the decryption,
/// the manifest hash, then every dataset's SHA-256, record count and totals.
/// </summary>
public static class ArchivePackage
{
    public const int Format = 1;
    private static readonly byte[] Magic = "SBARC1\n"u8.ToArray();
    private static readonly byte[] KeyInfo = "SBARC1 package key"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Write(
        ArchiveManifest manifestDraft, IReadOnlyList<ArchiveDatasetInput> datasets, ECDsa signer, ECDiffieHellman recipient, out ArchiveManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifestDraft);
        ArgumentNullException.ThrowIfNull(datasets);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(recipient);

        // The plaintext: the datasets as JSON lines, then the manifest describing them.
        var files = new List<(string Name, byte[] Bytes)>();
        var described = new List<ArchiveDataset>();
        foreach (var dataset in datasets)
        {
            var bytes = Lines(dataset.Rows);
            files.Add((dataset.Name + ".jsonl", bytes));
            described.Add(new ArchiveDataset(dataset.Name, dataset.Rows.Count, Hex(SHA256.HashData(bytes)), Totals(dataset.Rows.Select(Parse), dataset.TotalColumns)));
        }

        manifest = manifestDraft with { Format = Format, Datasets = described };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var plaintext = Zip(files.Prepend(("manifest.json", manifestBytes)));

        // Encrypted for the archive server only.
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var key = Key(ephemeral.DeriveRawSecretAgreement(recipient.PublicKey), manifest.PackageId);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, manifest.PackageId.ToByteArray());
        }

        var header = new ArchiveHeader(Format, manifest.PackageId, manifest.BusinessId, manifest.BusinessCode, manifest.Month, manifest.CreatedAtUtc,
            KeyId(signer.ExportSubjectPublicKeyInfo()), KeyId(recipient.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Hex(SHA256.HashData(manifestBytes)), Hex(SHA256.HashData(ciphertext)));
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(signer.SignData(headerBytes, HashAlgorithmName.SHA256)));

        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(headerBytes);
        output.WriteByte((byte)'\n');
        output.Write(signature);
        output.WriteByte((byte)'\n');
        output.Write(ciphertext);
        return output.ToArray();
    }

    /// <summary>The header, without opening (or trusting) the package.</summary>
    public static ArchiveHeader ReadHeader(byte[] file) => Split(file).Header;

    /// <summary>Verifies and opens a package. <paramref name="trustedSigner"/> gives the public key of a trusted store server by key id, or null.</summary>
    public static ArchiveContents Open(byte[] file, ECDiffieHellman recipient, Func<string, ECDsa?> trustedSigner)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(trustedSigner);
        var (header, headerBytes, signature, ciphertext) = Split(file);
        var signer = trustedSigner(header.SignerKeyId)
            ?? throw new ArchivePackageException($"The package was signed by a store server this archive does not trust (key {header.SignerKeyId}).");
        if (!signer.VerifyData(headerBytes, signature, HashAlgorithmName.SHA256))
        {
            throw new ArchivePackageException("The package's signature is not valid: it was changed after it was made, or not made by that store server.");
        }

        if (header.RecipientKeyId != KeyId(recipient.ExportSubjectPublicKeyInfo()))
        {
            throw new ArchivePackageException("The package was encrypted for another archive.");
        }

        if (Hex(SHA256.HashData(ciphertext)) != header.CiphertextSha256)
        {
            throw new ArchivePackageException("The package's content does not match its checksum.");
        }

        byte[] plaintext;
        try
        {
            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportSubjectPublicKeyInfo(Convert.FromBase64String(header.EphemeralPublicKey), out _);
            var key = Key(recipient.DeriveRawSecretAgreement(ephemeral.PublicKey), header.PackageId);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(header.Nonce), ciphertext, Convert.FromBase64String(header.Tag), plaintext, header.PackageId.ToByteArray());
        }
        catch (CryptographicException e)
        {
            throw new ArchivePackageException($"The package cannot be decrypted: {e.Message}");
        }

        var files = Unzip(plaintext);
        var manifestBytes = files.GetValueOrDefault("manifest.json") ?? throw new ArchivePackageException("The package has no manifest.");
        if (Hex(SHA256.HashData(manifestBytes)) != header.ManifestSha256)
        {
            throw new ArchivePackageException("The manifest does not match the signed header.");
        }

        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(manifestBytes, Json)!;
        if (manifest.PackageId != header.PackageId || manifest.BusinessId != header.BusinessId || manifest.Month != header.Month)
        {
            throw new ArchivePackageException("The manifest and the header describe different packages.");
        }

        var datasets = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal);
        foreach (var dataset in manifest.Datasets)
        {
            var bytes = files.GetValueOrDefault(dataset.Name + ".jsonl") ?? throw new ArchivePackageException($"Dataset {dataset.Name} is missing.");
            if (Hex(SHA256.HashData(bytes)) != dataset.Sha256)
            {
                throw new ArchivePackageException($"Dataset {dataset.Name} does not match its checksum.");
            }

            var rows = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList();
            if (rows.Count != dataset.Records)
            {
                throw new ArchivePackageException($"Dataset {dataset.Name} has {rows.Count} records, not {dataset.Records}.");
            }

            var totals = Totals(rows, dataset.Totals.Keys.ToList());
            foreach (var (column, expected) in dataset.Totals)
            {
                if (totals[column] != expected)
                {
                    throw new ArchivePackageException($"Dataset {dataset.Name}: {column} adds up to {totals[column]}, not {expected}.");
                }
            }

            datasets[dataset.Name] = rows;
        }

        return new ArchiveContents(header, manifest, datasets);
    }

    /// <summary>A short id for a public key (SubjectPublicKeyInfo): the first 16 hex digits of its SHA-256.</summary>
    public static string KeyId(byte[] subjectPublicKeyInfo) => Hex(SHA256.HashData(subjectPublicKeyInfo))[..16];

    /// <summary>The SHA-256 of a whole package file (what the store server records and the archive confirms).</summary>
    public static string FileSha256(byte[] file) => Hex(SHA256.HashData(file));

    private static (ArchiveHeader Header, byte[] HeaderBytes, byte[] Signature, byte[] Ciphertext) Split(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < Magic.Length || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new ArchivePackageException("This is not an archive package (.sbarc).");
        }

        var headerEnd = Array.IndexOf(file, (byte)'\n', Magic.Length);
        var signatureEnd = headerEnd < 0 ? -1 : Array.IndexOf(file, (byte)'\n', headerEnd + 1);
        if (signatureEnd < 0)
        {
            throw new ArchivePackageException("The package is incomplete.");
        }

        var headerBytes = file[Magic.Length..headerEnd];
        try
        {
            var header = JsonSerializer.Deserialize<ArchiveHeader>(headerBytes, Json) ?? throw new ArchivePackageException("The package header is empty.");
            if (header.Format != Format)
            {
                throw new ArchivePackageException($"Package format {header.Format} is not supported (this version reads format {Format}).");
            }

            return (header, headerBytes, Convert.FromBase64String(Encoding.ASCII.GetString(file[(headerEnd + 1)..signatureEnd])), file[(signatureEnd + 1)..]);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw new ArchivePackageException($"The package header cannot be read: {e.Message}");
        }
    }

    private static byte[] Key(byte[] secret, Guid packageId) => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, packageId.ToByteArray(), KeyInfo);

    private static byte[] Lines(IEnumerable<string> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            if (row.Contains('\n', StringComparison.Ordinal))
            {
                throw new ArgumentException("A row must be one line of JSON.", nameof(rows));
            }

            text.Append(row).Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static JsonElement Parse(string row)
    {
        using var document = JsonDocument.Parse(row);
        return document.RootElement.Clone();
    }

    private static Dictionary<string, decimal> Totals(IEnumerable<JsonElement> rows, IReadOnlyList<string> columns)
    {
        var totals = columns.ToDictionary(c => c, _ => 0m, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var column in columns)
            {
                if (row.TryGetProperty(column, out var value) && value.ValueKind == JsonValueKind.Number)
                {
                    totals[column] += decimal.Parse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }
        }

        return totals;
    }

    private static byte[] Zip(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    private static Dictionary<string, byte[]> Unzip(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > 512L * 1024 * 1024)
                {
                    throw new ArchivePackageException($"{entry.FullName} is too large.");
                }

                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                files[entry.FullName] = copy.ToArray();
            }

            return files;
        }
        catch (InvalidDataException e)
        {
            throw new ArchivePackageException($"The package content is damaged: {e.Message}");
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
