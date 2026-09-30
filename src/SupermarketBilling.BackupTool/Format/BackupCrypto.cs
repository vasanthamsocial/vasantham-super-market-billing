using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupermarketBilling.BackupTool.Format;

/// <summary>Plain-text file header. Authenticated (as associated data) but not encrypted.</summary>
internal sealed record BackupHeader(
    int Format,
    DateTimeOffset CreatedUtc,
    string Kdf,
    int Iterations,
    string Salt,
    string Cipher,
    int ChunkSize,
    string NoncePrefix,
    string KeyCheck);

/// <summary>
/// Streaming authenticated encryption for backup files.
/// <para>
/// Layout: <c>"SBBAK001"</c> | int32 header length | header JSON | blocks.
/// Each block is: 1-byte final flag | int32 length | AES-256-GCM ciphertext | 16-byte tag.
/// The nonce is a random 7-byte prefix, a 4-byte big-endian block counter and the final flag, so blocks cannot be
/// reordered, dropped or re-flagged without failing authentication, and a file missing its final block is rejected.
/// Every block also authenticates the SHA-256 of the header.
/// </para>
/// The key is derived from the backup passphrase with PBKDF2-SHA256.
/// </summary>
internal static class BackupCrypto
{
    public const int FormatVersion = 1;
    public const string KdfName = "PBKDF2-SHA256";
    public const string CipherName = "AES-256-GCM-STREAM";
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 100_000;
    public const int MaxIterations = 10_000_000;
    public const int DefaultChunkSize = 1024 * 1024;
    public const int MinChunkSize = 1024;
    public const int MaxChunkSize = 64 * 1024 * 1024;
    public const int MinPassphraseLength = 16;

    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int TagSize = 16;
    private const int NoncePrefixSize = 7;
    private const int NonceSize = 12;
    private const int FrameSize = 5;
    private const int MaxHeaderSize = 16 * 1024;

    private static readonly byte[] Magic = "SBBAK001"u8.ToArray();
    private static readonly byte[] KeyCheckLabel = "SupermarketBilling backup key check v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<BackupHeader> EncryptAsync(
        Stream plaintext,
        Stream output,
        string passphrase,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken,
        int chunkSize = DefaultChunkSize,
        int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(output);
        ValidatePassphrase(passphrase);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, MinChunkSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, MaxChunkSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            var header = new BackupHeader(
                FormatVersion,
                createdUtc,
                KdfName,
                iterations,
                Convert.ToBase64String(salt),
                CipherName,
                chunkSize,
                Convert.ToBase64String(noncePrefix),
                ComputeKeyCheck(key));

            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
            var lengthBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, headerBytes.Length);
            await output.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

            var associatedData = HeaderDigest(lengthBytes, headerBytes);
            using var gcm = new AesGcm(key, TagSize);
            var current = new byte[chunkSize];
            var next = new byte[chunkSize];
            var ciphertext = new byte[chunkSize];
            var tag = new byte[TagSize];
            var frame = new byte[FrameSize];

            var currentCount = await ReadFullAsync(plaintext, current, cancellationToken).ConfigureAwait(false);
            uint counter = 0;
            while (true)
            {
                // Read ahead one block so the last block can be flagged as final.
                var nextCount = currentCount == chunkSize
                    ? await ReadFullAsync(plaintext, next, cancellationToken).ConfigureAwait(false)
                    : 0;
                var isFinal = nextCount == 0;

                gcm.Encrypt(
                    BuildNonce(noncePrefix, counter, isFinal),
                    current.AsSpan(0, currentCount),
                    ciphertext.AsSpan(0, currentCount),
                    tag,
                    associatedData);

                frame[0] = isFinal ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), currentCount);
                await output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(ciphertext.AsMemory(0, currentCount), cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);

                if (isFinal)
                {
                    break;
                }

                counter = checked(counter + 1);
                (current, next) = (next, current);
                currentCount = nextCount;
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(current);
            CryptographicOperations.ZeroMemory(next);
            return header;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Decrypts and authenticates a backup, passing each authenticated plaintext block to <paramref name="sink"/>.
    /// Throws <see cref="BackupIntegrityException"/> on any corruption, truncation or modification.
    /// </summary>
    public static async Task<BackupHeader> DecryptAsync(
        Stream input,
        string passphrase,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        var magic = new byte[Magic.Length];
        if (await ReadFullAsync(input, magic, cancellationToken).ConfigureAwait(false) != Magic.Length
            || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new BackupIntegrityException("This is not a SupermarketBilling backup file (unrecognised signature).");
        }

        var lengthBytes = new byte[4];
        await ReadExactAsync(input, lengthBytes, "header length", cancellationToken).ConfigureAwait(false);
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (headerLength <= 0 || headerLength > MaxHeaderSize)
        {
            throw new BackupIntegrityException("The backup header is invalid.");
        }

        var headerBytes = new byte[headerLength];
        await ReadExactAsync(input, headerBytes, "header", cancellationToken).ConfigureAwait(false);
        var header = ParseHeader(headerBytes);
        var salt = DecodeBase64(header.Salt, SaltSize, "salt");
        var noncePrefix = DecodeBase64(header.NoncePrefix, NoncePrefixSize, "nonce prefix");
        var expectedKeyCheck = DecodeBase64(header.KeyCheck, 16, "key check");

        var key = DeriveKey(passphrase, salt, header.Iterations);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(ComputeKeyCheck(key)), expectedKeyCheck))
            {
                throw new BackupPassphraseException("The backup passphrase is incorrect for this backup file.");
            }

            var associatedData = HeaderDigest(lengthBytes, headerBytes);
            using var gcm = new AesGcm(key, TagSize);
            var frame = new byte[FrameSize];
            var ciphertext = new byte[header.ChunkSize];
            var plaintext = new byte[header.ChunkSize];
            var tag = new byte[TagSize];
            uint counter = 0;

            while (true)
            {
                var frameRead = await ReadFullAsync(input, frame, cancellationToken).ConfigureAwait(false);
                if (frameRead == 0)
                {
                    throw new BackupIntegrityException("The backup is truncated: its final block is missing.");
                }

                if (frameRead != FrameSize || frame[0] > 1)
                {
                    throw new BackupIntegrityException($"Backup block {counter} has an invalid frame.");
                }

                var isFinal = frame[0] == 1;
                var length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(1));
                if (length < 0 || length > header.ChunkSize)
                {
                    throw new BackupIntegrityException($"Backup block {counter} has an invalid length.");
                }

                await ReadExactAsync(input, ciphertext.AsMemory(0, length), $"block {counter}", cancellationToken).ConfigureAwait(false);
                await ReadExactAsync(input, tag, $"block {counter} tag", cancellationToken).ConfigureAwait(false);

                try
                {
                    gcm.Decrypt(
                        BuildNonce(noncePrefix, counter, isFinal),
                        ciphertext.AsSpan(0, length),
                        tag,
                        plaintext.AsSpan(0, length),
                        associatedData);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new BackupIntegrityException(
                        $"Backup block {counter} failed authentication: the file is corrupted or has been modified.", ex);
                }

                if (length > 0)
                {
                    await sink(plaintext.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                }

                if (isFinal)
                {
                    break;
                }

                counter = checked(counter + 1);
            }

            CryptographicOperations.ZeroMemory(plaintext);
            var trailing = new byte[1];
            if (await input.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new BackupIntegrityException("The backup has unexpected data after its final block.");
            }

            return header;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void ValidatePassphrase(string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Trim().Length < MinPassphraseLength)
        {
            throw new BackupException(
                $"The backup passphrase must be at least {MinPassphraseLength} characters (SB_BACKUP_PASSPHRASE).");
        }
    }

    private static BackupHeader ParseHeader(byte[] headerBytes)
    {
        BackupHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<BackupHeader>(headerBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BackupIntegrityException("The backup header is not valid JSON.", ex);
        }

        if (header is null
            || header.Format != FormatVersion
            || header.Kdf != KdfName
            || header.Cipher != CipherName
            || header.Iterations is < MinIterations or > MaxIterations
            || header.ChunkSize is < MinChunkSize or > MaxChunkSize
            || header.Salt is null || header.NoncePrefix is null || header.KeyCheck is null)
        {
            throw new BackupIntegrityException("The backup header is invalid or uses an unsupported format version.");
        }

        return header;
    }

    private static byte[] DecodeBase64(string value, int expectedLength, string name)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == expectedLength)
            {
                return bytes;
            }
        }
        catch (FormatException)
        {
            // Reported below.
        }

        throw new BackupIntegrityException($"The backup header has an invalid {name}.");
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeySize);

    private static string ComputeKeyCheck(byte[] key) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, KeyCheckLabel).AsSpan(0, 16));

    private static byte[] HeaderDigest(byte[] lengthBytes, byte[] headerBytes)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Magic);
        sha.AppendData(lengthBytes);
        sha.AppendData(headerBytes);
        return sha.GetHashAndReset();
    }

    private static byte[] BuildNonce(byte[] prefix, uint counter, bool isFinal)
    {
        var nonce = new byte[NonceSize];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize), counter);
        nonce[NonceSize - 1] = isFinal ? (byte)1 : (byte)0;
        return nonce;
    }

    private static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, string what, CancellationToken cancellationToken)
    {
        if (await ReadFullAsync(stream, buffer, cancellationToken).ConfigureAwait(false) != buffer.Length)
        {
            throw new BackupIntegrityException($"The backup is truncated (while reading the {what}).");
        }
    }
}
