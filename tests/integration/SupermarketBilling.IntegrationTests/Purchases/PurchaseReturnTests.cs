using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Accounts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;

namespace SupermarketBilling.IntegrationTests.Purchases;

internal static class Returns
{
    public static PurchaseReturnRequest Request(Guid grnId, string reason, params PurchaseReturnLineRequest[] lines) =>
        new(grnId, reason, lines, Guid.NewGuid().ToString("N"));

    public static async Task<PurchaseReturnDto> SaveAsync(TestClient client, Guid businessId, PurchaseReturnRequest request)
    {
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/purchase-returns", request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<PurchaseReturnDto>(TestClient.Json))!;
    }

    public static Task<ReturnableGrnDto> ReturnableAsync(TestClient client, Guid businessId, Guid grnId) =>
        client.GetJsonAsync<ReturnableGrnDto>($"/api/v1/businesses/{businessId}/grns/{grnId}/returnable");
}

[Collection(ApiTestGroup.Name)]
public sealed class PurchaseReturnTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    [Fact]
    public async Task Goods_go_back_against_a_receipt_reducing_stock_and_what_is_owed_until_nothing_is_left()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, Business);
        var pack = product.Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);

        // 10 paid + 2 free for Rs. 600: each piece is worth Rs. 50.
        var grn = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 60m, FreeQuantity: 2)));
        var line = grn.Lines.Single();
        var returnable = await Returns.ReturnableAsync(owner, Business, grn.Id);
        Assert.Equal((12m, 0m, 12m, 50m), (returnable.Lines[0].Received, returnable.Lines[0].Returned, returnable.Lines[0].Returnable, returnable.Lines[0].UnitValue));

        var lineId = returnable.Lines[0].GrnLineId;
        var preview = await owner.PostJsonAsync($"/api/v1/businesses/{Business}/purchase-returns/preview",
            Returns.Request(grn.Id, "Torn packs", new PurchaseReturnLineRequest(lineId, 3)));
        await preview.EnsureSuccessWithBodyAsync();
        Assert.Equal(150m, (await preview.Content.ReadFromJsonAsync<PurchaseReturnDto>(TestClient.Json))!.Total);

        var first = await Returns.SaveAsync(owner, Business, Returns.Request(grn.Id, "Torn packs", new PurchaseReturnLineRequest(lineId, 3)));
        Assert.Matches(@"/DN/\d{6}$", first.Number);
        Assert.Equal((150m, 150m, 3m), (first.Total, first.StockValue, first.Lines[0].BaseQuantity));
        Assert.Equal(9m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);
        var open = await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id);
        Assert.Equal((450m, 450m), (open.Balance, Assert.Single(open.Charges).Remaining)); // deducted from this receipt

        // The rest goes back: exactly what was left of the receipt; nothing more can be returned.
        var rest = await Returns.SaveAsync(owner, Business, Returns.Request(grn.Id, "Supplier recalled the lot", new PurchaseReturnLineRequest(lineId, 9)));
        Assert.Equal(450m, rest.Total);
        Assert.Equal(0m, (await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id)).Balance);
        Assert.Equal(0m, (await StockTests.OnHandAsync(owner, Business, Store, product))!.Quantity);
        var tooMuch = await owner.PostJsonAsync($"/api/v1/businesses/{Business}/purchase-returns", Returns.Request(grn.Id, "Again", new PurchaseReturnLineRequest(lineId, 1)));
        Assert.Equal("purchase_return.quantity_too_large", await tooMuch.ProblemCodeAsync());
        Assert.Equal([first.Number, rest.Number], (await Returns.ReturnableAsync(owner, Business, grn.Id)).ReturnNumbers);

        // The debit note for the supplier.
        var pdf = await owner.GetAsync($"/api/v1/businesses/{Business}/purchase-returns/{first.Id}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]), StringComparison.Ordinal);
        Assert.Equal(line.Description, first.Lines[0].Description);
        Assert.Equal("purchase_return.reason_required", await (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/purchase-returns",
            Returns.Request(grn.Id, " ", new PurchaseReturnLineRequest(lineId, 1)))).ProblemCodeAsync());
    }

    [Fact]
    public async Task Returned_goods_leave_from_their_own_receipt_and_a_paid_receipt_leaves_a_credit()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, Business);
        var pack = product.Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);

        // Older stock at Rs. 40 (MRP 60), newer at Rs. 50 (MRP 70); FIFO would take the older first.
        await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 5, 40m, Mrp: 60m)));
        var newer = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 5, 50m, Mrp: 70m)));
        await Ledgers.PayAsync(owner, Business, Ledgers.Payment(Store, supplier.Id, 450m)); // both receipts paid in full

        var line = (await Returns.ReturnableAsync(owner, Business, newer.Id)).Lines[0].GrnLineId;
        var back = await Returns.SaveAsync(owner, Business, Returns.Request(newer.Id, "Wrong variant sent", new PurchaseReturnLineRequest(line, 2)));
        Assert.Equal((100m, 100m), (back.Total, back.StockValue)); // at the newer receipt's cost, not FIFO's Rs. 40

        // Nothing was unpaid, so the supplier now owes the business; the next receipt uses it up.
        var open = await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id);
        Assert.Equal((-100m, 100m), (open.Balance, Assert.Single(open.UnappliedPayments).Remaining));
        await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 3, 50m, Mrp: 70m)));
        open = await Ledgers.OpenItemsAsync(owner, Business, "suppliers", supplier.Id);
        Assert.Equal((50m, 50m, 0), (open.Balance, Assert.Single(open.Charges).Remaining, open.UnappliedPayments.Count));
    }

    [Fact]
    public async Task Concurrent_returns_cannot_send_back_more_than_was_received()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var grn = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 10m)));
        var line = (await Returns.ReturnableAsync(owner, Business, grn.Id)).Lines[0].GrnLineId;

        // Warm up the return path and open enough connections, so the requests below really overlap.
        await owner.PostJsonAsync($"/api/v1/businesses/{Business}/purchase-returns/preview", Returns.Request(grn.Id, "Damaged", new PurchaseReturnLineRequest(line, 4)));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => owner.GetAsync($"/api/v1/businesses/{Business}/grns/{grn.Id}/returnable")));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostJsonAsync($"/api/v1/businesses/{Business}/purchase-returns",
            Returns.Request(grn.Id, "Damaged", new PurchaseReturnLineRequest(line, 4)))));
        var saved = 0;
        foreach (var response in responses)
        {
            if (response.IsSuccessStatusCode)
            {
                saved++;
            }
            else
            {
                Assert.Equal("purchase_return.quantity_too_large", await response.ProblemCodeAsync());
            }
        }

        Assert.Equal(2, saved); // 4 + 4 of 10; a third would make 12
        Assert.Equal(2m, (await Returns.ReturnableAsync(owner, Business, grn.Id)).Lines[0].Returnable);
    }

    [Fact]
    public async Task Only_posted_receipts_and_purchase_managers_can_return_and_the_database_holds_the_line()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var grn = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 5, 10m)));
        var line = (await Returns.ReturnableAsync(owner, Business, grn.Id)).Lines[0].GrnLineId;
        var cashier = await factory.CreateSignedInUserAsync("cashier", Store);
        using (var client = cashier.Client)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync($"/api/v1/businesses/{Business}/purchase-returns",
                Returns.Request(grn.Id, "Damaged", new PurchaseReturnLineRequest(line, 1)))).StatusCode);
        }

        var note = await Returns.SaveAsync(owner, Business, Returns.Request(grn.Id, "Damaged", new PurchaseReturnLineRequest(line, 2)));
        var sql9 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "009_purchase_returns.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql9, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        // Even written directly, a return line cannot exceed what the receipt line received.
        await using (var forged = new NpgsqlCommand(
                         "INSERT INTO purchase_return_lines (id, tenant_id, business_id, purchase_return_id, grn_line_id, line_number, variant_id, variant_unit_id, " +
                         "description, unit_code, quantity, base_quantity, taxable, cgst, sgst, igst, cess, total, stock_value) " +
                         "SELECT gen_random_uuid(), tenant_id, business_id, purchase_return_id, grn_line_id, 2, variant_id, variant_unit_id, description, unit_code, " +
                         "4, 4, 0, 0, 0, 0, 0, 0, 0 FROM purchase_return_lines WHERE purchase_return_id = @id", admin))
        {
            forged.Parameters.AddWithValue("id", note.Id);
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync())).SqlState);
        }

        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand(
                         "ALTER TABLE purchase_return_lines DISABLE TRIGGER trg_purchase_return_lines_no_update_delete; " +
                         "UPDATE purchase_return_lines SET total = total + 1, taxable = taxable + 1 WHERE purchase_return_id = @id; " +
                         "ALTER TABLE purchase_return_lines ENABLE TRIGGER trg_purchase_return_lines_no_update_delete;", admin, transaction))
        {
            tamper.Parameters.AddWithValue("id", note.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql9, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("do not add up", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }
}

/// <summary>Own installation: on a GST tax invoice, a return reverses each tax in proportion.</summary>
public sealed class GstPurchaseReturnTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_return_reverses_cgst_and_sgst_in_proportion_and_the_last_return_closes_the_receipt_exactly()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (business, store) = await Sales.GstBillingTests.BusinessAsync(owner, "GSTRET", "GST_REGULAR", SupermarketBilling.Domain.Tax.Gstin.Complete("33AAACR5555F1Z"));
        var pack = (await CatalogTests.CreateProductAsync(owner, business, gst: 18)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, business, SupermarketBilling.Domain.Tax.Gstin.Complete("33AABCS6666G1Z"));
        var grn = await Purchasing.SaveAsync(owner, business, Purchasing.Request(store, supplier.Id, "GST_TAX_INVOICE", new GrnLineRequest(pack, 3, 33.33m)));
        var line = (await Returns.ReturnableAsync(owner, business, grn.Id)).Lines[0].GrnLineId;

        var one = await Returns.SaveAsync(owner, business, Returns.Request(grn.Id, "Expired", new PurchaseReturnLineRequest(line, 1)));
        Assert.Equal((33.33m, 3m, 3m, 0m, 39.33m), (one.Taxable, one.Cgst, one.Sgst, one.Igst, one.Total));
        var two = await Returns.SaveAsync(owner, business, Returns.Request(grn.Id, "Expired", new PurchaseReturnLineRequest(line, 2)));
        Assert.Equal((grn.TaxableTotal, grn.CgstTotal, grn.SgstTotal), (one.Taxable + two.Taxable, one.Cgst + two.Cgst, one.Sgst + two.Sgst));
        Assert.Equal(grn.InvoiceTotal, one.Total + two.Total);
        Assert.Equal(0m, (await Ledgers.OpenItemsAsync(owner, business, "suppliers", supplier.Id)).Balance);
    }
}
