using System.Text;
using SupermarketBilling.CounterAgent;

namespace SupermarketBilling.UnitTests.CounterAgent;

public sealed class CounterAgentTests
{
    internal static ReceiptInvoice Invoice(decimal change = 96m) => new(
        "C1-000001", "TAX_INVOICE", "GST_REGULAR", new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero), "C1", "Priya", "Test Traders Private Limited",
        "12 Bazaar Street, T. Nagar, Chennai 600017", "33AAACG1234A1ZX", null, null, false,
        [
            new ReceiptLine("Whole Wheat Atta 5 kg premium chakki fresh", 2, "PCS", 52m, 55m, 104m, 5, "TAXABLE"),
            new ReceiptLine("Tomato (loose) தக்காளி", 1.235m, "KG", 40m, null, 49.40m, 0, "EXEMPT"),
        ],
        0, 148.45m, 2.48m, 2.47m, 0, 0, 0.60m, 154m, [new ReceiptPayment("CASH", 250m, null)], change, null);

    [Fact]
    public void Receipt_starts_with_initialise_ends_with_a_cut_and_opens_the_drawer_only_when_asked()
    {
        var plain = ReceiptFormatter.Format(Invoice(), 48, openDrawer: false);
        Assert.Equal(new byte[] { 0x1B, (byte)'@' }, plain[..2]);
        Assert.True(EndsWith(plain, [0x1D, (byte)'V', 66, 0]));

        var withDrawer = ReceiptFormatter.Format(Invoice(), 48, openDrawer: true);
        Assert.True(EndsWith(withDrawer, [0x1B, (byte)'p', 0, 25, 250]));
    }

    [Fact]
    public void Receipt_text_fits_the_paper_and_shows_the_bill()
    {
        var text = Encoding.Latin1.GetString(ReceiptFormatter.Format(Invoice(), 48, openDrawer: false));
        var lines = text.Split('\n').Select(l => new string(l.Where(c => c >= ' ').ToArray())).ToList();

        Assert.All(lines, l => Assert.True(l.Length <= 48 + 8, $"too long: '{l}'")); // + command bytes on the same line
        Assert.Contains(lines, l => l.Contains("TAX INVOICE", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("No. C1-000001", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.TrimEnd().EndsWith("104.00", StringComparison.Ordinal) && l.Contains("2 PCS x 52.00 @5%", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("TOTAL", StringComparison.Ordinal) && l.Contains("154.00", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Change", StringComparison.Ordinal) && l.EndsWith("96.00", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Tomato (loose) ?????", StringComparison.Ordinal)); // non-Latin script prints as '?'
    }

    [Theory]
    [InlineData("ST,GS,+  1.235kg", 1.235, true)]
    [InlineData("US,GS,+  0.990kg", 0.990, false)]
    [InlineData("  1250 g", 1.250, true)]
    [InlineData("002.500", 2.5, true)]
    public void Scale_lines_are_read_in_kilograms(string line, double kilograms, bool stable)
    {
        var weight = ScaleReader.Parse(line);
        Assert.NotNull(weight);
        Assert.Equal(((decimal)kilograms, stable), (weight.Kilograms, weight.Stable));
    }

    [Fact]
    public void Unreadable_scale_lines_are_ignored() => Assert.Null(ScaleReader.Parse("ERR"));

    [Fact]
    public void Customer_display_shows_two_fitted_lines()
    {
        var bytes = CustomerDisplay.Show("Marie Biscuits 250 g extra", "Total Rs. 104.00", 20);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("\u001bQAMarie Biscuits 250 g\r", text, StringComparison.Ordinal);
        Assert.Contains("\u001bQBTotal Rs. 104.00    \r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_columns_keep_the_amount_whole()
    {
        var line = EscPos.TwoColumns("A very long item name that does not fit", "1,04,000.00", 32);
        Assert.Equal(32, line.Length);
        Assert.EndsWith(" 1,04,000.00", line, StringComparison.Ordinal);
    }

    private static bool EndsWith(byte[] bytes, byte[] tail) => bytes.Length >= tail.Length && bytes.AsSpan()[^tail.Length..].SequenceEqual(tail);
}
