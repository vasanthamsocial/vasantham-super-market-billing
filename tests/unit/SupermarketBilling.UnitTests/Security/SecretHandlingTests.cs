using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.UnitTests.Security;

public sealed class SecretHandlingTests
{
    private static SecretProtector Protector(string? key = null) =>
        new(Options.Create(new SecurityOptions { DataProtectionKey = key ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));

    [Fact]
    public void Protector_round_trips_and_output_differs_each_time()
    {
        var protector = Protector();
        var secret = Encoding.UTF8.GetBytes("totp-secret-value");

        var first = protector.Protect(secret, "mfa-secret");
        var second = protector.Protect(secret, "mfa-secret");

        Assert.NotEqual(first, second);
        Assert.StartsWith("v1.", first, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(first, "mfa-secret"));
    }

    [Fact]
    public void Protected_value_is_bound_to_its_purpose_and_key()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var protected1 = Protector(key).Protect([1, 2, 3], "mfa-secret");

        Assert.ThrowsAny<CryptographicException>(() => Protector(key).Unprotect(protected1, "something-else"));
        Assert.ThrowsAny<CryptographicException>(() => Protector().Unprotect(protected1, "mfa-secret"));
    }

    [Fact]
    public void Tampered_values_are_rejected()
    {
        var protector = Protector();
        var value = protector.Protect([1, 2, 3, 4], "p");
        var bytes = Convert.FromBase64String(value[3..]);
        bytes[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v1." + Convert.ToBase64String(bytes), "p"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("CHANGE_ME")]
    [InlineData("c2hvcnQ=")]
    public void Missing_or_weak_keys_stop_the_application_starting(string key)
    {
        Assert.Throws<InvalidOperationException>(() => Protector(key));
    }

    [Fact]
    public void Tokens_are_unique_high_entropy_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => SecretTokens.NewToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
    }

    [Fact]
    public void Human_codes_avoid_confusable_characters_and_hash_ignoring_format()
    {
        var code = SecretTokens.NewHumanCode();

        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        Assert.Equal(SecretTokens.HashHumanCode(code), SecretTokens.HashHumanCode(code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)));
        Assert.NotEqual(SecretTokens.HashHumanCode(code), SecretTokens.HashHumanCode("AAAA-BBBB-CCCC"));
    }

    [Fact]
    public void Password_hashes_are_salted_verifiable_and_use_the_configured_work_factor()
    {
        var hashing = new PasswordHashing(Options.Create(new SecurityOptions { PasswordHashIterations = 210_000 }));

        var first = hashing.Hash("Correct-Horse-9");
        var second = hashing.Hash("Correct-Horse-9");

        Assert.NotEqual(first, second);
        Assert.True(hashing.Verify(first, "Correct-Horse-9").Valid);
        Assert.False(hashing.Verify(first, "correct-horse-9").Valid);

        // Identity V3 format: 0x01 | PRF | iteration count (big-endian) | salt length | salt | subkey
        var bytes = Convert.FromBase64String(first);
        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(210_000, (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8]);
    }
}
