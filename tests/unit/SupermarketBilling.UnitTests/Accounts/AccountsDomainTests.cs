using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.UnitTests.Accounts;

public sealed class AccountsDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static PartyDetails Details(string? whatsApp = null, bool whatsAppConsent = false, int creditDays = 0) =>
        new("Lakshmi Stores", null, null, "33", null, null, null, null, whatsApp, null, whatsAppConsent, false, creditDays);

    [Theory]
    [InlineData("9876543210", "+919876543210")]
    [InlineData("+91 98765-43210", "+919876543210")]
    [InlineData("09876543210", "+919876543210")]
    [InlineData("919876543210", "+919876543210")]
    public void Mobile_numbers_are_stored_as_plus_91_and_ten_digits(string given, string expected) =>
        Assert.Equal(expected, PartyDetails.Mobile(given, "x", "The number"));

    [Theory]
    [InlineData("5876543210")]
    [InlineData("98765")]
    [InlineData("98765abc10")]
    public void Other_numbers_are_refused(string given) =>
        Assert.Equal("x", Assert.Throws<DomainException>(() => PartyDetails.Mobile(given, "x", "The number")).Code);

    [Fact]
    public void Consent_needs_a_number_and_its_change_is_timestamped()
    {
        Assert.Equal("debtor.whatsapp_required", Assert.Throws<DomainException>(() => Details(whatsAppConsent: true).Normalize("debtor")).Code);
        var debtor = Debtor.Create(Guid.NewGuid(), "d-1", Details(), 5000m, null, Now);
        Assert.Equal(("D-1", (DateTimeOffset?)null), (debtor.Code, debtor.ConsentChangedAtUtc));
        debtor.Update(Details("9876543210", whatsAppConsent: true, creditDays: 30), 5000m, null, Now.AddDays(1));
        Assert.Equal((true, Now.AddDays(1), 30), (debtor.WhatsAppConsent, debtor.ConsentChangedAtUtc, debtor.CreditPeriodDays));
        Assert.Equal("debtor.credit_period_invalid", Assert.Throws<DomainException>(() => debtor.Update(Details(creditDays: 400), 0, null, Now)).Code);
        Assert.Equal("debtor.credit_limit_invalid", Assert.Throws<DomainException>(() => debtor.Update(Details(), -1m, null, Now)).Code);
    }

    [Fact]
    public void A_debtor_account_closes_only_at_zero()
    {
        var debtor = Debtor.Create(Guid.NewGuid(), "D1", Details(), 0, null, Now);
        Assert.Equal("debtor.balance_not_zero", Assert.Throws<DomainException>(() => debtor.SetStatus(DebtorStatus.Closed, 10m)).Code);
        debtor.SetStatus(DebtorStatus.Closed, 0m);
        Assert.Equal(DebtorStatus.Closed, debtor.Status);
    }

    [Fact]
    public void Ledger_entries_chain_and_have_the_right_sign()
    {
        var supplier = Guid.NewGuid();
        var grn = SupplierLedgerEntry.Create(Guid.NewGuid(), supplier, 0, 0,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Grn, null, null, "GRN1", Today, Today.AddDays(30), 1000m, "Receipt"), Guid.NewGuid(), Now);
        Assert.Equal((1L, 1000m, Today.AddDays(30)), (grn.Sequence, grn.BalanceAfter, grn.DueDate));
        var paid = SupplierLedgerEntry.Create(Guid.NewGuid(), supplier, grn.Sequence, grn.BalanceAfter,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Payment, null, null, "PMT1", Today, Today, -400m, "Paid"), Guid.NewGuid(), Now);
        Assert.Equal((2L, 600m, (DateOnly?)null), (paid.Sequence, paid.BalanceAfter, paid.DueDate));

        Assert.Equal("ledger.sign_invalid", Assert.Throws<DomainException>(() => SupplierLedgerEntry.Create(Guid.NewGuid(), supplier, 2, 600,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Payment, null, null, null, Today, null, 50m, "Wrong way"), Guid.NewGuid(), Now)).Code);
        Assert.Equal("ledger.type_invalid", Assert.Throws<DomainException>(() => SupplierLedgerEntry.Create(Guid.NewGuid(), supplier, 2, 600,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Invoice, null, null, null, Today, null, 50m, "Sale"), Guid.NewGuid(), Now)).Code);
        Assert.Equal("ledger.amount_invalid", Assert.Throws<DomainException>(() => DebtorLedgerEntry.Create(Guid.NewGuid(), supplier, 0, 0,
            new PartyLedgerEntry.Posting(LedgerEntryTypes.Adjustment, null, null, null, Today, null, 0.001m, "Paisa"), Guid.NewGuid(), Now)).Code);
    }

    [Fact]
    public void Payments_pay_the_oldest_due_bills_first_and_the_rest_stays_unapplied()
    {
        var older = new OpenItem(Guid.NewGuid(), Today.AddDays(-40), Today.AddDays(-10), 1, 300m);
        var newer = new OpenItem(Guid.NewGuid(), Today.AddDays(-5), Today.AddDays(25), 3, 500m);
        var earlyDue = new OpenItem(Guid.NewGuid(), Today.AddDays(-20), Today.AddDays(-20), 2, 200m);

        var plan = SettlementPlanner.Plan([older, newer, earlyDue], 600m);
        Assert.Equal([(earlyDue.EntryId, 200m), (older.EntryId, 300m), (newer.EntryId, 100m)], plan);
        Assert.Equal(1000m, SettlementPlanner.Plan([older, newer, earlyDue], 5000m).Sum(p => p.Amount)); // 4000 stays as an advance
    }

    [Fact]
    public void Chosen_bills_are_checked_against_what_is_left()
    {
        var bill = new OpenItem(Guid.NewGuid(), Today, Today, 1, 300m);
        Assert.Equal([(bill.EntryId, 100m)], SettlementPlanner.Plan([bill], 500m, [(bill.EntryId, 100m)]));
        Assert.Equal("settlement.too_much", Assert.Throws<DomainException>(() => SettlementPlanner.Plan([bill], 500m, [(bill.EntryId, 301m)])).Code);
        Assert.Equal("settlement.exceeds_payment", Assert.Throws<DomainException>(() => SettlementPlanner.Plan([bill], 50m, [(bill.EntryId, 100m)])).Code);
        Assert.Equal("settlement.not_open", Assert.Throws<DomainException>(() => SettlementPlanner.Plan([bill], 50m, [(Guid.NewGuid(), 10m)])).Code);
        Assert.Equal("payment.cheque_number_required", Assert.Throws<DomainException>(() => SupplierPayment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "X", 1, Today, PartyPaymentMethods.Cheque, " ", 10m, null, Guid.NewGuid(), "k", "h", Now)).Code);
    }
}
