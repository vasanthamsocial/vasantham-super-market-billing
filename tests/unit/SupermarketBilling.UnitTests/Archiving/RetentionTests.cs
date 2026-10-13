using System.Security.Cryptography;
using System.Text;
using SupermarketBilling.Archiving;
using SupermarketBilling.Domain.Archiving;
using SupermarketBilling.Domain.Identity;

namespace SupermarketBilling.UnitTests.Archiving;

/// <summary>Retention gates (D-044): the months always kept, who may remove, and the archive receipt.</summary>
public sealed class RetentionTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static ArchiveReceiptPayload Payload() => new(ArchiveReceipt.Version, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SMKT", "2024-05",
        new string('a', 64), 1234, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UnixEpoch.AddDays(2));

    [Theory]
    [InlineData("2026-10-08", "2025-04-01")]
    [InlineData("2027-03-31", "2025-04-01")]
    [InlineData("2027-04-01", "2026-04-01")]
    [InlineData("2026-01-15", "2024-04-01")]
    public void The_current_and_the_previous_financial_year_are_always_kept(string today, string keepFrom) =>
        Assert.Equal(DateOnly.Parse(keepFrom, System.Globalization.CultureInfo.InvariantCulture),
            Retention.KeepFrom(DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Only_the_owner_may_remove_months()
    {
        Assert.Contains(Permissions.MonthsRemove, Roles.Get(Roles.Owner).Permissions);
        Assert.All(Roles.All.Where(r => r.Code != Roles.Owner), r => Assert.DoesNotContain(Permissions.MonthsRemove, r.Permissions));
    }

    [Fact]
    public void A_receipt_is_read_back_only_with_the_key_that_signed_it_and_unchanged()
    {
        using var archive = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Payload();
        var file = ArchiveReceipt.Write(payload, archive);

        Assert.Equal(payload, ArchiveReceipt.Read(file, archive));
        Assert.Equal(ArchivePackage.KeyId(archive.ExportSubjectPublicKeyInfo()), ArchiveReceipt.KeyId(file));
        Assert.Throws<ArchivePackageException>(() => ArchiveReceipt.Read(file, other));

        // The same payload with another month, under the original signature.
        var text = Encoding.UTF8.GetString(file);
        var original = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, Web));
        var changed = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload with { Month = "2024-06" }, Web));
        Assert.Contains(original, text, StringComparison.Ordinal);
        Assert.Throws<ArchivePackageException>(() => ArchiveReceipt.Read(Encoding.UTF8.GetBytes(text.Replace(original, changed, StringComparison.Ordinal)), archive));

        Assert.Throws<ArchivePackageException>(() => ArchiveReceipt.Read("not a receipt"u8.ToArray(), archive));
        Assert.Throws<ArchivePackageException>(() => ArchiveReceipt.Read(new byte[ArchiveReceipt.MaxSize + 1], archive));
    }
}
