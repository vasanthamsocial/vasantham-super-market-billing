using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;
using SupermarketBilling.IntegrationTests.Purchases;

namespace SupermarketBilling.IntegrationTests.Accounts;

internal static class Ledgers
{
    public static async Task<SupplierPaymentDto> PayAsync(TestClient client, Guid businessId, SupplierPaymentRequest request)
    {
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/supplier-payments", request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<SupplierPaymentDto>(TestClient.Json))!;
    }

    public static SupplierPaymentRequest Payment(Guid storeId, Guid supplierId, decimal amount, params SettlementAllocation[] allocations) =>
        new(storeId, supplierId, "BANK_TRANSFER", amount, "UTR123", null, allocations, Guid.NewGuid().ToString("N"));

    public static Task<OpenItemsDto> OpenItemsAsync(TestClient client, Guid businessId, string collection, Guid partyId) =>
        client.GetJsonAsync<OpenItemsDto>($"/api/v1/businesses/{businessId}/{collection}/{partyId}/open-items");

    public static async Task<DebtorDto> DebtorAsync(TestClient client, Guid businessId, decimal? opening = null, decimal creditLimit = 5000m)
    {
        var code = $"D{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/debtors",
            new CreateDebtorRequest(code, $"Debtor {code}", null, null, "33", WhatsAppNumber: "98765 43210", WhatsAppConsent: true, CreditPeriodDays: 15,
                CreditLimit: creditLimit, OpeningBalance: opening, OpeningBalanceDate: new DateOnly(2026, 9, 1)));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<DebtorDto>(TestClient.Json))!;
    }
}

[Collection(ApiTestGroup.Name)]
public sealed class AccountsTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    [Fact]
    public async Task Receipts_are_owed_to_the_supplier_and_payments_settle_the_oldest_due_first_leaving_an_advance()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var code = $"S{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var created = await owner.PostJsonAsync($"/api/v1/businesses/{Business}/suppliers",
            new CreateSupplierRequest(code, $"Supplier {code}", null, "33", null, null, CreditPeriodDays: 15, OpeningBalance: 500m, OpeningBalanceDate: new DateOnly(2026, 8, 1)));
        await created.EnsureSuccessWithBodyAsync();
        var supplier = (await created.Content.ReadFromJsonAsync<SupplierDto>(TestClient.Json))!;
        Assert.Equal((500m, 500m, 15), (supplier.Balance, supplier.Overdue, supplier.CreditPeriodDays)); // the opening balance was due on 1 August

        // The receipt is owed, due 15 days after the supplier's invoice date.
        var grn = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 40m)));
        var statement = await owner.GetJsonAsync<StatementDto>($"/api/v1/businesses/{Business}/suppliers/{supplier.Id}/statement");
        Assert.Equal(["OPENING", "GRN"], statement.Entries.Select(e => e.EntryType));
        var bill = statement.Entries[1];
        Assert.Equal((grn.Number, 400m, 900m, new DateOnly(2026, 10, 15)), (bill.DocumentNumber, bill.Amount, bill.BalanceAfter, bill.DueDate));
        Assert.Equal("ledger.opening_not_first", await (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/suppliers/{supplier.Id}/opening-balance",
            new OpeningBalanceRequest(10m, new DateOnly(2026, 8, 1)))).ProblemCodeAsync());

        // 600 paid: the opening balance (due first) is settled, then 100 of the receipt.
        var payment = await Ledgers.PayAsync(owner, Business, Ledgers.Payment(Store, supplier.Id, 600m));
        Assert.Matches(@"/PMT/\d{6}$", payment.Number);
        Assert.Equal([("OPENING", 500m), ("GRN", 100m)], payment.AppliedTo.Select(a => (a.EntryType, a.Amount)));
        var open = await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id);
        Assert.Equal((300m, 300m), (open.Balance, Assert.Single(open.Charges).Remaining));

        // Paying too much leaves an advance, which the next receipt uses up.
        var over = await Ledgers.PayAsync(owner, Business, Ledgers.Payment(Store, supplier.Id, 1000m));
        Assert.Equal((300m, 700m), (over.AppliedTo.Sum(a => a.Amount), over.Unapplied));
        await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 40m)));
        open = await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id);
        Assert.Equal((-300m, 0, 300m), (open.Balance, open.Charges.Count, Assert.Single(open.UnappliedPayments).Remaining));

        // A retried payment (same key) is the same payment; changed details under that key are refused.
        var request = Ledgers.Payment(Store, supplier.Id, 50m);
        var first = await Ledgers.PayAsync(owner, Business, request);
        Assert.Equal(first.Id, (await Ledgers.PayAsync(owner, Business, request)).Id);
        Assert.Equal("idempotency.mismatch", await (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/supplier-payments", request with { Amount = 60m })).ProblemCodeAsync());
        Assert.Equal("payment.cheque_number_required", await (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/supplier-payments",
            Ledgers.Payment(Store, supplier.Id, 10m) with { Method = "CHEQUE", Reference = null })).ProblemCodeAsync());
    }

    [Fact]
    public async Task A_payment_can_name_the_bills_it_pays()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 1, 100m)));
        await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 2, 100m)));
        var charges = (await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id)).Charges;
        var second = charges.Single(c => c.Amount == 200m);

        var paid = await Ledgers.PayAsync(owner, Business, Ledgers.Payment(Store, supplier.Id, 150m, new SettlementAllocation(second.EntryId, 150m)));
        Assert.Equal((second.EntryId, 150m), (Assert.Single(paid.AppliedTo).ChargeEntryId, paid.AppliedTo[0].Amount));
        var refused = await owner.PostJsonAsync($"/api/v1/businesses/{Business}/supplier-payments",
            Ledgers.Payment(Store, supplier.Id, 100m, new SettlementAllocation(second.EntryId, 60m)));
        Assert.Equal("settlement.too_much", await refused.ProblemCodeAsync());
    }

    [Fact]
    public async Task Debtor_accounts_are_corrected_only_with_another_persons_approval_and_close_at_zero()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var debtor = await Ledgers.DebtorAsync(owner, Business, opening: 1000m);
        Assert.Equal(("+919876543210", true, 1000m, "ACTIVE"), (debtor.WhatsAppNumber, debtor.WhatsAppConsent, debtor.Balance, debtor.Status));
        Assert.NotNull(debtor.ConsentChangedAtUtc);
        Assert.Contains(await owner.GetJsonAsync<List<DebtorDto>>($"/api/v1/businesses/{Business}/debtors?search=98765"), d => d.Id == debtor.Id);

        UpdateDebtorRequest Close(DebtorDto d) => new(d.LegalName, d.TradeName, d.Gstin, d.StateCode, d.Address, d.ContactPerson, d.Phone, d.Email, d.WhatsAppNumber,
            d.SmsNumber, d.WhatsAppConsent, d.SmsConsent, d.CreditPeriodDays, d.CreditLimit, d.CustomerGroupId, "CLOSED", d.RowVersion);
        Assert.Equal("debtor.balance_not_zero", await (await owner.PutJsonAsync($"/api/v1/businesses/{Business}/debtors/{debtor.Id}", Close(debtor))).ProblemCodeAsync());

        // A correction waits for someone else; the requester cannot approve it.
        var asked = await owner.PostJsonAsync($"/api/v1/businesses/{Business}/debtors/{debtor.Id}/adjustments", new LedgerAdjustmentRequest(-1000m, "Settled before go-live"));
        await asked.EnsureSuccessWithBodyAsync();
        var approval = (await asked.Content.ReadFromJsonAsync<LedgerAdjustmentResponse>(TestClient.Json))!.ApprovalRequestId;
        Assert.Equal(1000m, (await owner.GetJsonAsync<DebtorDto>($"/api/v1/businesses/{Business}/debtors/{debtor.Id}")).Balance);
        Assert.False((await owner.PostJsonAsync($"/api/v1/approvals/{approval}/approve", new ApprovalDecisionRequest("own"))).IsSuccessStatusCode);
        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        (await approver.PostJsonAsync($"/api/v1/approvals/{approval}/approve", new ApprovalDecisionRequest("Checked the old books"))).EnsureSuccessStatusCode();

        var statement = await owner.GetJsonAsync<StatementDto>($"/api/v1/businesses/{Business}/debtors/{debtor.Id}/statement");
        Assert.Equal([("OPENING", 1000m), ("ADJUSTMENT", -1000m)], statement.Entries.Select(e => (e.EntryType, e.Amount)));
        Assert.Equal(0m, statement.ClosingBalance);
        debtor = await owner.GetJsonAsync<DebtorDto>($"/api/v1/businesses/{Business}/debtors/{debtor.Id}");
        var closed = await owner.PutJsonAsync($"/api/v1/businesses/{Business}/debtors/{debtor.Id}", Close(debtor));
        await closed.EnsureSuccessWithBodyAsync();
        Assert.Equal("CLOSED", (await closed.Content.ReadFromJsonAsync<DebtorDto>(TestClient.Json))!.Status);
    }

    [Fact]
    public async Task Only_those_allowed_see_debtors_pay_suppliers_or_enter_balances()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        using (var client = cashier.Client)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/businesses/{Business}/debtors")).StatusCode);
        }

        var buyer = await factory.CreateSignedInUserAsync("purchase_operator", Store);
        using (var client = buyer.Client)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync($"/api/v1/businesses/{Business}/supplier-payments",
                Ledgers.Payment(Store, supplier.Id, 10m))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync($"/api/v1/businesses/{Business}/suppliers/{supplier.Id}/opening-balance",
                new OpeningBalanceRequest(10m, new DateOnly(2026, 9, 1)))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/businesses/{Business}/suppliers/{supplier.Id}/statement")).StatusCode);
        }
    }

    [Fact]
    public async Task Concurrent_payments_cannot_pay_one_bill_twice_and_entries_stay_chained()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 3, 100m)));
        var bill = Assert.Single((await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id)).Charges);

        // Warm up the payment path and open enough connections, so the requests below really overlap.
        await Ledgers.PayAsync(owner, Business, Ledgers.Payment(Store, supplier.Id, 1m, new SettlementAllocation(bill.EntryId, 1m)));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => owner.GetAsync($"/api/v1/businesses/{Business}/suppliers/{supplier.Id}")));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostJsonAsync($"/api/v1/businesses/{Business}/supplier-payments",
            Ledgers.Payment(Store, supplier.Id, 100m, new SettlementAllocation(bill.EntryId, 100m)))));
        var paid = 0;
        foreach (var response in responses)
        {
            if (response.IsSuccessStatusCode)
            {
                paid++;
            }
            else
            {
                Assert.Equal("settlement.too_much", await response.ProblemCodeAsync());
            }
        }

        Assert.Equal(2, paid); // 1 + 100 + 100 of 300; a third 100 would exceed the 199 then left
        var statement = await owner.GetJsonAsync<StatementDto>($"/api/v1/businesses/{Business}/suppliers/{supplier.Id}/statement");
        Assert.Equal(Enumerable.Range(1, statement.Entries.Count).Select(i => (long)i), statement.Entries.Select(e => e.Sequence));
        Assert.Equal(99m, statement.ClosingBalance);
    }

    [Fact]
    public async Task Receipts_and_payments_posting_to_one_supplier_at_once_all_land_in_order()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        GrnRequest Receipt() => Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 1, 100m));

        // Warm up both paths and open enough connections, so the requests below really overlap.
        await Purchasing.SaveAsync(owner, Business, Receipt());
        await Ledgers.PayAsync(owner, Business, Ledgers.Payment(Store, supplier.Id, 50m));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => owner.GetAsync($"/api/v1/businesses/{Business}/suppliers/{supplier.Id}")));

        // Receipts and payments use different number series, so only the account lock orders them.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => i % 2 == 0
            ? owner.PostJsonAsync($"/api/v1/businesses/{Business}/grns", Receipt())
            : owner.PostJsonAsync($"/api/v1/businesses/{Business}/supplier-payments", Ledgers.Payment(Store, supplier.Id, 50m))));
        foreach (var response in responses)
        {
            await response.EnsureSuccessWithBodyAsync();
        }

        var statement = await owner.GetJsonAsync<StatementDto>($"/api/v1/businesses/{Business}/suppliers/{supplier.Id}/statement");
        Assert.Equal(10, statement.Entries.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), statement.Entries.Select(e => e.Sequence).Order());
        Assert.Equal(250m, statement.ClosingBalance); // 5 x 100 owed, 5 x 50 paid
        var open = await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id);
        Assert.Equal((250m, 0), (open.Charges.Sum(c => c.Remaining), open.UnappliedPayments.Count));
    }

    [Fact]
    public async Task Database_keeps_accounts_append_only_chained_and_verification_reconciles_them()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 2, 100m)));
        var payment = await Ledgers.PayAsync(owner, Business, Ledgers.Payment(Store, supplier.Id, 150m));

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[]
                     {
                         "UPDATE supplier_ledger SET amount = 1 WHERE supplier_id = @id", "DELETE FROM supplier_settlements WHERE supplier_id = @id",
                         "UPDATE supplier_payments SET amount = 1 WHERE supplier_id = @id",
                     })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("id", supplier.Id);
                Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
        }

        var sql8 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "008_accounts.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql8, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        // An entry that does not follow the previous balance is refused even by the superuser.
        await using (var forged = new NpgsqlCommand(
                         "INSERT INTO supplier_ledger (id, tenant_id, business_id, supplier_id, sequence, entry_type, entry_date, due_date, amount, balance_after, narration, " +
                         "created_by_user_id, created_at_utc) SELECT gen_random_uuid(), tenant_id, business_id, supplier_id, sequence + 1, 'ADJUSTMENT', entry_date, entry_date, " +
                         "10, balance_after + 999, 'forged', created_by_user_id, now() FROM supplier_ledger WHERE supplier_id = @id ORDER BY sequence DESC LIMIT 1", admin))
        {
            forged.Parameters.AddWithValue("id", supplier.Id);
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync())).SqlState);
        }

        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand(
                         "ALTER TABLE supplier_settlements DISABLE TRIGGER trg_supplier_settlements_no_update_delete; " +
                         "UPDATE supplier_settlements SET amount = amount + 500 WHERE supplier_id = @id; " +
                         "ALTER TABLE supplier_settlements ENABLE TRIGGER trg_supplier_settlements_no_update_delete;", admin, transaction))
        {
            tamper.Parameters.AddWithValue("id", supplier.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql8, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("settled beyond their amount", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
        Assert.Equal(150m, payment.Amount);
    }
}
