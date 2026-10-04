using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;

namespace SupermarketBilling.IntegrationTests.Accounts;

[Collection(ApiTestGroup.Name)]
public sealed class CustodyTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    private string Base => $"/api/v1/businesses/{Business}";

    private static async Task<T> SendAsync<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    private static FieldCollectionRequest Collect(Guid debtorId, string method, decimal amount, string? reference = null) =>
        new(debtorId, method, amount, reference, method == "CHEQUE" ? "State Bank" : null, IdempotencyKey: Guid.NewGuid().ToString("N"));

    /// <summary>A collector (collection person) with one party of theirs owing Rs. 1,000 since 1 September.</summary>
    private async Task<(TestClient Collector, Guid CollectorId, DebtorDto Debtor)> CollectorWithPartyAsync(TestClient owner)
    {
        var user = await factory.CreateSignedInUserAsync("collection_person", Store);
        var debtor = await Ledgers.DebtorAsync(owner, Business, opening: 1000m);
        (await owner.PutJsonAsync($"{Base}/debtors/{debtor.Id}/collection-plan",
            new SetCollectionPlanRequest(null, null, user.UserId, null, null, null, "MANUAL"))).EnsureSuccessStatusCode();
        return (user.Client, user.UserId, debtor);
    }

    [Fact]
    public async Task A_round_collects_cash_and_cheques_is_handed_over_blind_and_counted_by_someone_else()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (collectorClient, _, debtor) = await CollectorWithPartyAsync(owner);
        using var collector = collectorClient;
        var stranger = await Ledgers.DebtorAsync(owner, Business, opening: 100m);

        Assert.Equal("session.not_open", await (await collector.PostJsonAsync($"{Base}/collections/receipts", Collect(debtor.Id, "CASH", 100m))).ProblemCodeAsync());
        var round = await SendAsync<CollectorSessionDto>(collector.PostJsonAsync($"{Base}/collections/sessions", new OpenCollectorSessionRequest(Store)));
        Assert.Equal("OPEN", round.Status);

        // Cash pays the oldest bill; a cheque goes into the register.
        var cash = await SendAsync<DebtorReceiptDto>(collector.PostJsonAsync($"{Base}/collections/receipts", Collect(debtor.Id, "CASH", 300m)));
        Assert.Equal((round.Id, 300m, 700m), (cash.CollectorSessionId!.Value, cash.AppliedTo.Single().Amount, cash.BalanceAfter));
        var chequeReceipt = await SendAsync<DebtorReceiptDto>(collector.PostJsonAsync($"{Base}/collections/receipts", Collect(debtor.Id, "CHEQUE", 500m, "004512")));
        Assert.Equal(("RECEIVED", 200m), (chequeReceipt.ChequeStatus, chequeReceipt.BalanceAfter));

        // Only one's own parties, and the oldest bills first unless allowed to choose.
        Assert.Equal(HttpStatusCode.Forbidden, (await collector.PostJsonAsync($"{Base}/collections/receipts", Collect(stranger.Id, "CASH", 10m))).StatusCode);
        var bill = (await Ledgers.OpenItemsAsync(owner, Business, "debtors", debtor.Id)).Charges.Single();
        Assert.Equal(HttpStatusCode.Forbidden, (await collector.PostJsonAsync($"{Base}/collections/receipts",
            Collect(debtor.Id, "CASH", 10m) with { Allocations = [new SettlementAllocation(bill.EntryId, 10m)] })).StatusCode);

        // Blind handover: the collector declares Rs. 300 without seeing what is expected; the round then takes no more.
        var handed = await SendAsync<CollectorSessionDto>(collector.PostJsonAsync($"{Base}/collections/sessions/{round.Id}/handover",
            new HandOverRequest([new CashCount(200, 1), new CashCount(100, 1)])));
        Assert.Equal(("HANDED_OVER", (decimal?)null, (decimal?)300m), (handed.Status, handed.ExpectedCash, handed.DeclaredCash));
        Assert.Equal("CHEQUE", Assert.Single(handed.Instruments).Kind);
        Assert.Equal("session.not_open", await (await collector.PostJsonAsync($"{Base}/collections/receipts", Collect(debtor.Id, "CASH", 10m))).ProblemCodeAsync());

        // The receiver counts Rs. 250: a shortage of Rs. 50 must be explained. The collector cannot confirm their own round.
        Assert.Equal(HttpStatusCode.Forbidden, (await collector.PostJsonAsync($"{Base}/collections/sessions/{round.Id}/confirm",
            new ConfirmHandoverRequest([new CashCount(200, 1), new CashCount(100, 1)]))).StatusCode);
        var shortCount = new ConfirmHandoverRequest([new CashCount(200, 1), new CashCount(50, 1)]);
        Assert.Equal("session.variance_note_required", await (await owner.PostJsonAsync($"{Base}/collections/sessions/{round.Id}/confirm", shortCount)).ProblemCodeAsync());
        var confirmed = await SendAsync<CollectorSessionDto>(owner.PostJsonAsync($"{Base}/collections/sessions/{round.Id}/confirm",
            shortCount with { Note = "Rs. 50 short, collector to repay" }));
        Assert.Equal(("CONFIRMED", 300m, 250m, -50m), (confirmed.Status, confirmed.ExpectedCash!.Value, confirmed.CountedCash!.Value, confirmed.Variance!.Value));
    }

    [Fact]
    public async Task A_bounced_cheque_reverses_its_receipt_so_the_bills_are_owed_again_and_a_new_payment_replaces_it()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (collectorClient, _, debtor) = await CollectorWithPartyAsync(owner);
        using var collector = collectorClient;
        await SendAsync<CollectorSessionDto>(collector.PostJsonAsync($"{Base}/collections/sessions", new OpenCollectorSessionRequest(Store)));
        var receipt = await SendAsync<DebtorReceiptDto>(collector.PostJsonAsync($"{Base}/collections/receipts", Collect(debtor.Id, "CHEQUE", 600m, "778899")));
        var cheque = (await owner.GetJsonAsync<List<ChequeDto>>($"{Base}/cheques?debtorId={debtor.Id}")).Single();

        Assert.Equal("cheque.move_invalid", await (await owner.PostJsonAsync($"{Base}/cheques/{cheque.Id}/move", new MoveChequeRequest("CLEARED"))).ProblemCodeAsync());
        await SendAsync<ChequeDto>(owner.PostJsonAsync($"{Base}/cheques/{cheque.Id}/move", new MoveChequeRequest("DEPOSITED")));
        var bounced = await SendAsync<ChequeDto>(owner.PostJsonAsync($"{Base}/cheques/{cheque.Id}/move", new MoveChequeRequest("BOUNCED", Note: "Insufficient funds")));
        Assert.Equal(["RECEIVED", "DEPOSITED", "BOUNCED"], bounced.History.Select(h => h.Status));

        // Owed again, and the opening balance is unpaid again (not a new bill).
        var open = await Ledgers.OpenItemsAsync(owner, Business, "debtors", debtor.Id);
        Assert.Equal((1000m, 1000m), (open.Balance, Assert.Single(open.Charges).Remaining));
        var reversed = (await owner.GetJsonAsync<List<DebtorReceiptDto>>($"{Base}/debtor-receipts?debtorId={debtor.Id}")).Single(r => r.Id == receipt.Id);
        Assert.Equal(("BOUNCED", "Insufficient funds", 0m), (reversed.ReversalKind, reversed.ReversalReason, reversed.Unapplied));
        Assert.Empty(reversed.AppliedTo);

        // The debtor pays by UPI instead: the bounced cheque is marked replaced.
        var upi = await SendAsync<DebtorReceiptDto>(owner.PostJsonAsync($"{Base}/debtor-receipts",
            new DebtorReceiptRequest(debtor.Id, "UPI", 600m, Store, "UPI-55", IdempotencyKey: Guid.NewGuid().ToString("N"))));
        var replaced = await SendAsync<ChequeDto>(owner.PostJsonAsync($"{Base}/cheques/{cheque.Id}/move", new MoveChequeRequest("REPLACED", ReplacedByReceiptId: upi.Id)));
        Assert.Equal(("REPLACED", upi.Number), (replaced.Status, replaced.ReplacedByReceiptNumber));
        Assert.Equal(400m, (await Ledgers.OpenItemsAsync(owner, Business, "debtors", debtor.Id)).Balance);
    }

    [Fact]
    public async Task A_receipt_recorded_in_error_is_reversed_only_with_another_persons_approval()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var debtor = await Ledgers.DebtorAsync(owner, Business, opening: 500m);
        var wrong = await SendAsync<DebtorReceiptDto>(owner.PostJsonAsync($"{Base}/debtor-receipts",
            new DebtorReceiptRequest(debtor.Id, "CASH", 200m, Store, IdempotencyKey: Guid.NewGuid().ToString("N"))));
        Assert.Equal(300m, wrong.BalanceAfter);

        var asked = await SendAsync<ReverseReceiptResponse>(owner.PostJsonAsync($"{Base}/debtor-receipts/{wrong.Id}/reversal", new ReverseReceiptRequest("Entered on the wrong account")));
        Assert.Equal(300m, (await Ledgers.OpenItemsAsync(owner, Business, "debtors", debtor.Id)).Balance); // nothing until approved
        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        (await approver.PostJsonAsync($"/api/v1/approvals/{asked.ApprovalRequestId}/approve", new ApprovalDecisionRequest("Checked"))).EnsureSuccessStatusCode();
        Assert.Equal(500m, (await Ledgers.OpenItemsAsync(owner, Business, "debtors", debtor.Id)).Balance);
        Assert.Equal("reversal.exists", await (await owner.PostJsonAsync($"{Base}/debtor-receipts/{wrong.Id}/reversal", new ReverseReceiptRequest("Again please"))).ProblemCodeAsync());

        var cheque = await SendAsync<DebtorReceiptDto>(owner.PostJsonAsync($"{Base}/debtor-receipts",
            new DebtorReceiptRequest(debtor.Id, "CHEQUE", 100m, Store, "123", IdempotencyKey: Guid.NewGuid().ToString("N"))));
        Assert.Equal("reversal.use_cheque", await (await owner.PostJsonAsync($"{Base}/debtor-receipts/{cheque.Id}/reversal", new ReverseReceiptRequest("Wrong"))).ProblemCodeAsync());
    }

    [Fact]
    public async Task A_handover_racing_collections_never_misses_a_receipt_in_its_expected_cash()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (collectorClient, _, debtor) = await CollectorWithPartyAsync(owner);
        using var collector = collectorClient;
        var round = await SendAsync<CollectorSessionDto>(collector.PostJsonAsync($"{Base}/collections/sessions", new OpenCollectorSessionRequest(Store)));

        // Warm up and open enough connections, so the requests below really overlap.
        await SendAsync<DebtorReceiptDto>(collector.PostJsonAsync($"{Base}/collections/receipts", Collect(debtor.Id, "CASH", 1m)));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => collector.GetAsync($"{Base}/collections/session")));

        var collecting = Enumerable.Range(0, 6).Select(_ => collector.PostJsonAsync($"{Base}/collections/receipts", Collect(debtor.Id, "CASH", 10m))).ToList();
        var handover = collector.PostJsonAsync($"{Base}/collections/sessions/{round.Id}/handover", new HandOverRequest([]));
        var responses = await Task.WhenAll(collecting);
        await (await handover).EnsureSuccessWithBodyAsync();
        foreach (var response in responses.Where(r => !r.IsSuccessStatusCode))
        {
            Assert.Equal("session.not_open", await response.ProblemCodeAsync());
        }

        var saved = responses.Count(r => r.IsSuccessStatusCode);
        var confirmed = await SendAsync<CollectorSessionDto>(owner.PostJsonAsync($"{Base}/collections/sessions/{round.Id}/confirm",
            new ConfirmHandoverRequest([new CashCount(1, 1 + (10 * saved))])));
        Assert.Equal((1m + (10 * saved), 0m), (confirmed.ExpectedCash!.Value, confirmed.Variance!.Value));
        Assert.Equal(1 + saved, confirmed.Receipts);
    }

    [Fact]
    public async Task Database_holds_reversals_and_rounds_and_verification_reconciles_them()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var debtor = await Ledgers.DebtorAsync(owner, Business, opening: 300m);
        var receipt = await SendAsync<DebtorReceiptDto>(owner.PostJsonAsync($"{Base}/debtor-receipts",
            new DebtorReceiptRequest(debtor.Id, "CHEQUE", 300m, Store, "991", IdempotencyKey: Guid.NewGuid().ToString("N"))));
        var cheque = (await owner.GetJsonAsync<List<ChequeDto>>($"{Base}/cheques?debtorId={debtor.Id}")).Single();
        await SendAsync<ChequeDto>(owner.PostJsonAsync($"{Base}/cheques/{cheque.Id}/move", new MoveChequeRequest("CANCELLED", Note: "Given back unsigned")));

        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);

        // Taking back more than was settled is refused even when written directly.
        await using (var forged = new NpgsqlCommand(
                         "INSERT INTO debtor_settlements (id, tenant_id, business_id, debtor_id, charge_entry_id, payment_entry_id, amount, created_at_utc) " +
                         "SELECT gen_random_uuid(), tenant_id, business_id, debtor_id, charge_entry_id, payment_entry_id, -1, now() FROM debtor_settlements " +
                         "WHERE debtor_id = @id AND amount < 0 LIMIT 1", admin))
        {
            forged.Parameters.AddWithValue("id", debtor.Id);
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync())).SqlState);
        }

        await using (var app = await factory.OpenAppConnectionAsync())
        {
            await using var command = new NpgsqlCommand("UPDATE cheques SET status = 'RECEIVED' WHERE id = @id", app);
            command.Parameters.AddWithValue("id", cheque.Id);
            Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }

        var sql12 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "012_custody.sql"));
        await using (var verify = new NpgsqlCommand(sql12, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand(
                         "ALTER TABLE receipt_reversals DISABLE TRIGGER trg_receipt_reversals_no_update_delete; " +
                         "DELETE FROM receipt_reversals WHERE receipt_id = @id; " +
                         "ALTER TABLE receipt_reversals ENABLE TRIGGER trg_receipt_reversals_no_update_delete;", admin, transaction))
        {
            tamper.Parameters.AddWithValue("id", receipt.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql12, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("reversal entries without a recorded reversal", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }
}
