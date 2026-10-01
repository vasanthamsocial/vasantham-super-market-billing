using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Infrastructure.Documents;

namespace SupermarketBilling.UnitTests.Sales;

public sealed partial class InvoiceDocumentTests
{
    [Theory]
    [InlineData(0, "Rupees Zero Only")]
    [InlineData(105, "Rupees One Hundred Five Only")]
    [InlineData(1999.50, "Rupees One Thousand Nine Hundred Ninety Nine and Fifty Paise Only")]
    [InlineData(250000, "Rupees Two Lakh Fifty Thousand Only")]
    [InlineData(12345678.09, "Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight and Nine Paise Only")]
    public void Amounts_are_written_in_the_indian_system(double amount, string expected) =>
        Assert.Equal(expected, AmountInWords.Rupees((decimal)amount));

    [Fact]
    public void Pdf_has_a_valid_cross_reference_table_and_readable_text()
    {
        var pdf = new SimplePdf("Test (1)");
        pdf.Text(36, 36, "Hello (world) \\ Rs. ₹ 5 த", 10);
        pdf.NewPage();
        pdf.Text(36, 36, "Second page", 10, bold: true);
        var bytes = pdf.ToBytes();
        var text = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/Count 2", text, StringComparison.Ordinal);

        // Every xref entry points at "n 0 obj", and startxref points at the xref table.
        var startXref = int.Parse(StartXref().Match(text).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith("xref", text[startXref..], StringComparison.Ordinal);
        var entries = XrefEntry().Matches(text[startXref..]).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", text[entries[i]..], StringComparison.Ordinal);
        }

        var content = PageContents(bytes);
        Assert.Contains(@"(Hello \(world\) \\ Rs. Rs. 5 ?) Tj", content[0], StringComparison.Ordinal); // rupee sign spelt out, Tamil as '?'
        Assert.Contains("/F2", content[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_measured_so_numbers_can_be_right_aligned()
    {
        Assert.Equal(5.56 * 4 + 2.78, SimplePdf.Width("12.34", 10), 3);
        var fitted = SimplePdf.Fit("Very long item name indeed", 10, 70);
        Assert.EndsWith("...", fitted, StringComparison.Ordinal);
        Assert.True(SimplePdf.Width(fitted, 10) <= 70);
        Assert.True(SimplePdf.Width("Very long item name indeed"[..(fitted.Length - 2)] + "...", 10) > 70); // one more character would not fit
        Assert.Equal(["one two", "three"], SimplePdf.Wrap("one two three", 10, 45));
    }

    [Fact]
    public void Invoice_pdf_prints_the_numbers_as_issued_and_runs_over_pages_when_long()
    {
        var lines = Enumerable.Range(1, 80).Select(i => new CartLineDto(
            i, Guid.NewGuid(), Guid.NewGuid(), $"Item {i}", "PCS", "1101", 1, 55m, 52.50m, true, "STANDARD", Guid.NewGuid(), false, false, "TAXABLE",
            5, 0, 52.50m, 0, 0, 50m, 1.25m, 1.25m, 0, 0, 52.50m)).ToList();
        var invoice = new InvoiceDto(
            Guid.NewGuid(), "C1-000042", "TAX_INVOICE", "GST_REGULAR", "RETAIL", new DateOnly(2026, 10, 1), new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero),
            Guid.NewGuid(), Guid.NewGuid(), "C1", "Cashier One", "Test Traders Private Limited", "33AAACG1234A1ZX", "1 Main Road, Chennai", "33", "Buyer & Co",
            null, "9876543210", null, "33", false, lines, 4200m, 0, 4000m, 100m, 100m, 0, 0, 0, 4200m, 4200m, 0,
            [new InvoicePaymentDto("CASH", 4200m, null)], null);

        var bytes = InvoicePdf.Render(invoice, "Asia/Kolkata");
        var pages = PageContents(bytes);

        Assert.True(pages.Count >= 2);
        var all = string.Join("\n", pages);
        Assert.Contains("(TAX INVOICE)", all, StringComparison.Ordinal);
        Assert.Contains("(C1-000042)", all, StringComparison.Ordinal);
        Assert.Contains("(01-10-2026 10:30)", all, StringComparison.Ordinal); // issued 05:00 UTC, printed in store time
        Assert.Contains("(4,200.00)", all, StringComparison.Ordinal);
        Assert.Contains("(Rupees Four Thousand Two Hundred Only)", all, StringComparison.Ordinal);
        Assert.Contains("(State: 33 - Tamil Nadu)", all, StringComparison.Ordinal);
        Assert.Contains($"(Page {pages.Count} of {pages.Count})", pages[^1], StringComparison.Ordinal);
        Assert.Contains($"(Page 1 of {pages.Count})", pages[0], StringComparison.Ordinal);
    }

    private static List<string> PageContents(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var result = new List<string>();
        foreach (Match match in StreamPattern().Matches(text))
        {
            var start = match.Index + match.Length;
            var length = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            using var input = new MemoryStream(pdf, start, length);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            result.Add(reader.ReadToEnd());
        }

        return result;
    }

    [GeneratedRegex(@"startxref\n(\d+)")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"(\d{10}) 00000 n ")]
    private static partial Regex XrefEntry();

    [GeneratedRegex(@"<< /Length (\d+) /Filter /FlateDecode >>\nstream\n")]
    private static partial Regex StreamPattern();
}
