using System.Text;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Purchases;

namespace SupermarketBilling.UnitTests.Purchases;

public sealed class PurchaseDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];

    [Theory]
    [InlineData("invoice.pdf", "invoice.pdf")]
    [InlineData(@"C:\scans\..\..\Windows\inv 12.pdf", "inv 12.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("<script>.pdf", "_script_.pdf")]
    [InlineData("scan", "scan.pdf")]
    [InlineData("", "attachment.pdf")]
    public void File_names_lose_folders_and_unsafe_characters(string given, string expected) =>
        Assert.Equal(expected, Attachment.Create(Guid.NewGuid(), "GRN", Guid.NewGuid(), given, Pdf, Guid.NewGuid(), Now).FileName);

    [Fact]
    public void Files_are_recognised_by_content_not_by_name()
    {
        var png = Attachment.Create(Guid.NewGuid(), "GRN", Guid.NewGuid(), "photo.png", Png, Guid.NewGuid(), Now);
        Assert.Equal(("image/png", 64), (png.ContentType, png.Sha256.Length));
        Assert.Equal("attachment.extension_mismatch", Assert.Throws<DomainException>(() =>
            Attachment.Create(Guid.NewGuid(), "GRN", Guid.NewGuid(), "photo.pdf", Png, Guid.NewGuid(), Now)).Code);
        Assert.Equal("attachment.type_invalid", Assert.Throws<DomainException>(() =>
            Attachment.Create(Guid.NewGuid(), "GRN", Guid.NewGuid(), "virus.exe", Encoding.ASCII.GetBytes("MZ\u0090\0"), Guid.NewGuid(), Now)).Code);
        Assert.Equal("attachment.type_invalid", Assert.Throws<DomainException>(() =>
            Attachment.Create(Guid.NewGuid(), "GRN", Guid.NewGuid(), "page.pdf", Encoding.ASCII.GetBytes("<html><script>"), Guid.NewGuid(), Now)).Code);
        Assert.Equal("attachment.size_invalid", Assert.Throws<DomainException>(() =>
            Attachment.Create(Guid.NewGuid(), "GRN", Guid.NewGuid(), "big.pdf", [.. Pdf, .. new byte[Attachment.MaxBytes]], Guid.NewGuid(), Now)).Code);
    }

    [Fact]
    public void An_order_with_receipts_is_closed_not_cancelled()
    {
        var order = PurchaseOrder.Place(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "MAIN/PO/000001", 1, new DateOnly(2026, 10, 2), null, null, Guid.NewGuid(), Now);
        Assert.Equal("purchase_order.received", Assert.Throws<DomainException>(() => order.Cancel(anythingReceived: true, Now)).Code);
        order.Close(Now);
        Assert.Equal(PurchaseOrderStatus.Closed, order.Status);
        Assert.Equal("purchase_order.not_open", Assert.Throws<DomainException>(() => order.Cancel(false, Now)).Code);
        Assert.Equal("purchase_order.expected_date_invalid", Assert.Throws<DomainException>(() =>
            PurchaseOrder.Place(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "X", 1, new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1), null, Guid.NewGuid(), Now)).Code);
    }
}
