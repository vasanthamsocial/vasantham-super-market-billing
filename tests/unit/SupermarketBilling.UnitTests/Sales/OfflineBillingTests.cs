using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.UnitTests.Sales;

public sealed class OfflineBillingTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
    internal static readonly Guid Atta = Guid.NewGuid();
    internal static readonly Guid Tomato = Guid.NewGuid();
    internal static readonly Guid Soap = Guid.NewGuid();
    internal static readonly Guid Store = Guid.NewGuid();

    /// <summary>A pack for counter C1: atta (GST 5%, Rs. 52 inclusive, MRP 55), loose tomato (exempt, Rs. 40/kg), soap with two MRPs.</summary>
    internal static OfflinePack Pack(string mode = "GST_REGULAR", int bills = 3, decimal amount = 1000m, int hours = 4, Guid? shift = null, long next = 1) => new(
        Guid.CreateVersion7(), Now, Guid.NewGuid(), Store, "Asia/Kolkata", Guid.NewGuid(), "C1", DeviceId, CashierId, "Priya", shift ?? ShiftId, mode, "C1/OF", next,
        new SalesInvoice.Seller("Test Traders", mode == "NOT_GST_REGISTERED" ? null : "33AAACG1234A1ZX", "12 Bazaar Street", "33"),
        new OfflineLimits(bills, amount, hours),
        [
            new OfflinePackItem(Atta, Guid.NewGuid(), Guid.NewGuid(), "Atta 5 kg", "PCS", "PCS", 0, 1, "1101", "TAXABLE", 5, 0, [55m], ["8901234567894"]),
            new OfflinePackItem(Tomato, Guid.NewGuid(), Guid.NewGuid(), "Tomato", "KG", "KG", 3, 1, "0702", "EXEMPT", 0, 0, [], []),
            new OfflinePackItem(Soap, Guid.NewGuid(), Guid.NewGuid(), "Soap", "PCS", "PCS", 0, 1, "3401", "TAXABLE", 18, 0, [20m, 22m], []),
        ],
        [
            new OfflinePackPrice(Guid.NewGuid(), Atta, "STANDARD", "ANY", 52m, true, null, null, 0, null, Now.AddDays(-30), null, 10),
            new OfflinePackPrice(Guid.NewGuid(), Tomato, "STANDARD", "ANY", 40m, true, null, null, 0, null, Now.AddDays(-30), null, 10),
            new OfflinePackPrice(Guid.NewGuid(), Soap, "STANDARD", "ANY", 19m, true, null, null, 0, null, Now.AddDays(-30), null, 10),
            new OfflinePackPrice(Guid.NewGuid(), Soap, "STANDARD", "ANY", 21m, true, 22m, null, 0, null, Now.AddDays(-30), null, 10),
        ]);

    internal static readonly Guid DeviceId = Guid.NewGuid();
    internal static readonly Guid CashierId = Guid.NewGuid();
    internal static readonly Guid ShiftId = Guid.NewGuid();

    internal static OfflineCart Cart(params (Guid Pack, decimal Quantity, decimal? Mrp)[] lines) =>
        new("RETAIL", lines.Select(l => new OfflineCartLine(l.Pack, l.Quantity, l.Mrp)).ToList());

    private static OfflineBill Issue(OfflinePack pack, OfflineCart cart, long sequence = 1, params PaymentInput[] payments)
    {
        var draft = OfflineBilling.Price(pack, cart, Now);
        return OfflineBilling.Issue(pack, draft, payments.Length > 0 ? payments : [new PaymentInput("CASH", draft.Result.GrandTotal, null)], Guid.NewGuid(), sequence, Now);
    }

    [Fact]
    public void A_cart_is_priced_from_the_pack_with_the_servers_rules_and_arithmetic()
    {
        var draft = OfflineBilling.Price(Pack(), Cart((Atta, 2, null), (Tomato, 1.235m, null)), Now);
        Assert.Equal(("TAX_INVOICE", 153.40m, 104m, 49.40m), (draft.Kind, draft.Result.Lines.Sum(l => l.Total), draft.Lines[0].Amounts.Total, draft.Lines[1].Amounts.Total));
        Assert.Equal((2.48m, 2.48m, -0.40m, 153m), (draft.Result.Cgst, draft.Result.Sgst, draft.Result.RoundOff, draft.Result.GrandTotal));
        var server = InvoiceCalculator.Calculate(new BillInput("GST_REGULAR", false, [
            new BillLineInput(2, 52m, true, "TAXABLE", 5, 0, 0), new BillLineInput(1.235m, 40m, true, "EXEMPT", 0, 0, 0)], 0));
        Assert.Equal(server.Lines, draft.Result.Lines);
        Assert.Equal((server.Taxable, server.GrandTotal), (draft.Result.Taxable, draft.Result.GrandTotal));

        var composition = OfflineBilling.Price(Pack("GST_COMPOSITION"), Cart((Atta, 2, null)), Now);
        Assert.Equal(("BILL_OF_SUPPLY", 0m, 104m), (composition.Kind, composition.Result.Cgst, composition.Result.GrandTotal));
    }

    [Fact]
    public void The_pack_on_the_label_decides_the_mrp_and_its_price()
    {
        Assert.Equal("mrp.choose", Assert.Throws<DomainException>(() => OfflineBilling.Price(Pack(), Cart((Soap, 1, null)), Now)).Code);
        Assert.Equal(21m, OfflineBilling.Price(Pack(), Cart((Soap, 1, 22m)), Now).Lines[0].UnitPrice);
        Assert.Equal(19m, OfflineBilling.Price(Pack(), Cart((Soap, 1, 20m)), Now).Lines[0].UnitPrice);
        Assert.Equal("mrp.unknown", Assert.Throws<DomainException>(() => OfflineBilling.Price(Pack(), Cart((Soap, 1, 25m)), Now)).Code);
    }

    [Theory]
    [InlineData(1.5, "invoice.quantity_precision")]
    [InlineData(0, "invoice.quantity_invalid")]
    public void Quantities_follow_the_unit(double quantity, string code) =>
        Assert.Equal(code, Assert.Throws<DomainException>(() => OfflineBilling.Price(Pack(), Cart((Atta, (decimal)quantity, null)), Now)).Code);

    [Fact]
    public void An_item_without_a_price_in_the_pack_cannot_be_sold_offline()
    {
        var pack = Pack() with { Prices = Pack().Prices.Where(p => p.VariantUnitId != Atta).ToList() };
        Assert.Equal("price.missing", Assert.Throws<DomainException>(() => OfflineBilling.Price(pack, Cart((Atta, 1, null)), Now)).Code);
        Assert.Equal("item.unknown", Assert.Throws<DomainException>(() => OfflineBilling.Price(pack, Cart((Guid.NewGuid(), 1, null)), Now)).Code);
        var expired = Pack() with { Prices = Pack().Prices.Select(p => p with { ValidToUtc = Now.AddMinutes(-1) }).ToList() };
        Assert.Equal("price.missing", Assert.Throws<DomainException>(() => OfflineBilling.Price(expired, Cart((Tomato, 1, null)), Now)).Code);
    }

    [Fact]
    public void Offline_bills_are_paid_in_cash_card_or_upi_with_a_reference_and_numbered_in_the_offline_series()
    {
        var pack = Pack();
        var bill = Issue(pack, Cart((Atta, 1, null)), 7, new PaymentInput("CASH", 100m, null));
        Assert.Equal(("C1/OF-000007", 48m, new DateOnly(2026, 10, 5), CashierId, ShiftId), (bill.Number, bill.ChangeDue, bill.BusinessDate, bill.CashierUserId, bill.ShiftId));

        Assert.Equal("offline.method_not_allowed", Assert.Throws<DomainException>(() => Issue(pack, Cart((Atta, 1, null)), 1, new PaymentInput("WALLET", 52m, "W1"))).Code);
        Assert.Equal("offline.method_not_allowed", Assert.Throws<DomainException>(() => Issue(pack, Cart((Atta, 1, null)), 1, new PaymentInput("ON_ACCOUNT", 52m, null))).Code);
        Assert.Equal("offline.reference_required", Assert.Throws<DomainException>(() => Issue(pack, Cart((Atta, 1, null)), 1, new PaymentInput("UPI", 52m, " "))).Code);
        Assert.Equal("payment.short", Assert.Throws<DomainException>(() => Issue(pack, Cart((Atta, 1, null)), 1, new PaymentInput("CASH", 50m, null))).Code);
    }

    [Fact]
    public void A_counter_stops_billing_offline_at_its_limits()
    {
        var pack = Pack(bills: 3, amount: 200m, hours: 4);
        var one = Issue(pack, Cart((Atta, 2, null)));
        Assert.Null(OfflineBilling.Refusal(pack, [one], 96m, Now));
        Assert.Contains("Rs. 200.00", OfflineBilling.Refusal(pack, [one], 97m, Now), StringComparison.Ordinal);
        Assert.Contains("at most 3 bills", OfflineBilling.Refusal(pack, [one, one, one], 1m, Now), StringComparison.Ordinal);
        Assert.Contains("waited more than 4 hours", OfflineBilling.Refusal(pack with { CreatedAtUtc = Now.AddHours(3) }, [one], 1m, Now.AddHours(4.5)), StringComparison.Ordinal);
        Assert.Contains("price list is more than 4 hours old", OfflineBilling.Refusal(pack, [], 1m, Now.AddHours(5)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_server_finds_any_bill_whose_figures_were_changed()
    {
        var bill = Issue(Pack(), Cart((Atta, 2, null), (Tomato, 1.235m, null)), 1, new PaymentInput("CASH", 200m, null));
        Assert.Null(OfflineBilling.Inconsistency(bill));
        Assert.Contains("do not add up", OfflineBilling.Inconsistency(bill with { GrandTotal = 150m }), StringComparison.Ordinal);
        Assert.Contains("do not add up", OfflineBilling.Inconsistency(bill with { CgstTotal = 0m, SgstTotal = 0m }), StringComparison.Ordinal);
        var line = bill.Lines[0];
        Assert.Contains("do not add up", OfflineBilling.Inconsistency(bill with { Lines = [line with { UnitPrice = 40m }, bill.Lines[1]] }), StringComparison.Ordinal);
        Assert.Contains("offline series", OfflineBilling.Inconsistency(bill with { NumberPrefix = "C1", Number = "C1-000001" }), StringComparison.Ordinal);
        Assert.Contains("offline series", OfflineBilling.Inconsistency(bill with { Number = "C1/OF-000002" }), StringComparison.Ordinal);
        Assert.Contains("cash, card or UPI", OfflineBilling.Inconsistency(bill with { Payments = [new PaymentInput("WALLET", 200m, "x")] }), StringComparison.Ordinal);
        Assert.Contains("change", OfflineBilling.Inconsistency(bill with { ChangeDue = 0m }), StringComparison.Ordinal);
        Assert.Contains("numbered", OfflineBilling.Inconsistency(bill with { Lines = [bill.Lines[1], line] }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C1", true)]
    [InlineData("ABCDEF", true)]
    [InlineData("ABCDEFB", false)]
    public void The_offline_series_fits_the_16_characters_gst_allows(string prefix, bool fits)
    {
        Assert.Equal(fits, OfflineSeries.Fits(prefix));
        Assert.True(OfflineSeries.IsOffline(OfflineSeries.Prefix(prefix)));
        Assert.False(OfflineSeries.IsOffline(prefix));
    }

    [Fact]
    public void Offline_limits_are_sensible()
    {
        Assert.Equal("offline.bills_invalid", Assert.Throws<DomainException>(() => new OfflineLimits(0, 100m, 4).Validate()).Code);
        Assert.Equal("offline.amount_invalid", Assert.Throws<DomainException>(() => new OfflineLimits(10, 10.005m, 4).Validate()).Code);
        Assert.Equal("offline.hours_invalid", Assert.Throws<DomainException>(() => new OfflineLimits(10, 100m, 73).Validate()).Code);
        new OfflineLimits(2000, 10_000_000m, 72).Validate();
    }
}
