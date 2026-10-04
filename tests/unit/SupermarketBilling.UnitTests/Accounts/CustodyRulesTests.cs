using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.UnitTests.Accounts;

public sealed class CustodyRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("RECEIVED", "DEPOSITED", true)]
    [InlineData("RECEIVED", "CANCELLED", true)]
    [InlineData("DEPOSITED", "CLEARED", true)]
    [InlineData("DEPOSITED", "BOUNCED", true)]
    [InlineData("BOUNCED", "REPLACED", true)]
    [InlineData("RECEIVED", "CLEARED", false)]
    [InlineData("CLEARED", "BOUNCED", false)]
    [InlineData("BOUNCED", "DEPOSITED", false)]
    [InlineData("REPLACED", "RECEIVED", false)]
    public void Cheques_only_make_the_allowed_moves(string from, string to, bool allowed) => Assert.Equal(allowed, ChequeStatus.CanMove(from, to));

    [Fact]
    public void A_handover_is_confirmed_by_someone_else_and_a_difference_needs_a_reason()
    {
        var collector = Guid.NewGuid();
        var round = CollectorSession.Open(Guid.NewGuid(), Guid.NewGuid(), collector, new DateOnly(2026, 10, 4), Now);
        Assert.Equal("session.not_handed_over", Assert.Throws<DomainException>(() => round.Confirm(Guid.NewGuid(), 100m, null, Now)).Code);
        round.HandOver(expected: 500m, declared: 500m, Now);
        Assert.Equal("session.not_open", Assert.Throws<DomainException>(() => round.HandOver(500m, 500m, Now)).Code);
        Assert.Equal("session.self_confirm", Assert.Throws<DomainException>(() => round.Confirm(collector, 500m, null, Now)).Code);
        Assert.Equal("session.variance_note_required", Assert.Throws<DomainException>(() => round.Confirm(Guid.NewGuid(), 480m, " ", Now)).Code);
        round.Confirm(Guid.NewGuid(), 480m, "Rs. 20 short", Now);
        Assert.Equal((CollectorSessionStatus.Confirmed, -20m), (round.Status, round.Variance));
    }

    [Fact]
    public void Drafts_need_a_number_other_methods_say_what_they_were_and_counter_and_field_do_not_mix()
    {
        DebtorReceipt Receipt(string method, string? reference, DebtorReceipt.AtCounter? counter = null, Guid? session = null) => DebtorReceipt.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "MAIN/RCT/000001", 1, new DateOnly(2026, 10, 4), method, reference, 100m, null, counter,
            Guid.NewGuid(), "k", "h", Now, session);
        Assert.Equal("receipt.cheque_number_required", Assert.Throws<DomainException>(() => Receipt(ReceiptMethods.DemandDraft, null)).Code);
        Assert.Equal("receipt.reference_required", Assert.Throws<DomainException>(() => Receipt(ReceiptMethods.Other, null)).Code);
        Assert.Equal("receipt.place_invalid", Assert.Throws<DomainException>(() =>
            Receipt(ReceiptMethods.Cash, null, new DebtorReceipt.AtCounter(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid())).Code);
        var draft = Receipt(ReceiptMethods.DemandDraft, "DD-99");
        Assert.Equal(("DEMAND_DRAFT", ChequeStatus.Received), (Cheque.Receive(Guid.NewGuid(), draft, "Canara", null, Now).Kind, ChequeStatus.Received));
        Assert.Equal("cheque.not_an_instrument", Assert.Throws<DomainException>(() => Cheque.Receive(Guid.NewGuid(), Receipt(ReceiptMethods.Upi, "x"), null, null, Now)).Code);
    }

    [Fact]
    public void Undoing_a_settlement_is_stored_as_a_negative_amount_and_reversals_need_a_reason()
    {
        Assert.Equal(-40m, DebtorSettlement.Undo(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 40m, Now).Amount);
        Assert.Equal("settlement.amount_invalid", Assert.Throws<DomainException>(() => DebtorSettlement.Undo(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0m, Now)).Code);
        Assert.Equal(1, LedgerEntryTypes.Sign(LedgerEntryTypes.ReceiptReversal));
        Assert.Equal("reversal.reason_required", Assert.Throws<DomainException>(() =>
            ReceiptReversal.Record(Guid.NewGuid(), Guid.NewGuid(), ReversalKinds.Bounced, " ", null, Guid.NewGuid(), Now)).Code);
    }
}
