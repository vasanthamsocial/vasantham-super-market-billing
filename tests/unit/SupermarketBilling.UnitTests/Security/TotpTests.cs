using System.Text;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.UnitTests.Security;

public sealed class TotpTests
{
    // RFC 6238 Appendix B, SHA-1 secret. The RFC lists 8-digit values; authenticator apps use the last 6 digits.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Matches_rfc_6238_test_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, Totp.Compute(RfcSecret, step));
    }

    [Fact]
    public void Accepts_one_step_of_clock_drift_either_way_but_not_more()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 1), now, null));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 2), now, null));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 2), now, null));
    }

    [Fact]
    public void Rejects_a_code_from_an_already_used_step()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.Compute(RfcSecret, step);

        Assert.Equal(step, Totp.Verify(RfcSecret, code, now, lastUsedStep: step - 1));
        Assert.Null(Totp.Verify(RfcSecret, code, now, lastUsedStep: step));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Rejects_malformed_codes(string? code)
    {
        Assert.Null(Totp.Verify(RfcSecret, code, DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void Accepts_codes_typed_with_spaces()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.Compute(RfcSecret, Totp.StepAt(now));

        Assert.NotNull(Totp.Verify(RfcSecret, code[..3] + " " + code[3..], now, null));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_rfc_4648_and_round_trips(string input, string encoded)
    {
        var bytes = Encoding.ASCII.GetBytes(input);

        Assert.Equal(encoded, Totp.Base32Encode(bytes));
        Assert.Equal(bytes, Totp.Base32Decode(encoded));
    }

    [Fact]
    public void Otpauth_uri_contains_issuer_account_and_parameters()
    {
        var uri = Totp.OtpAuthUri("SupermarketBilling", "cashier.one", RfcSecret);

        Assert.StartsWith("otpauth://totp/SupermarketBilling:cashier.one?secret=", uri, StringComparison.Ordinal);
        Assert.Contains("&period=30", uri, StringComparison.Ordinal);
        Assert.Contains("&digits=6", uri, StringComparison.Ordinal);
    }
}
