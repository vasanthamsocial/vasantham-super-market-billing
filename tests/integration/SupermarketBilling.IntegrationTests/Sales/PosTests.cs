using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;

namespace SupermarketBilling.IntegrationTests.Sales;

/// <summary>Helpers shared by the billing tests.</summary>
internal static class Pos
{
    /// <summary>
    /// A counter PC as in real life: the owner signs in on the browser, creates and enrols the counter, signs out, and the
    /// person who bills signs in on the same browser (which keeps the device cookie) and opens their shift.
    /// Without a named user, a new manager is created for the counter: each cashier can have only one open shift.
    /// </summary>
    public static async Task<CounterSession> CounterBrowserAsync(
        ApiFactory factory, Guid businessId, Guid storeId, string? username = null, string? password = null, bool openShift = true, decimal openingFloat = 0)
    {
        if (username is null)
        {
            var manager = await factory.CreateSignedInUserAsync("manager", businessId: businessId);
            manager.Client.Dispose();
            (username, password) = (manager.Username, manager.Password);
        }

        var browser = factory.CreateBrowserClient();
        await SignInAsync(browser, ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var code = $"T{Guid.NewGuid():N}"[..6].ToUpperInvariant();
        var created = await browser.PostJsonAsync($"/api/v1/businesses/{businessId}/counters", new CreateCounterRequest(storeId, code, $"Counter {code}"));
        await created.EnsureSuccessWithBodyAsync();
        var counter = (await created.Content.ReadFromJsonAsync<CounterDto>(TestClient.Json))!;
        (await browser.PostJsonAsync($"/api/v1/businesses/{businessId}/counters/{counter.Id}/devices", new EnrolDeviceRequest($"PC {code}")))
            .EnsureSuccessStatusCode();
        if (username != ApiFactory.OwnerUsername)
        {
            (await browser.PostJsonAsync("/api/v1/auth/logout", new { })).EnsureSuccessStatusCode();
            await SignInAsync(browser, username, password!);
        }

        if (openShift)
        {
            var counts = openingFloat > 0 ? [new DenominationCount(1, (int)openingFloat)] : Array.Empty<DenominationCount>();
            await (await browser.PostJsonAsync("/api/v1/pos/shift/open", new OpenShiftRequest(counts))).EnsureSuccessWithBodyAsync();
        }

        return new CounterSession(browser, counter, username, password!);
    }

    /// <summary>A counter browser, the counter, and who is billing on it.</summary>
    public sealed record CounterSession(TestClient Browser, CounterDto Counter, string Username, string Password)
    {
        public void Deconstruct(out TestClient browser, out CounterDto counter) => (browser, counter) = (Browser, Counter);
    }

    public static async Task SignInAsync(TestClient browser, string username, string password) =>
        await (await browser.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, password))).EnsureSuccessWithBodyAsync();

    /// <summary>A product with an MRP, a standard retail price, and opening stock in the store.</summary>
    public static async Task<(ProductDetailDto Product, Guid Pack)> StockedProductAsync(
        TestClient owner, Guid businessId, Guid storeId, decimal price, decimal? mrp = null, decimal stock = 100, decimal cost = 10,
        string supply = "TAXABLE", decimal gst = 5, bool inclusive = true)
    {
        var product = await CatalogTests.CreateProductAsync(owner, businessId, mrp: mrp, supply: supply, gst: supply == "TAXABLE" ? gst : 0);
        var variant = product.Variants.Single();
        var pack = variant.Units.Single().Id;
        var priced = await owner.PostJsonAsync($"/api/v1/businesses/{businessId}/prices/variants/{variant.Id}", PricingTests.Price(pack, price) with { TaxInclusive = inclusive });
        await priced.EnsureSuccessWithBodyAsync();
        if (stock > 0)
        {
            await StockTests.PostAsync(owner, businessId, StockTests.Doc("OPENING", storeId, StockTests.Line(product, stock, cost: cost)));
        }

        return (product, pack);
    }

    public static CartRequest Cart(params CartLineRequest[] lines) => new("RETAIL", lines);

    public static IssueInvoiceRequest Issue(CartRequest cart, decimal total, params PaymentRequest[] payments) =>
        new(Guid.NewGuid().ToString("N"), cart, payments.Length > 0 ? payments : [new PaymentRequest("CASH", total, null)], total);

    public static async Task<CartDto> PriceAsync(TestClient browser, CartRequest cart)
    {
        var response = await browser.PostJsonAsync("/api/v1/pos/cart", cart);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<CartDto>(TestClient.Json))!;
    }

    public static async Task<InvoiceDto> IssueAsync(TestClient browser, IssueInvoiceRequest request)
    {
        var response = await browser.PostJsonAsync("/api/v1/pos/invoices", request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<InvoiceDto>(TestClient.Json))!;
    }
}

[Collection(ApiTestGroup.Name)]
public sealed class PosTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    [Fact]
    public async Task A_bill_is_priced_by_the_server_takes_the_counters_next_number_and_takes_stock_out_at_cost()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 52m, mrp: 55m, stock: 10, cost: 40m);
        var (browser, counter) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            var context = await browser.GetJsonAsync<PosContextDto>("/api/v1/pos/context");
            Assert.Equal((counter.Code, $"{counter.Code}-000001", "NOT_GST_REGISTERED"), (context.CounterCode, context.NextInvoiceNumber, context.TaxMode));

            var cart = Pos.Cart(new CartLineRequest(pack, 2));
            var preview = await Pos.PriceAsync(browser, cart);
            Assert.Equal(("INVOICE", 104m, 0m), (preview.Kind, preview.GrandTotal, preview.CgstTotal)); // not GST registered: no tax

            var invoice = await Pos.IssueAsync(browser, Pos.Issue(cart, 104m, new PaymentRequest("UPI", 50m, "UTR123"), new PaymentRequest("CASH", 100m, null)));
            Assert.Equal(($"{counter.Code}-000001", 104m, 150m, 46m), (invoice.Number, invoice.GrandTotal, invoice.PaidTotal, invoice.ChangeDue));
            var line = Assert.Single(invoice.Lines);
            Assert.Equal(("STANDARD", 52m, 55m), (line.RateType, line.UnitPrice, line.Mrp!.Value));
            Assert.NotNull(line.PriceRuleId);
            Assert.Null(invoice.SellerGstin);

            Assert.Equal(8m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);
            var ledger = await owner.GetJsonAsync<List<LedgerEntryDto>>($"/api/v1/businesses/{Business}/stock/ledger?storeId={Store}&variantId={product.Variants.Single().Id}");
            Assert.Equal(("SALE", -2m, -80m, "SALES_INVOICE"), (ledger[0].MovementType, ledger[0].Quantity, ledger[0].Value, ledger[0].DocumentType));

            var again = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 52m));
            Assert.Equal($"{counter.Code}-000002", again.Number);
            var listed = await owner.GetJsonAsync<List<InvoiceSummaryDto>>($"/api/v1/businesses/{Business}/sales/invoices?storeId={Store}&search={invoice.Number}");
            Assert.Equal(invoice.Id, Assert.Single(listed).Id);
        }
    }

    [Fact]
    public async Task Billing_needs_an_enrolled_device_and_a_cashier_allowed_in_that_store()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 20m);
        var cart = Pos.Cart(new CartLineRequest(pack, 1));

        // Signed in, but this browser is not a counter.
        var notEnrolled = await owner.PostJsonAsync("/api/v1/pos/cart", cart);
        Assert.Equal(HttpStatusCode.Forbidden, notEnrolled.StatusCode);

        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        cashier.Client.Dispose();
        var (browser, counter) = await Pos.CounterBrowserAsync(factory, Business, Store, cashier.Username, cashier.Password);
        using (browser)
        {
            await Pos.IssueAsync(browser, Pos.Issue(cart, 20m));

            // The device cookie alone (signed out) cannot bill.
            (await browser.PostJsonAsync("/api/v1/auth/logout", new { })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostJsonAsync("/api/v1/pos/cart", cart)).StatusCode);

            // A revoked device cannot bill either.
            await Pos.SignInAsync(browser, cashier.Username, cashier.Password);
            var devices = await owner.GetJsonAsync<List<CounterDeviceDto>>($"/api/v1/businesses/{Business}/counters/{counter.Id}/devices");
            (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/counters/{counter.Id}/devices/{devices.Single().Id}/revoke", new { })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync("/api/v1/pos/cart", cart)).StatusCode);
        }

        // A user without billing rights cannot bill even on an enrolled counter.
        var keeper = await factory.CreateSignedInUserAsync("inventory_operator", Store);
        keeper.Client.Dispose();
        var (keeperBrowser, _) = await Pos.CounterBrowserAsync(factory, Business, Store, keeper.Username, keeper.Password, openShift: false);
        using (keeperBrowser)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await keeperBrowser.PostJsonAsync("/api/v1/pos/cart", cart)).StatusCode);
        }
    }

    [Fact]
    public async Task Retrying_a_bill_returns_the_same_invoice_and_a_changed_total_is_refused()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 30m, stock: 20);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            var request = Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 30m);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => browser.PostJsonAsync("/api/v1/pos/invoices", request)));
            var ids = new HashSet<Guid>();
            foreach (var attempt in attempts)
            {
                await attempt.EnsureSuccessWithBodyAsync();
                ids.Add((await attempt.Content.ReadFromJsonAsync<InvoiceDto>(TestClient.Json))!.Id);
            }

            Assert.Single(ids);
            Assert.Equal(19m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);

            var changed = await browser.PostJsonAsync("/api/v1/pos/invoices", request with { Payments = [new PaymentRequest("CASH", 50m, null)] });
            Assert.Equal("idempotency.mismatch", await changed.ProblemCodeAsync());

            var wrongTotal = await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)), 59m));
            Assert.Equal(HttpStatusCode.Conflict, wrongTotal.StatusCode);
            Assert.Equal("invoice.total_changed", await wrongTotal.ProblemCodeAsync());

            var shortPaid = await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)), 60m, new PaymentRequest("CASH", 50m, null)));
            Assert.Equal("payment.short", await shortPaid.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task Counters_bill_in_parallel_without_overselling_and_each_keeps_a_gapless_series()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 10m, stock: 30);
        var (first, c1) = await Pos.CounterBrowserAsync(factory, Business, Store);
        var (second, c2) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (first)
        using (second)
        {
            var cart = Pos.Cart(new CartLineRequest(pack, 1));
            var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
                (i % 2 == 0 ? first : second).PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(cart, 10m))));

            Assert.Equal(30, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            {
                Assert.Equal("stock.insufficient", await refused.ProblemCodeAsync());
            }

            Assert.Equal(0m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);
            var invoices = await owner.GetJsonAsync<List<InvoiceSummaryDto>>($"/api/v1/businesses/{Business}/sales/invoices?storeId={Store}");
            foreach (var code in new[] { c1.Code, c2.Code })
            {
                var numbers = invoices.Where(i => i.CounterCode == code).Select(i => int.Parse(i.Number[(code.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
                Assert.Equal(Enumerable.Range(1, numbers.Count), numbers); // refused bills gave their numbers back
            }
        }
    }

    [Fact]
    public async Task A_cashier_needs_a_supervisor_for_a_price_override_and_the_approval_works_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 52m, mrp: 55m);
        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        cashier.Client.Dispose();
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store, cashier.Username, cashier.Password);
        using (browser)
        {
            var cart = Pos.Cart(new CartLineRequest(pack, 1, OverridePrice: 45m));
            Assert.True((await Pos.PriceAsync(browser, cart)).Lines[0].NeedsPriceApproval);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(cart, 45m))).StatusCode);

            SupervisorApprovalRequest Ask(string user, string password, decimal price = 45m) =>
                new(user, password, null, "PRICE_OVERRIDE", pack, price, null, "Matched a competitor");
            Assert.Equal("approval.invalid_credentials",
                await (await browser.PostJsonAsync("/api/v1/pos/supervisor-approvals", Ask(ApiFactory.ApproverUsername, "wrong-password"))).ProblemCodeAsync());
            Assert.Equal(HttpStatusCode.Forbidden, // a cashier cannot approve
                (await browser.PostJsonAsync("/api/v1/pos/supervisor-approvals", Ask(cashier.Username, cashier.Password))).StatusCode);

            var approved = await browser.PostJsonAsync("/api/v1/pos/supervisor-approvals", Ask(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword));
            await approved.EnsureSuccessWithBodyAsync();
            var token = (await approved.Content.ReadFromJsonAsync<SupervisorApprovalResponse>(TestClient.Json))!.Token;

            // The token is for 45.00 only.
            var otherPrice = await browser.PostJsonAsync("/api/v1/pos/invoices",
                Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1, OverridePrice: 44m, OverrideApprovalToken: token)), 44m));
            Assert.Equal(HttpStatusCode.Forbidden, otherPrice.StatusCode);

            var withToken = Pos.Cart(new CartLineRequest(pack, 1, OverridePrice: 45m, OverrideApprovalToken: token));
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(withToken, 45m));
            Assert.Equal(("OVERRIDE", 45m, (Guid?)null), (invoice.Lines[0].RateType, invoice.Lines[0].UnitPrice, invoice.Lines[0].PriceRuleId));

            var reused = await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(withToken, 45m));
            Assert.Equal("approval.used", await reused.ProblemCodeAsync());

            // Never above the MRP, approval or not.
            var aboveMrp = await browser.PostJsonAsync("/api/v1/pos/supervisor-approvals", Ask(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword, 60m));
            var aboveToken = (await aboveMrp.Content.ReadFromJsonAsync<SupervisorApprovalResponse>(TestClient.Json))!.Token;
            var refused = await browser.PostJsonAsync("/api/v1/pos/invoices",
                Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1, OverridePrice: 60m, OverrideApprovalToken: aboveToken)), 60m));
            Assert.Equal("price.above_mrp", await refused.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task Discounts_from_a_cashier_need_a_supervisor_and_stay_within_what_was_approved()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 50m);
        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        cashier.Client.Dispose();
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store, cashier.Username, cashier.Password);
        using (browser)
        {
            var cart = Pos.Cart(new CartLineRequest(pack, 2)) with { BillDiscountAmount = 5m };
            var preview = await Pos.PriceAsync(browser, cart);
            Assert.True(preview.NeedsDiscountApproval);
            Assert.Equal(95m, preview.GrandTotal);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(cart, 95m))).StatusCode);

            var approved = await browser.PostJsonAsync("/api/v1/pos/supervisor-approvals",
                new SupervisorApprovalRequest(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword, null, "DISCOUNT", null, null, 5m, "Regular customer"));
            var token = (await approved.Content.ReadFromJsonAsync<SupervisorApprovalResponse>(TestClient.Json))!.Token;

            var tooMuch = await browser.PostJsonAsync("/api/v1/pos/invoices",
                Pos.Issue(cart with { BillDiscountAmount = 6m }, 94m) with { DiscountApprovalToken = token });
            Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);

            var invoice = await Pos.IssueAsync(browser, Pos.Issue(cart, 95m) with { DiscountApprovalToken = token });
            Assert.Equal((5m, 95m), (invoice.DiscountTotal, invoice.GrandTotal));
        }
    }

    [Fact]
    public async Task A_pack_with_several_mrps_must_say_which_one_is_sold()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 48m, mrp: 50m);
        (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/catalog/variants/{product.Variants.Single().Id}/mrps", new AddMrpRequest(pack, 55m, null)))
            .EnsureSuccessStatusCode();
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            var unclear = await browser.PostJsonAsync("/api/v1/pos/cart", Pos.Cart(new CartLineRequest(pack, 1)));
            Assert.Equal("mrp.choose", await unclear.ProblemCodeAsync());
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1, Mrp: 55m)), 48m));
            Assert.Equal(55m, invoice.Lines[0].Mrp);
        }
    }

    [Fact]
    public async Task A_refused_bill_changes_nothing()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 10m, stock: 2);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            var before = await browser.GetJsonAsync<PosContextDto>("/api/v1/pos/context");
            var refused = await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(Pos.Cart(new CartLineRequest(pack, 3)), 30m));
            Assert.Equal("stock.insufficient", await refused.ProblemCodeAsync());
            Assert.Equal(before.NextInvoiceNumber, (await browser.GetJsonAsync<PosContextDto>("/api/v1/pos/context")).NextInvoiceNumber);
            Assert.Equal(2m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);
        }
    }

    [Fact]
    public async Task Database_keeps_invoices_approvals_and_counter_codes_tamper_proof()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 10m);
        var (browser, counter) = await Pos.CounterBrowserAsync(factory, Business, Store);
        InvoiceDto invoice;
        using (browser)
        {
            invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 10m));
        }

        await using var db = await factory.OpenAppConnectionAsync();
        foreach (var (sql, id) in new[]
        {
            ("UPDATE sales_invoices SET grand_total = 1 WHERE id = @id", invoice.Id),
            ("DELETE FROM sales_invoices WHERE id = @id", invoice.Id),
            ("UPDATE sales_invoice_lines SET unit_price = 1 WHERE invoice_id = @id", invoice.Id),
            ("DELETE FROM sales_invoice_payments WHERE invoice_id = @id", invoice.Id),
            ("UPDATE counters SET code = 'ZZ' WHERE id = @id", counter.Id),
            ("DELETE FROM counters WHERE id = @id", counter.Id),
        })
        {
            await using var command = new NpgsqlCommand(sql, db);
            command.Parameters.AddWithValue("id", id);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task Database_verification_reconciles_invoices_numbers_payments_and_stock()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 12.5m);
        var (browser, counter) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        {
            await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 3)) with { BillDiscountAmount = 0.5m }, 37m));
        }

        var sql = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "003_sales.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        // A number taken without an invoice is a gap, and verification finds it.
        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand("UPDATE document_sequences SET next_number = next_number + 1 WHERE series = @s", admin, transaction))
        {
            tamper.Parameters.AddWithValue("s", "INV-" + counter.Code);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql, admin, transaction);
        var error = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("invoice numbering has gaps", error.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_parked_bill_survives_until_retrieved_once_on_the_same_counter()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 15m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        var (otherCounter, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        using (otherCounter)
        {
            var cart = Pos.Cart(new CartLineRequest(pack, 3)) with { BillDiscountAmount = 1m };
            var parked = await browser.PostJsonAsync("/api/v1/pos/parked", new ParkBillRequest("Lady in blue saree", cart));
            await parked.EnsureSuccessWithBodyAsync();
            var bill = (await parked.Content.ReadFromJsonAsync<ParkedBillDto>(TestClient.Json))!;
            Assert.Equal(("Lady in blue saree", 1), (bill.Label, bill.Items));

            Assert.Empty(await otherCounter.GetJsonAsync<List<ParkedBillDto>>("/api/v1/pos/parked")); // parked bills belong to their counter
            Assert.Equal(HttpStatusCode.NotFound, (await otherCounter.PostJsonAsync($"/api/v1/pos/parked/{bill.Id}/retrieve", new { })).StatusCode);

            var retrieved = await browser.PostJsonAsync($"/api/v1/pos/parked/{bill.Id}/retrieve", new { });
            await retrieved.EnsureSuccessWithBodyAsync();
            var back = (await retrieved.Content.ReadFromJsonAsync<CartRequest>(TestClient.Json))!;
            Assert.Equal((pack, 3m, 1m), (back.Lines[0].VariantUnitId, back.Lines[0].Quantity, back.BillDiscountAmount!.Value));
            Assert.Equal(HttpStatusCode.NotFound, (await browser.PostJsonAsync($"/api/v1/pos/parked/{bill.Id}/retrieve", new { })).StatusCode);
            Assert.Empty(await browser.GetJsonAsync<List<ParkedBillDto>>("/api/v1/pos/parked"));
        }
    }

    [Fact]
    public async Task An_invoice_downloads_as_a_pdf_from_its_counter_and_for_sales_staff()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 52m, mrp: 55m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        var (otherCounter, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (browser)
        using (otherCounter)
        {
            var invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)), 104m));

            foreach (var (client, path) in new[] { (browser, $"/api/v1/pos/invoices/{invoice.Id}/pdf"), (owner, $"/api/v1/businesses/{Business}/sales/invoices/{invoice.Id}/pdf") })
            {
                var response = await client.GetAsync(path);
                await response.EnsureSuccessWithBodyAsync();
                Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal($"{invoice.Number}.pdf", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.Equal("%PDF-1.4", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
            }

            // Another counter cannot fetch this counter's invoices through the counter route.
            Assert.Equal(HttpStatusCode.NotFound, (await otherCounter.GetAsync($"/api/v1/pos/invoices/{invoice.Id}/pdf")).StatusCode);
        }
    }
}
