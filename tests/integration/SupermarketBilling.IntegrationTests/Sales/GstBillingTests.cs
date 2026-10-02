using System.Net.Http.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Sales;

/// <summary>Own installation: GST-registered businesses (regular and composition) in Tamil Nadu (state 33).</summary>
public sealed class GstBillingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    internal static async Task<(Guid Business, Guid Store)> BusinessAsync(TestClient owner, string code, string mode, string? gstin)
    {
        var created = await owner.PostJsonAsync("/api/v1/businesses",
            new CreateBusinessRequest(code, $"{code} Traders Private Limited", $"{code} Mart", "33", gstin, "2 Bazaar Street, Chennai", mode));
        await created.EnsureSuccessWithBodyAsync();
        var business = (await created.Content.ReadFromJsonAsync<BusinessDto>(TestClient.Json))!;
        var store = await owner.PostJsonAsync($"/api/v1/businesses/{business.Id}/stores", new CreateStoreRequest("MAIN", "Main store", "33", null, "2 Bazaar Street, Chennai"));
        await store.EnsureSuccessWithBodyAsync();
        return (business.Id, (await store.Content.ReadFromJsonAsync<StoreDto>(TestClient.Json))!.Id);
    }

    [Fact]
    public async Task Regular_gst_bills_split_cgst_and_sgst_in_state_charge_igst_across_states_and_issue_a_bill_of_supply_for_exempt_goods()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var sellerGstin = Gstin.Complete("33AAACG1234A1Z");
        var (business, store) = await BusinessAsync(owner, "GSTREG", "GST_REGULAR", sellerGstin);
        var (_, rice) = await Pos.StockedProductAsync(owner, business, store, price: 52.50m, gst: 5);
        var (_, soap) = await Pos.StockedProductAsync(owner, business, store, price: 100m, gst: 18, inclusive: false);
        var (_, vegetables) = await Pos.StockedProductAsync(owner, business, store, price: 40m, supply: "EXEMPT");
        var (browser, _) = await Pos.CounterBrowserAsync(factory, business, store);
        using (browser)
        {
            // In-state retail: the tax is inside the price, half CGST and half SGST.
            var local = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(rice, 2)), 105m));
            Assert.Equal(("TAX_INVOICE", sellerGstin, false), (local.Kind, local.SellerGstin, local.IsInterState));
            Assert.Equal((100m, 2.50m, 2.50m, 0m, 105m), (local.TaxableTotal, local.CgstTotal, local.SgstTotal, local.IgstTotal, local.GrandTotal));

            // A registered buyer in Karnataka (29): place of supply 29, so IGST; the tax-exclusive price has tax added.
            var buyerGstin = Gstin.Complete("29AABCB1234C1Z");
            var cart = Pos.Cart(new CartLineRequest(soap, 1)) with { Buyer = new BuyerRequest("Bangalore Traders", buyerGstin, null, "MG Road, Bengaluru", null) };
            var preview = await Pos.PriceAsync(browser, cart);
            Assert.Equal(("29", true, 118m), (preview.PlaceOfSupplyStateCode, preview.IsInterState, preview.GrandTotal));
            var interState = await Pos.IssueAsync(browser, Pos.Issue(cart, 118m));
            Assert.Equal((100m, 0m, 0m, 18m), (interState.TaxableTotal, interState.CgstTotal, interState.SgstTotal, interState.IgstTotal));
            Assert.Equal((buyerGstin, "Bangalore Traders"), (interState.BuyerGstin, interState.BuyerName));

            // Only exempt goods: a bill of supply, no tax.
            var exempt = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(vegetables, 1)), 40m));
            Assert.Equal(("BILL_OF_SUPPLY", 0m), (exempt.Kind, exempt.CgstTotal + exempt.SgstTotal + exempt.IgstTotal));

            var badBuyer = await browser.PostJsonAsync("/api/v1/pos/cart", cart with { Buyer = new BuyerRequest("X", "29AABCB1234C1ZZ", null, null, null) });
            Assert.Equal("buyer.gstin_invalid", await badBuyer.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task A_composition_dealer_issues_a_bill_of_supply_with_the_declaration_and_cannot_sell_out_of_state()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (business, store) = await BusinessAsync(owner, "GSTCMP", "GST_COMPOSITION", Gstin.Complete("33AAACC5678B1Z"));
        var (_, item) = await Pos.StockedProductAsync(owner, business, store, price: 105m, gst: 5);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, business, store);
        using (browser)
        {
            var bill = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(item, 1)), 105m));
            Assert.Equal(("BILL_OF_SUPPLY", "GST_COMPOSITION", 105m, 105m), (bill.Kind, bill.TaxMode, bill.TaxableTotal, bill.GrandTotal));
            Assert.Equal(0m, bill.CgstTotal + bill.SgstTotal + bill.IgstTotal + bill.CessTotal);
            Assert.Contains("not eligible to collect tax", bill.Declaration, StringComparison.Ordinal);

            var outOfState = await browser.PostJsonAsync("/api/v1/pos/cart",
                Pos.Cart(new CartLineRequest(item, 1)) with { Buyer = new BuyerRequest("Visitor", null, null, null, "29") });
            Assert.Equal("composition.inter_state", await outOfState.ProblemCodeAsync());
        }
    }
}

/// <summary>Own installation: a business that registers for GST while it is trading.</summary>
public sealed class InvoiceSeriesTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_change_of_registration_starts_a_new_invoice_series_and_old_invoices_keep_their_numbers()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (business, store) = await GstBillingTests.BusinessAsync(owner, "GSTCHG", "NOT_GST_REGISTERED", null);
        var (_, item) = await Pos.StockedProductAsync(owner, business, store, price: 105m, gst: 5);
        var session = await Pos.CounterBrowserAsync(factory, business, store);
        var (browser, counter) = session;
        using (browser)
        {
            var before = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(item, 1)), 105m));
            Assert.Equal(($"{counter.Code}-000001", "INVOICE"), (before.Number, before.Kind));

            // The accountant prepares the GST registration; the owner approves it from the day after tomorrow.
            var accountant = await factory.CreateSignedInUserAsync("accountant", businessId: business);
            var effective = DateOnly.FromDateTime(factory.Clock.GetUtcNow().AddDays(2).UtcDateTime);
            Guid requestId;
            using (accountant.Client)
            {
                var asked = await accountant.Client.PostJsonAsync($"/api/v1/businesses/{business}/tax-registrations/change-requests",
                    new TaxRegistrationChangeRequest("GST_REGULAR", Gstin.Complete("33AAACR4321D1Z"), effective, "Turnover crossed the threshold", "REG-06 ARN 123", true));
                await asked.EnsureSuccessWithBodyAsync();
                requestId = (await asked.Content.ReadFromJsonAsync<TaxRegistrationChangeResponse>(TestClient.Json))!.ApprovalRequestId;
            }

            (await owner.PostJsonAsync($"/api/v1/approvals/{requestId}/approve", new ApprovalDecisionRequest("Registration certificate seen"))).EnsureSuccessStatusCode();
            factory.Clock.Advance(TimeSpan.FromDays(2));

            await Pos.SignInAsync(browser, session.Username, session.Password); // the old session has expired
            var after = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(item, 1)), 105m));
            Assert.Equal(($"{counter.Code}B-000001", "TAX_INVOICE", "GST_REGULAR"), (after.Number, after.Kind, after.TaxMode));
            Assert.Equal((2.50m, 2.50m), (after.CgstTotal, after.SgstTotal));

            using var ownerAgain = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
            var old = await ownerAgain.GetJsonAsync<InvoiceDto>($"/api/v1/businesses/{business}/sales/invoices/{before.Id}");
            Assert.Equal((before.Number, "INVOICE", (string?)null), (old.Number, old.Kind, old.SellerGstin)); // history unchanged
        }
    }
}
