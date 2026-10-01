using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.UnitTests.Sales;

public sealed class SalesRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Split_payment_gives_change_only_from_cash()
    {
        Assert.Equal(30m, PaymentRules.ChangeDue(470m, [new("UPI", 200m, "UTR1"), new("CASH", 300m, null)]));
        Assert.Equal(0m, PaymentRules.ChangeDue(470m, [new("CARD", 470m, "1234")]));
    }

    [Theory]
    [InlineData("CARD", 500, "payment.overpaid_non_cash")]
    [InlineData("CASH", 400, "payment.short")]
    [InlineData("CHEQUE", 470, "payment.method_invalid")]
    [InlineData("CASH", 0, "payment.amount_invalid")]
    [InlineData("CASH", 470.005, "payment.amount_invalid")]
    public void Wrong_payments_are_refused(string method, double amount, string code)
    {
        var error = Assert.Throws<DomainException>(() => PaymentRules.ChangeDue(470m, [new(method, (decimal)amount, null)]));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("c1", "C1")]
    [InlineData("BILL01", "BILL01")]
    public void Counter_codes_are_short_and_upper_case(string code, string expected) =>
        Assert.Equal(expected, Counter.Create(Guid.NewGuid(), Guid.NewGuid(), code, "Front counter", Now).Code);

    [Theory]
    [InlineData("")]
    [InlineData("COUNTER1")]
    [InlineData("C-1")]
    public void Bad_counter_codes_are_refused(string code) =>
        Assert.Equal("counter.code_invalid", Assert.Throws<DomainException>(() => Counter.Create(Guid.NewGuid(), Guid.NewGuid(), code, "X", Now)).Code);

    [Theory]
    [InlineData("C1", 123, "C1-000123")]
    [InlineData("BILL01", 1_000_000, "BILL01-1000000")]
    public void Invoice_numbers_stay_within_the_gst_limit(string code, long sequence, string expected)
    {
        var number = Counter.InvoiceNumber(code, sequence);
        Assert.Equal(expected, number);
        Assert.True(number.Length <= 16);
    }

    [Fact]
    public void Supervisor_approval_is_single_use_short_lived_and_bound_to_its_counter_and_cashier()
    {
        var counter = Guid.NewGuid();
        var cashier = Guid.NewGuid();
        var supervisor = Guid.NewGuid();
        Assert.Equal("approval.self", Assert.Throws<DomainException>(() => SupervisorApproval.Grant(
            Guid.NewGuid(), counter, "DISCOUNT", null, null, 50m, "Damaged box", cashier, cashier, [1], Now, TimeSpan.FromMinutes(10))).Code);

        SupervisorApproval Grant() => SupervisorApproval.Grant(Guid.NewGuid(), counter, "DISCOUNT", null, null, 50m, "Damaged box", supervisor, cashier, [1], Now, TimeSpan.FromMinutes(10));

        Assert.Equal("approval.mismatch", Assert.Throws<DomainException>(() => Grant().Use(Guid.NewGuid(), cashier, Guid.NewGuid(), Now)).Code);
        Assert.Equal("approval.mismatch", Assert.Throws<DomainException>(() => Grant().Use(counter, Guid.NewGuid(), Guid.NewGuid(), Now)).Code);
        Assert.Equal("approval.expired", Assert.Throws<DomainException>(() => Grant().Use(counter, cashier, Guid.NewGuid(), Now.AddMinutes(10))).Code);

        var approval = Grant();
        approval.Use(counter, cashier, Guid.NewGuid(), Now.AddMinutes(1));
        Assert.Equal("approval.used", Assert.Throws<DomainException>(() => approval.Use(counter, cashier, Guid.NewGuid(), Now.AddMinutes(2))).Code);
    }

    [Fact]
    public void A_price_override_needs_the_pack_and_a_price_in_paise() =>
        Assert.Equal("approval.price_invalid", Assert.Throws<DomainException>(() => SupervisorApproval.Grant(
            Guid.NewGuid(), Guid.NewGuid(), "PRICE_OVERRIDE", Guid.NewGuid(), 10.005m, null, "Price match", Guid.NewGuid(), Guid.NewGuid(), [1], Now,
            TimeSpan.FromMinutes(10))).Code);

    [Fact]
    public void A_registration_change_gives_the_counter_a_new_series_prefix()
    {
        var counter = Counter.Create(Guid.NewGuid(), Guid.NewGuid(), "BILL01", "Front", Now);
        Assert.Equal(["BILL01", "BILL01B", "BILL01C"], Enumerable.Range(0, 3).Select(counter.InvoicePrefix));
        Assert.True(Counter.InvoiceNumber(counter.InvoicePrefix(25 - 1), 99_999_999).Length <= 16);
    }
}
