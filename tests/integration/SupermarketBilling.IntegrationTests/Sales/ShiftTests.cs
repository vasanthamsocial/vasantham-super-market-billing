using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;

namespace SupermarketBilling.IntegrationTests.Sales;

[Collection(ApiTestGroup.Name)]
public sealed class ShiftTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    private static DenominationCount[] Count(params (decimal Note, int Count)[] counts) => counts.Select(c => new DenominationCount(c.Note, c.Count)).ToArray();

    private static async Task<ShiftSummaryDto> CloseAsync(TestClient browser, DenominationCount[] counts, string? note = null)
    {
        var response = await browser.PostJsonAsync("/api/v1/pos/shift/close", new CloseShiftRequest(counts, note));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<ShiftSummaryDto>(TestClient.Json))!;
    }

    [Fact]
    public async Task A_shift_reconciles_float_sales_refunds_and_cash_movements_with_a_blind_count()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store, openShift: false);
        using (browser)
        {
            Assert.Equal("shift.not_open", await (await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 100m))).ProblemCodeAsync());

            var opened = await browser.PostJsonAsync("/api/v1/pos/shift/open", new OpenShiftRequest(Count((500, 2), (200, 5))));
            await opened.EnsureSuccessWithBodyAsync();
            Assert.Equal(2000m, (await opened.Content.ReadFromJsonAsync<ShiftSummaryDto>(TestClient.Json))!.OpeningFloat);

            // 300 in cash (500 tendered, 200 change), 150 by UPI, one item of 100 returned in cash.
            var cashBill = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 3)), 300m, new PaymentRequest("CASH", 500m, null)));
            await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)) with { BillDiscountAmount = 0 }, 100m, new PaymentRequest("UPI", 100m, "UTR9")));
            var line = (await Returns.FindAsync(browser, cashBill.Number)).Lines[0].OriginalLineId;
            await Returns.IssueAsync(browser, Returns.Request(cashBill.Id, 100m, [new ReturnLineRequest(line, 1)]));
            foreach (var (kind, amount, reason) in new[] { ("PAY_IN", 500m, "Change from the safe"), ("DROP", 1000m, "To the safe at noon"), ("PAY_OUT", 50m, "Milk for staff tea") })
            {
                (await browser.PostJsonAsync("/api/v1/pos/shift/cash", new CashMovementRequest(kind, amount, reason))).EnsureSuccessStatusCode();
            }

            // Blind: while open, the cashier's view does not show what the drawer should hold.
            var current = await browser.GetJsonAsync<ShiftSummaryDto>("/api/v1/pos/shift");
            Assert.Null(current.ExpectedCash);
            Assert.Equal((2, 1), (current.Invoices, current.Returns));

            // 2000 + 500 - 200 - 100 + 500 - 1000 - 50 = 1650.
            var closed = await CloseAsync(browser, Count((500, 3), (100, 1), (50, 1)));
            Assert.Equal(("CLOSED", 1650m, 1650m, 0m, false), (closed.Status, closed.ExpectedCash!.Value, closed.CountedCash!.Value, closed.Difference!.Value, closed.NeedsReview));
            Assert.Contains(closed.Payments, p => p.Method == "CASH" && p.Amount == 300m);
            Assert.Contains(closed.Payments, p => p.Method == "UPI" && p.Amount == 100m);
            Assert.Contains(closed.Refunds, r => r.Method == "CASH" && r.Amount == 100m);
            Assert.Equal(3, closed.Movements.Count);

            Assert.Equal("shift.not_open", await (await browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 100m))).ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task A_short_drawer_needs_an_explanation_and_a_review_by_another_manager()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        cashier.Client.Dispose();
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store, cashier.Username, cashier.Password, openingFloat: 500);
        ShiftSummaryDto closed;
        using (browser)
        {
            await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)), 200m));
            (await browser.PostJsonAsync("/api/v1/pos/parked", new ParkBillRequest("Waiting", Pos.Cart(new CartLineRequest(pack, 1))))).EnsureSuccessStatusCode();

            // A cashier cannot pay cash out without a supervisor.
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostJsonAsync("/api/v1/pos/shift/cash", new CashMovementRequest("PAY_OUT", 30m, "Courier charge"))).StatusCode);
            var approval = await browser.PostJsonAsync("/api/v1/pos/supervisor-approvals",
                new SupervisorApprovalRequest(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword, null, "PAY_OUT", null, null, 30m, "Courier charge"));
            var token = (await approval.Content.ReadFromJsonAsync<SupervisorApprovalResponse>(TestClient.Json))!.Token;
            (await browser.PostJsonAsync("/api/v1/pos/shift/cash", new CashMovementRequest("PAY_OUT", 30m, "Courier charge", token))).EnsureSuccessStatusCode();

            // Expected 500 + 200 - 30 = 670; counted 650.
            var unexplained = await browser.PostJsonAsync("/api/v1/pos/shift/close", new CloseShiftRequest(Count((500, 1), (100, 1), (50, 1)), null));
            Assert.Equal("shift.note_required", await unexplained.ProblemCodeAsync());
            closed = await CloseAsync(browser, Count((500, 1), (100, 1), (50, 1)), "Gave Rs. 20 extra change");
            Assert.Equal((670m, -20m, true, 1), (closed.ExpectedCash!.Value, closed.Difference!.Value, closed.NeedsReview, closed.ParkedBillsCleared));
            Assert.Empty(await browser.GetJsonAsync<List<ParkedBillDto>>("/api/v1/pos/parked"));
        }

        var pending = await owner.GetJsonAsync<List<ShiftSummaryDto>>($"/api/v1/businesses/{Business}/shifts?storeId={Store}");
        Assert.Contains(pending, s => s.Id == closed.Id && s.NeedsReview);

        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        var reviewed = await approver.PostJsonAsync($"/api/v1/businesses/{Business}/shifts/{closed.Id}/review", new ReviewShiftRequest("Counted again, accepted"));
        await reviewed.EnsureSuccessWithBodyAsync();
        Assert.False((await reviewed.Content.ReadFromJsonAsync<ShiftSummaryDto>(TestClient.Json))!.NeedsReview);
    }

    [Fact]
    public async Task One_open_shift_per_counter_and_per_cashier_and_only_its_cashier_bills_in_it()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 10m);
        var first = await Pos.CounterBrowserAsync(factory, Business, Store);
        var second = await Pos.CounterBrowserAsync(factory, Business, Store, first.Username, first.Password, openShift: false);
        using (first.Browser)
        using (second.Browser)
        {
            Assert.Equal("shift.open_elsewhere", await (await second.Browser.PostJsonAsync("/api/v1/pos/shift/open", new OpenShiftRequest([]))).ProblemCodeAsync());
            Assert.Equal("shift.already_open", await (await first.Browser.PostJsonAsync("/api/v1/pos/shift/open", new OpenShiftRequest([]))).ProblemCodeAsync());

            // Someone else signs in on the first counter while its shift is open: they cannot bill in it.
            var other = await factory.CreateSignedInUserAsync("cashier", Store);
            other.Client.Dispose();
            (await first.Browser.PostJsonAsync("/api/v1/auth/logout", new { })).EnsureSuccessStatusCode();
            await Pos.SignInAsync(first.Browser, other.Username, other.Password);
            Assert.Equal("shift.not_yours", await (await first.Browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 10m))).ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task Bills_racing_the_close_are_either_counted_in_it_or_refused()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 10m, stock: 1000);
        var session = await Pos.CounterBrowserAsync(factory, Business, Store);
        using (session.Browser)
        {
            var browser = session.Browser;
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => browser.GetAsync("/api/v1/pos/context"))); // open connections first
            var bills = Enumerable.Range(0, 10).Select(_ => browser.PostJsonAsync("/api/v1/pos/invoices", Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 10m))).ToList();
            var close = browser.PostJsonAsync("/api/v1/pos/shift/close", new CloseShiftRequest([], "Race test: whatever the drawer holds"));
            await Task.WhenAll(bills.Append(close));

            var closed = (await (await close).Content.ReadFromJsonAsync<ShiftSummaryDto>(TestClient.Json))!;
            var issued = 0;
            foreach (var bill in bills.Select(b => b.Result))
            {
                if (bill.StatusCode == HttpStatusCode.Created)
                {
                    issued++;
                }
                else
                {
                    Assert.Equal("shift.not_open", await bill.ProblemCodeAsync());
                }
            }

            // Every bill that got in is in the expected cash; none landed after the count.
            Assert.Equal(issued, closed.Invoices);
            Assert.Equal(issued * 10m, closed.ExpectedCash);
            var fresh = await owner.GetJsonAsync<ShiftSummaryDto>($"/api/v1/businesses/{Business}/shifts/{closed.Id}");
            Assert.Equal(closed.Invoices, fresh.Invoices);
        }
    }

    [Fact]
    public async Task Database_keeps_closed_shifts_fixed_and_verification_reconciles_them()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 40m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store, openingFloat: 100);
        ShiftSummaryDto closed;
        using (browser)
        {
            await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 1)), 40m));
            closed = await CloseAsync(browser, Count((100, 1), (20, 2)));
        }

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[] { "UPDATE shifts SET counted_cash = 0, difference = -140 WHERE id = @id", "DELETE FROM shifts WHERE id = @id", "DELETE FROM shift_counts WHERE shift_id = @id" })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("id", closed.Id);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
            }
        }

        var sql5 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "005_shifts.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql5, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        // A closed shift whose figures were altered behind the guard is caught.
        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand(
                         "ALTER TABLE shifts DISABLE TRIGGER trg_shifts_guard; UPDATE shifts SET expected_cash = expected_cash + 10, difference = difference - 10, close_note = 'altered' WHERE id = @id; ALTER TABLE shifts ENABLE TRIGGER trg_shifts_guard;",
                         admin, transaction))
        {
            tamper.Parameters.AddWithValue("id", closed.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql5, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("expected cash no longer matches", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }
}
