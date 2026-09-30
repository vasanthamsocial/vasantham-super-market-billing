using System.Buffers.Binary;
using System.Security.Cryptography;
using SupermarketBilling.BackupTool.Format;

namespace SupermarketBilling.UnitTests.Backup;

public sealed class BackupCryptoTests
{
    private const string Passphrase = "correct horse battery staple 2026";
    private const int ChunkSize = 4096;
    private static readonly DateTimeOffset Created = new(2026, 9, 30, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ChunkSize - 1)]
    [InlineData(ChunkSize)]
    [InlineData(ChunkSize + 1)]
    [InlineData((ChunkSize * 5) + 123)]
    public async Task Round_trips_payloads_of_any_size(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);

        var encrypted = await EncryptAsync(plaintext);
        var decrypted = await DecryptAsync(encrypted, Passphrase);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task Ciphertext_does_not_contain_the_plaintext()
    {
        var plaintext = "INVOICE-0001 customer secret data "u8.ToArray().Concat(new byte[1000]).ToArray();

        var encrypted = await EncryptAsync(plaintext);

        Assert.Equal(-1, encrypted.AsSpan().IndexOf("INVOICE-0001"u8));
    }

    [Fact]
    public async Task Same_input_encrypts_differently_each_time()
    {
        var plaintext = RandomNumberGenerator.GetBytes(100);

        Assert.NotEqual(await EncryptAsync(plaintext), await EncryptAsync(plaintext));
    }

    [Fact]
    public async Task Wrong_passphrase_is_reported_as_such()
    {
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(100));

        await Assert.ThrowsAsync<BackupPassphraseException>(() => DecryptAsync(encrypted, "a different long passphrase!"));
    }

    [Fact]
    public async Task Any_modified_ciphertext_byte_is_detected()
    {
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(ChunkSize * 3));

        // Flip one byte in the middle of the second block's ciphertext.
        encrypted[encrypted.Length / 2] ^= 0x01;

        var error = await Assert.ThrowsAsync<BackupIntegrityException>(() => DecryptAsync(encrypted, Passphrase));
        Assert.Contains("failed authentication", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modified_header_is_detected()
    {
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(100));
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(8));
        var header = System.Text.Encoding.UTF8.GetString(encrypted, 12, headerLength);
        // Change the created date without changing length: the header digest is authenticated by every block.
        var tampered = header.Replace("2026-09-30", "2026-09-29", StringComparison.Ordinal);
        System.Text.Encoding.UTF8.GetBytes(tampered).CopyTo(encrypted, 12);

        await Assert.ThrowsAsync<BackupIntegrityException>(() => DecryptAsync(encrypted, Passphrase));
    }

    [Fact]
    public async Task Truncated_backup_missing_its_final_block_is_detected()
    {
        var plaintext = RandomNumberGenerator.GetBytes(ChunkSize * 3);
        var encrypted = await EncryptAsync(plaintext);
        var headerEnd = 12 + BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(8));
        var blockSize = 5 + ChunkSize + 16;
        // Keep the header and the first two complete (non-final) blocks only.
        var truncated = encrypted.AsSpan(0, headerEnd + (2 * blockSize)).ToArray();

        var error = await Assert.ThrowsAsync<BackupIntegrityException>(() => DecryptAsync(truncated, Passphrase));
        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reordered_blocks_are_detected()
    {
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(ChunkSize * 3));
        var headerEnd = 12 + BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(8));
        var blockSize = 5 + ChunkSize + 16;
        var first = encrypted.AsSpan(headerEnd, blockSize).ToArray();
        encrypted.AsSpan(headerEnd + blockSize, blockSize).CopyTo(encrypted.AsSpan(headerEnd));
        first.CopyTo(encrypted.AsSpan(headerEnd + blockSize));

        await Assert.ThrowsAsync<BackupIntegrityException>(() => DecryptAsync(encrypted, Passphrase));
    }

    [Fact]
    public async Task Data_appended_after_the_final_block_is_detected()
    {
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(100));
        var extended = encrypted.Concat(new byte[] { 0x00 }).ToArray();

        await Assert.ThrowsAsync<BackupIntegrityException>(() => DecryptAsync(extended, Passphrase));
    }

    [Fact]
    public async Task Files_that_are_not_backups_are_rejected()
    {
        var notABackup = "PGDMP this is a raw pg_dump, not an encrypted backup"u8.ToArray();

        var error = await Assert.ThrowsAsync<BackupIntegrityException>(() => DecryptAsync(notABackup, Passphrase));
        Assert.Contains("not a SupermarketBilling backup", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("               ")]
    public async Task Weak_passphrases_are_refused(string passphrase)
    {
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<BackupException>(() =>
            BackupCrypto.EncryptAsync(new MemoryStream([1, 2, 3]), output, passphrase, Created, CancellationToken.None, ChunkSize, BackupCrypto.MinIterations));
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext)
    {
        await using var output = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(plaintext), output, Passphrase, Created, CancellationToken.None, ChunkSize, BackupCrypto.MinIterations);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] encrypted, string passphrase)
    {
        await using var result = new MemoryStream();
        await BackupCrypto.DecryptAsync(
            new MemoryStream(encrypted),
            passphrase,
            (data, ct) => result.WriteAsync(data, ct),
            CancellationToken.None);
        return result.ToArray();
    }
}
