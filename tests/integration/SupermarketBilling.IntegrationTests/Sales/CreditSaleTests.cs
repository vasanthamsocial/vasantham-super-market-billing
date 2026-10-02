using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Accounts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;

namespace SupermarketBilling.IntegrationTests.Sales;

[Collection(ApiTestGroup.Name)]
public sealed class CreditSaleTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    private static PaymentRequest OnAccount(decimal amount) => new("ON_ACCOUNT", amount, null);

    private async Task<Pos.CounterSession> CashierCounterAsync()
    {
        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        cashier.Client.Dispose();
        return await Pos.CounterBrowserAsync(factory, Business, Store, cashier.Username, cashier.Password);
    }

    private static Task<DebtorReceiptDto> ReceiveAtCounterAsync(TestClient browser, Guid debtorId, decimal amount, string method = "CASH") =>
        PostAsync<DebtorReceiptDto>(browser, "/api/v1/pos/debtor-receipts", new DebtorReceiptRequest(debtorId, method, amount, IdempotencyKey: Guid.NewGuid().ToString("N")));

    private static async Task<T> PostAsync<T>(TestClient client, string path, object body)
    {
        var response = await client.PostJsonAsync(path, body);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(TestClient.Json))!;
    }

    [Fact]
    public async Task A_debtor_buys_on_account_with_the_due_date_kept_on_the_invoice_and_pays_at_the_counter_into_the_drawer()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var debtor = await Ledgers.DebtorAsync(owner, Business, creditLimit: 1000m); // 15 days' credit
        var (browser, _) = await CashierCounterAsync();
        using (browser)
        {
            var cart = Pos.Cart(new CartLineRequest(pack, 3)) with { DebtorId = debtor.Id };
            var preview = await Pos.PriceAsync(browser, cart);
            Assert.Equal((1000m, 0m), (preview.Debtor!.Available, preview.Debtor.Balance));

            // Fully on account: the debtor's details go on the invoice, due 15 days after it.
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(cart, 300m, OnAccount(300m)));
            Assert.Equal((debtor.Id, debtor.Code, debtor.DisplayName, 300m), (invoice.DebtorId, invoice.DebtorCode, invoice.BuyerName, invoice.OnAccount));
            Assert.Equal(invoice.BusinessDate.AddDays(15), invoice.DueDate);

            // Part cash, part on account.
            var split = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)) with { DebtorId = debtor.Id }, 200m,
                new PaymentRequest("CASH", 50m, null), OnAccount(150m)));
            Assert.Equal(150m, split.OnAccount);
            Assert.Equal(450m, (await owner.GetJsonAsync<DebtorDto>($"/api/v1/businesses/{Business}/debtors/{debtor.Id}")).Balance);

            // On account needs an account; a later change to the credit period never moves the due date.
            Assert.Equal("payment.debtor_required", await (await browser.PostJsonAsync("/api/v1/pos/invoices",
                Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 100m, OnAccount(100m)))).ProblemCodeAsync());

            // Rs. 400 in cash at the counter pays the oldest invoice, then part of the next; it goes into the drawer.
            var receipt = await ReceiveAtCounterAsync(browser, debtor.Id, 400m);
            Assert.Matches(@"/RCT/\d{6}$", receipt.Number);
            Assert.Equal([(invoice.Number, 300m), (split.Number, 100m)], receipt.AppliedTo.Select(a => (a.DocumentNumber!, a.Amount)));
            Assert.Equal(50m, receipt.BalanceAfter);
            var shift = await browser.GetJsonAsync<ShiftSummaryDto>("/api/v1/pos/shift");
            Assert.Equal(400m, Assert.Single(shift.Receipts!).Amount);
            var closed = await (await browser.PostJsonAsync("/api/v1/pos/shift/close", new CloseShiftRequest([new DenominationCount(50, 9)], null))).Content
                .ReadFromJsonAsync<ShiftSummaryDto>(TestClient.Json);
            Assert.Equal((450m, 0m), (closed!.ExpectedCash, closed.Difference)); // 50 cash on the split bill + 400 received
        }
    }

    [Fact]
    public async Task Going_over_the_credit_limit_needs_a_supervisor_and_an_account_on_hold_gets_no_credit()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var debtor = await Ledgers.DebtorAsync(owner, Business, opening: 450m, creditLimit: 500m);
        var (browser, _) = await CashierCounterAsync();
        using (browser)
        {
            var bill = Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)) with { DebtorId = debtor.Id }, 100m, OnAccount(100m));
            var refused = await browser.PostJsonAsync("/api/v1/pos/invoices", bill);
            Assert.Equal("credit.limit_exceeded", await refused.ProblemCodeAsync()); // 550 owed against 500

            SupervisorApprovalRequest Ask(decimal upTo) => new(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword, null, "CREDIT_LIMIT", null, null, upTo, "Old customer, pays monthly");
            var small = await PostAsync<SupervisorApprovalResponse>(browser, "/api/v1/pos/supervisor-approvals", Ask(20m));
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync("/api/v1/pos/invoices", bill with { CreditApprovalToken = small.Token })).StatusCode);
            var enough = await PostAsync<SupervisorApprovalResponse>(browser, "/api/v1/pos/supervisor-approvals", Ask(50m));
            var allowed = await Pos.IssueAsync(browser, bill with { IdempotencyKey = Guid.NewGuid().ToString("N"), CreditApprovalToken = enough.Token });
            Assert.Equal(100m, allowed.OnAccount);

            // On hold: no new credit, but cash sales and receipts go on.
            var current = await owner.GetJsonAsync<DebtorDto>($"/api/v1/businesses/{Business}/debtors/{debtor.Id}");
            (await owner.PutJsonAsync($"/api/v1/businesses/{Business}/debtors/{debtor.Id}", new UpdateDebtorRequest(current.LegalName, current.TradeName, current.Gstin,
                current.StateCode, current.Address, current.ContactPerson, current.Phone, current.Email, current.WhatsAppNumber, current.SmsNumber, current.WhatsAppConsent,
                current.SmsConsent, current.CreditPeriodDays, current.CreditLimit, current.CustomerGroupId, "ON_HOLD", current.RowVersion))).EnsureSuccessStatusCode();
            Assert.Equal("debtor.on_hold", await (await browser.PostJsonAsync("/api/v1/pos/invoices",
                Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)) with { DebtorId = debtor.Id }, 100m, OnAccount(100m)))).ProblemCodeAsync());
            await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)) with { DebtorId = debtor.Id }, 100m));
            Assert.Equal(-50m, (await ReceiveAtCounterAsync(browser, debtor.Id, 600m, "UPI")).BalanceAfter);
        }
    }

    [Fact]
    public async Task A_debtors_customer_group_price_applies_and_a_return_goes_back_to_the_account()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var code = $"G{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var group = await PostAsync<CustomerGroupDto>(owner, $"/api/v1/businesses/{Business}/catalog/customer-groups", new CreateCustomerGroupRequest(code, $"Hotels {code}"));
        (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/prices/variants/{product.Variants.Single().Id}",
            PricingTests.Price(pack, 90m, "CUSTOMER_GROUP") with { CustomerGroupId = group.Id })).EnsureSuccessStatusCode();
        var debtor = await Ledgers.DebtorAsync(owner, Business, creditLimit: 5000m);
        var current = await owner.GetJsonAsync<DebtorDto>($"/api/v1/businesses/{Business}/debtors/{debtor.Id}");
        (await owner.PutJsonAsync($"/api/v1/businesses/{Business}/debtors/{debtor.Id}", new UpdateDebtorRequest(current.LegalName, current.TradeName, current.Gstin,
            current.StateCode, current.Address, current.ContactPerson, current.Phone, current.Email, current.WhatsAppNumber, current.SmsNumber, current.WhatsAppConsent,
            current.SmsConsent, current.CreditPeriodDays, current.CreditLimit, group.Id, "ACTIVE", current.RowVersion))).EnsureSuccessStatusCode();

        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 4)) with { DebtorId = debtor.Id }, 360m, OnAccount(360m)));
            Assert.Equal((90m, "CUSTOMER_GROUP"), (invoice.Lines[0].UnitPrice, invoice.Lines[0].RateType));

            // One back: taken off the account, against this invoice.
            var line = (await Returns.FindAsync(browser, invoice.Number)).Lines[0].OriginalLineId;
            await Returns.IssueAsync(browser, Returns.Request(invoice.Id, 90m, [new ReturnLineRequest(line, 1)], "ON_ACCOUNT"));
            var open = await Ledgers.OpenItemsAsync(owner, Business, "debtors", debtor.Id);
            Assert.Equal((270m, 270m), (open.Balance, Assert.Single(open.Charges).Remaining));

            // A walk-in invoice cannot be refunded to an account.
            var walkIn = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 100m));
            var walkInLine = (await Returns.FindAsync(browser, walkIn.Number)).Lines[0].OriginalLineId;
            Assert.Equal("refund.no_account", await (await browser.PostJsonAsync("/api/v1/pos/returns",
                Returns.Request(walkIn.Id, 100m, [new ReturnLineRequest(walkInLine, 1)], "ON_ACCOUNT"))).ProblemCodeAsync());
        }

        // The office takes the rest by bank transfer.
        var office = await PostAsync<DebtorReceiptDto>(owner, $"/api/v1/businesses/{Business}/debtor-receipts",
            new DebtorReceiptRequest(debtor.Id, "BANK_TRANSFER", 270m, Store, "NEFT-99", IdempotencyKey: Guid.NewGuid().ToString("N")));
        Assert.Equal((0m, (Guid?)null), (office.BalanceAfter, office.ShiftId));
    }

    [Fact]
    public async Task Two_counters_cannot_together_take_a_debtor_over_the_limit()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        // A different item per bill: otherwise the stock locks alone would queue the bills, hiding the race on the account.
        var packs = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            packs.Add((await Pos.StockedProductAsync(owner, Business, Store, price: 100m)).Pack);
        }

        var debtor = await Ledgers.DebtorAsync(owner, Business, creditLimit: 500m);
        var (first, _) = await CashierCounterAsync();
        var (second, _) = await CashierCounterAsync();
        using (first)
        using (second)
        {
            IssueInvoiceRequest Bill(int i = 0) => Pos.Issue(Pos.Cart(new CartLineRequest(packs[i], 3)) with { DebtorId = debtor.Id }, 300m, OnAccount(300m));

            // Warm up both counters and open enough connections, so the bills below really overlap.
            await Pos.PriceAsync(first, Bill().Cart);
            await Pos.PriceAsync(second, Bill().Cart);
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? first : second).GetAsync("/api/v1/pos/context")));

            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? first : second).PostJsonAsync("/api/v1/pos/invoices", Bill(i))));
            var issued = 0;
            foreach (var response in responses)
            {
                if (response.IsSuccessStatusCode)
                {
                    issued++;
                }
                else
                {
                    Assert.Equal("credit.limit_exceeded", await response.ProblemCodeAsync());
                }
            }

            Assert.Equal(1, issued); // 300 fits in 500; a second 300 would make 600
            Assert.Equal(300m, (await owner.GetJsonAsync<DebtorDto>($"/api/v1/businesses/{Business}/debtors/{debtor.Id}")).Balance);
        }

        var sql10 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "010_credit_sales.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql10, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand(
                         "ALTER TABLE debtor_ledger DISABLE TRIGGER trg_debtor_ledger_no_update_delete; " +
                         "DELETE FROM debtor_ledger WHERE debtor_id = @id; " +
                         "ALTER TABLE debtor_ledger ENABLE TRIGGER trg_debtor_ledger_no_update_delete;", admin, transaction))
        {
            tamper.Parameters.AddWithValue("id", debtor.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql10, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("does not match the debtor ledger", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }
}
