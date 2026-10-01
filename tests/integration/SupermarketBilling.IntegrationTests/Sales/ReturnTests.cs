using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;

namespace SupermarketBilling.IntegrationTests.Sales;

internal static class Returns
{
    public static async Task<ReturnableInvoiceDto> FindAsync(TestClient browser, string number) =>
        await browser.GetJsonAsync<ReturnableInvoiceDto>($"/api/v1/pos/returns/invoice?number={Uri.EscapeDataString(number)}");

    public static IssueReturnRequest Request(Guid invoiceId, decimal total, IReadOnlyList<ReturnLineRequest> lines, string method = "CASH") =>
        new(Guid.NewGuid().ToString("N"), invoiceId, "Customer changed mind", lines, [new PaymentRequest(method, total, null)], total);

    public static async Task<CreditNoteDto> IssueAsync(TestClient browser, IssueReturnRequest request)
    {
        var response = await browser.PostJsonAsync("/api/v1/pos/returns", request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<CreditNoteDto>(TestClient.Json))!;
    }
}

[Collection(ApiTestGroup.Name)]
public sealed class ReturnTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    [Fact]
    public async Task Partial_returns_refund_their_share_put_stock_back_and_add_up_to_the_bill()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 52.50m, stock: 10, cost: 40m);
        var (browser, counter) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 3)), 158m)); // 157.50 rounds to 158
            var found = await Returns.FindAsync(browser, invoice.Number);
            var line = Assert.Single(found.Lines);
            Assert.Equal((3m, 3m), (line.Sold, line.Returnable));

            var first = await Returns.IssueAsync(browser, Returns.Request(invoice.Id, 53m, [new ReturnLineRequest(line.OriginalLineId, 1)]));
            Assert.Equal(($"{counter.Code}/CN000001", 52.50m, 0.50m, 53m), (first.Number, first.Lines[0].Total, first.RoundOff, first.GrandTotal));
            Assert.Equal((invoice.Number, invoice.BusinessDate), (first.OriginalInvoiceNumber, first.OriginalInvoiceDate));
            Assert.Equal(8m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);

            Assert.Equal(2m, (await Returns.FindAsync(browser, invoice.Number)).Lines[0].Returnable);
            var tooMany = await browser.PostJsonAsync("/api/v1/pos/returns", Returns.Request(invoice.Id, 158m, [new ReturnLineRequest(line.OriginalLineId, 3)]));
            Assert.Equal("return.quantity_too_large", await tooMany.ProblemCodeAsync());

            var rest = await Returns.IssueAsync(browser, Returns.Request(invoice.Id, 105m, [new ReturnLineRequest(line.OriginalLineId, 2)]));
            Assert.Equal($"{counter.Code}/CN000002", rest.Number);
            Assert.Equal(invoice.GrandTotal, first.GrandTotal + rest.GrandTotal); // everything back, to the rupee
            Assert.Equal(10m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);

            var ledger = await owner.GetJsonAsync<List<LedgerEntryDto>>($"/api/v1/businesses/{Business}/stock/ledger?storeId={Store}&variantId={product.Variants.Single().Id}");
            Assert.Equal(("SALE_RETURN", 2m, 80m), (ledger[0].MovementType, ledger[0].Quantity, ledger[0].Value)); // back at the cost it left at

            var pdf = await owner.GetAsync($"/api/v1/businesses/{Business}/sales/returns/{rest.Id}/pdf");
            await pdf.EnsureSuccessWithBodyAsync();
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task A_cashier_needs_a_supervisor_for_a_return_up_to_the_approved_amount()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 40m);
        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        cashier.Client.Dispose();
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store, cashier.Username, cashier.Password);
        using (browser)
        {
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 3)), 120m));
            var line = (await Returns.FindAsync(browser, invoice.Number)).Lines[0].OriginalLineId;
            var preview = await browser.PostJsonAsync("/api/v1/pos/returns/preview", new ReturnPreviewRequest(invoice.Id, [new ReturnLineRequest(line, 2)]));
            Assert.True((await preview.Content.ReadFromJsonAsync<ReturnPreviewDto>(TestClient.Json))!.NeedsApproval);

            var request = Returns.Request(invoice.Id, 80m, [new ReturnLineRequest(line, 2)]);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync("/api/v1/pos/returns", request)).StatusCode);

            var approval = await browser.PostJsonAsync("/api/v1/pos/supervisor-approvals",
                new SupervisorApprovalRequest(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword, null, "RETURN", null, null, 50m, "Wrong size"));
            var token = (await approval.Content.ReadFromJsonAsync<SupervisorApprovalResponse>(TestClient.Json))!.Token;
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync("/api/v1/pos/returns", request with { ApprovalToken = token })).StatusCode); // 80 > 50

            var one = await Returns.IssueAsync(browser, Returns.Request(invoice.Id, 40m, [new ReturnLineRequest(line, 1)]) with { ApprovalToken = token });
            Assert.Equal(40m, one.GrandTotal);
        }
    }

    [Fact]
    public async Task Damaged_goods_are_refunded_but_not_restocked()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 25m, stock: 5);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)), 50m));
            var line = (await Returns.FindAsync(browser, invoice.Number)).Lines[0].OriginalLineId;
            var note = await Returns.IssueAsync(browser, Returns.Request(invoice.Id, 25m, [new ReturnLineRequest(line, 1, Restock: false)]));
            Assert.False(note.Lines[0].Restocked);
            Assert.Equal(3m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);
        }
    }

    [Fact]
    public async Task Store_credit_pays_for_an_exchange_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, shirt) = await Pos.StockedProductAsync(owner, Business, Store, price: 300m);
        var (_, other) = await Pos.StockedProductAsync(owner, Business, Store, price: 350m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            var bought = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(shirt, 1)), 300m));
            var line = (await Returns.FindAsync(browser, bought.Number)).Lines[0].OriginalLineId;
            var credit = await Returns.IssueAsync(browser, Returns.Request(bought.Id, 300m, [new ReturnLineRequest(line, 1)], method: "STORE_CREDIT"));
            Assert.Equal((300m, 300m), (credit.StoreCredit, credit.StoreCreditLeft));

            // The exchange: 350, of which 300 from the credit note and 50 in cash.
            var exchange = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(other, 1)), 350m,
                new PaymentRequest("CREDIT_NOTE", 300m, credit.Number), new PaymentRequest("CASH", 50m, null)));
            Assert.Contains(exchange.Payments, p => p.Method == "CREDIT_NOTE" && p.Reference == credit.Number);
            Assert.Equal(0m, (await browser.GetJsonAsync<CreditNoteDto>($"/api/v1/pos/credit-notes?number={Uri.EscapeDataString(credit.Number)}")).StoreCreditLeft);

            var again = await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(Pos.Cart(new CartLineRequest(other, 1)), 350m,
                new PaymentRequest("CREDIT_NOTE", 300m, credit.Number), new PaymentRequest("CASH", 50m, null)));
            Assert.Equal("credit_note.insufficient", await again.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task Two_counters_cannot_spend_the_same_store_credit_at_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var (_, otherPack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m); // different items: no shared stock lock
        var (first, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        var (second, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (first)
        using (second)
        {
            var bought = await Pos.IssueAsync(first, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 100m));
            var line = (await Returns.FindAsync(first, bought.Number)).Lines[0].OriginalLineId;
            var credit = await Returns.IssueAsync(first, Returns.Request(bought.Id, 100m, [new ReturnLineRequest(line, 1)], method: "STORE_CREDIT"));
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? first : second).GetAsync("/api/v1/pos/context"))); // open connections

            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? first : second).PostJsonAsync("/api/v1/pos/invoices",
                Pos.Issue(Pos.Cart(new CartLineRequest(i % 2 == 0 ? pack : otherPack, 1)), 100m, new PaymentRequest("CREDIT_NOTE", 100m, credit.Number)))));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            {
                Assert.Equal("credit_note.insufficient", await refused.ProblemCodeAsync());
            }
        }
    }

    [Fact]
    public async Task Two_counters_cannot_both_refund_the_last_item_and_a_retry_returns_the_same_credit_note()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 10m);
        var (first, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        var (second, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (first)
        using (second)
        {
            var invoice = await Pos.IssueAsync(first, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 10m));
            var line = (await Returns.FindAsync(first, invoice.Number)).Lines[0].OriginalLineId;

            // Warm up the return path (query compilation), so the requests below really overlap.
            (await first.PostJsonAsync("/api/v1/pos/returns/preview", new ReturnPreviewRequest(invoice.Id, [new ReturnLineRequest(line, 1)]))).EnsureSuccessStatusCode();
            (await second.PostJsonAsync("/api/v1/pos/returns/preview", new ReturnPreviewRequest(invoice.Id, [new ReturnLineRequest(line, 1)]))).EnsureSuccessStatusCode();

            // Open enough database connections first: otherwise the burst below waits for new connections and the
            // first request finishes before the others start, so the race would not really happen.
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? first : second).GetAsync("/api/v1/pos/context")));

            // A double-clicked retry on one counter (same key) races other attempts to return the same last item.
            var request = Returns.Request(invoice.Id, 10m, [new ReturnLineRequest(line, 1)]);
            var other = Returns.Request(invoice.Id, 10m, [new ReturnLineRequest(line, 1)]);
            var competing = Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? first : second)
                .PostJsonAsync("/api/v1/pos/returns", Returns.Request(invoice.Id, 10m, [new ReturnLineRequest(line, 1)])));
            var responses = await Task.WhenAll(competing.Concat([
                first.PostJsonAsync("/api/v1/pos/returns", request), first.PostJsonAsync("/api/v1/pos/returns", request),
                second.PostJsonAsync("/api/v1/pos/returns", other)]));
            var issued = new HashSet<Guid>();
            foreach (var r in responses)
            {
                if (r.IsSuccessStatusCode)
                {
                    issued.Add((await r.Content.ReadFromJsonAsync<CreditNoteDto>(TestClient.Json))!.Id);
                }
                else
                {
                    Assert.Equal("return.quantity_too_large", await r.ProblemCodeAsync());
                }
            }

            Assert.Single(issued); // the item was refunded once, whoever won
            Assert.Equal(0m, (await Returns.FindAsync(first, invoice.Number)).Lines[0].Returnable);
        }
    }

    [Fact]
    public async Task Database_keeps_credit_notes_tamper_proof_and_verification_reconciles_them()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 20m);
        var (browser, counter) = await Pos.CounterBrowserAsync(factory, Business, Store);
        CreditNoteDto note;
        using (browser)
        {
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)), 40m));
            var line = (await Returns.FindAsync(browser, invoice.Number)).Lines[0].OriginalLineId;
            note = await Returns.IssueAsync(browser, Returns.Request(invoice.Id, 20m, [new ReturnLineRequest(line, 1)], method: "STORE_CREDIT"));
        }

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[]
            {
                "UPDATE sales_returns SET grand_total = 1 WHERE id = @id",
                "DELETE FROM sales_return_lines WHERE return_id = @id",
                "UPDATE sales_return_refunds SET amount = 1 WHERE return_id = @id",
            })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("id", note.Id);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
            }
        }

        var sql4 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "004_returns.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql4, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand("UPDATE document_sequences SET next_number = next_number + 1 WHERE series = @s", admin, transaction))
        {
            tamper.Parameters.AddWithValue("s", "CN-" + counter.Code);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql4, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("credit note numbering has gaps", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }
}

/// <summary>Own installation: a GST-registered business, so returns reverse CGST and SGST in proportion.</summary>
public sealed class GstReturnTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_credit_note_reverses_the_gst_of_what_is_returned()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (business, store) = await GstBillingTests.BusinessAsync(owner, "GSTRET", "GST_REGULAR", Gstin.Complete("33AAACR9876E1Z"));
        var (_, item) = await Pos.StockedProductAsync(owner, business, store, price: 105m, gst: 5);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, business, store);
        using (browser)
        {
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(item, 4)), 420m));
            Assert.Equal((400m, 10m, 10m), (invoice.TaxableTotal, invoice.CgstTotal, invoice.SgstTotal));
            var line = (await Returns.FindAsync(browser, invoice.Number)).Lines[0].OriginalLineId;
            var note = await Returns.IssueAsync(browser, Returns.Request(invoice.Id, 105m, [new ReturnLineRequest(line, 1)]));
            Assert.Equal((100m, 2.50m, 2.50m, 0m, 105m), (note.TaxableTotal, note.CgstTotal, note.SgstTotal, note.IgstTotal, note.GrandTotal));
            Assert.Equal(invoice.SellerGstin, note.SellerGstin);
        }
    }
}
