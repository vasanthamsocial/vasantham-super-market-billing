using System.Security.Cryptography;
using System.Text;
using SupermarketBilling.Archiving;

namespace SupermarketBilling.UnitTests.Archiving;

/// <summary>The monthly archive package: only the intended archive opens it, and anything changed or unsigned is refused.</summary>
public sealed class ArchivePackageTests : IDisposable
{
    private readonly ECDsa _store = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDiffieHellman _archive = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _business = Guid.NewGuid();

    public void Dispose()
    {
        _store.Dispose();
        _archive.Dispose();
    }

    private byte[] Package(out ArchiveManifest manifest, params ArchiveDatasetInput[] datasets) =>
        ArchivePackage.Write(new ArchiveManifest(0, Guid.CreateVersion7(), _business, "SMKT", "Test Traders", "2026-09", DateTimeOffset.UtcNow, Guid.NewGuid(), []),
            datasets.Length > 0 ? datasets : Sales(), _store, _archive, out manifest);

    private static ArchiveDatasetInput[] Sales() =>
    [
        new("sales_invoices", ["{\"id\":1,\"grand_total\":104.00,\"number\":\"C1-000001\"}", "{\"id\":2,\"grand_total\":52.50,\"number\":\"C1-000002\"}"], ["grand_total"]),
        new("users", ["{\"id\":\"u1\",\"display_name\":\"Priya\"}"], []),
        new("empty", [], ["amount"]),
    ];

    private ECDsa? Trusted(string keyId) => keyId == ArchivePackage.KeyId(_store.ExportSubjectPublicKeyInfo()) ? _store : null;

    [Fact]
    public void The_archive_opens_what_the_store_server_sent_with_its_counts_totals_and_checksums()
    {
        var file = Package(out var manifest);
        var header = ArchivePackage.ReadHeader(file);
        Assert.Equal(("2026-09", _business, "SMKT"), (header.Month, header.BusinessId, header.BusinessCode));

        var contents = ArchivePackage.Open(file, _archive, Trusted);
        Assert.Equal(manifest.PackageId, contents.Manifest.PackageId);
        Assert.Equal((2L, 156.50m), (contents.Manifest.Datasets[0].Records, contents.Manifest.Datasets[0].Totals["grand_total"]));
        Assert.Equal("C1-000002", contents.Datasets["sales_invoices"][1].GetProperty("number").GetString());
        Assert.Empty(contents.Datasets["empty"]);

        // Encrypted: nothing of the content is readable in the file.
        var text = Encoding.Latin1.GetString(file);
        Assert.DoesNotContain("C1-000001", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Priya", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_server_the_archive_does_not_trust_is_refused()
    {
        var file = Package(out _);
        Assert.Contains("does not trust", Assert.Throws<ArchivePackageException>(() => ArchivePackage.Open(file, _archive, _ => null)).Message, StringComparison.Ordinal);

        // Another key presented under the trusted id does not verify.
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Contains("signature", Assert.Throws<ArchivePackageException>(() => ArchivePackage.Open(file, _archive, _ => impostor)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_archive_it_was_made_for_can_open_it()
    {
        var file = Package(out _);
        using var other = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.Contains("another archive", Assert.Throws<ArchivePackageException>(() => ArchivePackage.Open(file, other, Trusted)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("content")]
    [InlineData("truncated")]
    public void Any_change_to_the_file_is_found(string where)
    {
        var file = Package(out _);
        var headerStart = "SBARC1\n".Length;
        var contentStart = Array.IndexOf(file, (byte)'\n', Array.IndexOf(file, (byte)'\n', headerStart) + 1) + 1;
        switch (where)
        {
            case "header":
                // "2026-09" becomes "2026-08": still valid JSON, but no longer what was signed.
                var at = Encoding.Latin1.GetString(file).IndexOf("2026-09", StringComparison.Ordinal);
                file[at + 6] = (byte)'8';
                break;
            case "content":
                file[contentStart + 10] ^= 0x01;
                break;
            default:
                file = file[..^5];
                break;
        }

        Assert.Throws<ArchivePackageException>(() => ArchivePackage.Open(file, _archive, Trusted));
    }

    [Fact]
    public void A_file_that_is_not_a_package_is_refused()
    {
        Assert.Contains("not an archive package", Assert.Throws<ArchivePackageException>(() => ArchivePackage.ReadHeader("hello"u8.ToArray())).Message, StringComparison.Ordinal);
        Assert.Contains("incomplete", Assert.Throws<ArchivePackageException>(() => ArchivePackage.ReadHeader("SBARC1\n{}"u8.ToArray())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_must_be_single_lines_of_json() =>
        Assert.Throws<ArgumentException>(() => Package(out _, new ArchiveDatasetInput("bad", ["{\"a\":\n1}"], [])));
}
