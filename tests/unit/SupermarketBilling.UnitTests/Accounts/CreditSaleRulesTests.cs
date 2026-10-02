using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.UnitTests.Accounts;

public sealed class CreditSaleRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Credit_is_not_change_so_on_account_cannot_exceed_the_bill()
    {
        Assert.Equal(0m, PaymentRules.ChangeDue(100m, [new PaymentInput(PaymentMethods.Cash, 40m, null), new PaymentInput(PaymentMethods.OnAccount, 60m, null)]));
        Assert.Equal("payment.overpaid_non_cash", Assert.Throws<DomainException>(() =>
            PaymentRules.ChangeDue(100m, [new PaymentInput(PaymentMethods.OnAccount, 110m, null)])).Code);
        Assert.Equal(10m, PaymentRules.ChangeDue(100m, [new PaymentInput(PaymentMethods.OnAccount, 50m, null), new PaymentInput(PaymentMethods.Cash, 60m, null)]));
    }

    [Fact]
    public void Cash_taken_from_debtors_at_the_counter_belongs_in_the_drawer() =>
        Assert.Equal(1450m, ShiftCash.Expected(openingFloat: 500, cashTendered: 1000, changeGiven: 50, cashRefunded: 100, payIns: 0, payOuts: 200, drops: 0, cashReceived: 300));

    [Fact]
    public void A_receipt_needs_a_known_method_a_positive_amount_and_a_cheque_number_for_cheques()
    {
        DebtorReceipt Receipt(string method, decimal amount, string? reference) => DebtorReceipt.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "MAIN/RCT/000001", 1, new DateOnly(2026, 10, 2), method, reference, amount, null, null, Guid.NewGuid(), "k", "h", Now);
        Assert.Null(Receipt(ReceiptMethods.Cash, 100m, null).ShiftId);
        Assert.Equal("receipt.cheque_number_required", Assert.Throws<DomainException>(() => Receipt(ReceiptMethods.Cheque, 100m, " ")).Code);
        Assert.Equal("receipt.method_invalid", Assert.Throws<DomainException>(() => Receipt("ON_ACCOUNT", 100m, null)).Code);
        Assert.Equal("receipt.amount_invalid", Assert.Throws<DomainException>(() => Receipt(ReceiptMethods.Upi, 0.001m, null)).Code);
    }

    [Fact]
    public void Approvals_to_go_over_a_credit_limit_carry_the_amount_allowed()
    {
        var approval = SupervisorApproval.Grant(Guid.NewGuid(), Guid.NewGuid(), SupervisorApprovalKinds.CreditLimit, null, null, 250m, "Pays monthly",
            Guid.NewGuid(), Guid.NewGuid(), new byte[32], Now, TimeSpan.FromMinutes(10));
        Assert.Equal(250m, approval.MaxAmount);
        Assert.Equal("approval.amount_invalid", Assert.Throws<DomainException>(() => SupervisorApproval.Grant(Guid.NewGuid(), Guid.NewGuid(),
            SupervisorApprovalKinds.CreditLimit, null, null, null, "No amount", Guid.NewGuid(), Guid.NewGuid(), new byte[32], Now, TimeSpan.FromMinutes(10))).Code);
    }
}
