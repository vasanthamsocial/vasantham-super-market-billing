using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;

namespace SupermarketBilling.IntegrationTests.Purchases;

internal static class Purchasing
{
    public static async Task<SupplierDto> SupplierAsync(TestClient client, Guid businessId, string? gstin = null, string state = "33")
    {
        var code = $"S{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/suppliers", new CreateSupplierRequest(code, $"Supplier {code}", gstin, state, null, null));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<SupplierDto>(TestClient.Json))!;
    }

    public static GrnRequest Request(Guid storeId, Guid supplierId, string classification, params GrnLineRequest[] lines) =>
        new(storeId, supplierId, $"INV-{Guid.NewGuid():N}"[..14], new DateOnly(2026, 9, 30), classification, lines, IdempotencyKey: Guid.NewGuid().ToString("N"));

    public static async Task<GrnDto> PreviewAsync(TestClient client, Guid businessId, GrnRequest request)
    {
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/grns/preview", request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<GrnDto>(TestClient.Json))!;
    }

    public static async Task<GrnDto> SaveAsync(TestClient client, Guid businessId, GrnRequest request)
    {
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/grns", request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<GrnDto>(TestClient.Json))!;
    }
}

[Collection(ApiTestGroup.Name)]
public sealed class GrnTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    [Fact]
    public async Task A_receipt_spreads_freight_over_its_lines_and_puts_stock_in_at_landed_cost_in_its_batch()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var rice = await CatalogTests.CreateProductAsync(owner, Business, mrp: 60m);
        var oil = await CatalogTests.CreateProductAsync(owner, Business, mrp: 200m, tracksBatches: true, tracksExpiry: true);
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var ricePack = rice.Variants.Single().Units.Single().Id;
        var oilPack = oil.Variants.Single().Units.Single().Id;

        // An unregistered supplier charges no GST.
        var request = Purchasing.Request(Store, supplier.Id, "UNREGISTERED",
                new GrnLineRequest(ricePack, 100, 40m, FreeQuantity: 10, Mrp: 60m),
                new GrnLineRequest(oilPack, 20, 150m, Mrp: 200m, BatchNumber: "OIL-26", ExpiresOn: new DateOnly(2027, 6, 30)))
            with { Expenses = [new GrnExpenseRequest("FREIGHT", 70m, "QUANTITY")], SupplierInvoiceTotal = 7000m };
        var preview = await Purchasing.PreviewAsync(owner, Business, request);
        Assert.Empty(preview.Issues);
        Assert.Equal([59.23m, 10.77m], preview.Expenses.Single().Allocations); // 70 shared 110 : 20 pieces (free goods included)
        Assert.Equal((4059.23m, 36.9021m), (preview.Lines[0].LandedTotal, preview.Lines[0].LandedUnitCost));

        var grn = await Purchasing.SaveAsync(owner, Business, request);
        Assert.Equal("POSTED", grn.Status);
        Assert.Matches(@"/GRN/\d{6}$", grn.Number);
        Assert.Equal((7000m, 70m, 7070m), (grn.InvoiceTotal, grn.ExpensesTotal, grn.LandedTotal));

        var onHand = await StockTests.OnHandAsync(owner, Business, Store, rice);
        Assert.Equal(110m, onHand!.Quantity);
        var batches = await owner.GetJsonAsync<List<BatchStockDto>>($"/api/v1/businesses/{Business}/stock/batches?storeId={Store}");
        Assert.Contains(batches, b => b.BatchNumber == "OIL-26" && b.Quantity == 20m);

        // The same supplier invoice cannot be received twice.
        var again = await owner.PostJsonAsync($"/api/v1/businesses/{Business}/grns", request with { IdempotencyKey = Guid.NewGuid().ToString("N") });
        Assert.Equal("grn.invoice_already_received", await again.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_cost_change_needs_a_reason_and_a_large_one_waits_for_a_managers_approval_before_stock_moves()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var item = await CatalogTests.CreateProductAsync(owner, Business, mrp: 100m);
        var pack = item.Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var keeper = await factory.CreateSignedInUserAsync("purchase_operator", Store);
        using var receiver = keeper.Client;

        await Purchasing.SaveAsync(receiver, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 50m, Mrp: 100m)));

        // +8%: a reason is needed (threshold 5%), no approval (15%).
        var plus8 = Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 54m, Mrp: 100m));
        var preview = await Purchasing.PreviewAsync(receiver, Business, plus8);
        var change = preview.Lines[0].CostChange!;
        Assert.Equal((50m, 54m, 8m, true, false, supplier.Name), (change.PreviousUnitCost, change.NewUnitCost, change.PercentChange, change.NeedsReason, change.NeedsApproval, change.PreviousSupplier));
        Assert.Equal("grn.cost_change_reason_required", await (await receiver.PostJsonAsync($"/api/v1/businesses/{Business}/grns", plus8)).ProblemCodeAsync());
        var withReason = await Purchasing.SaveAsync(receiver, Business, plus8 with { Lines = [plus8.Lines[0] with { CostChangeReason = "Diesel price rise" }] });
        Assert.Equal("POSTED", withReason.Status);

        // +20% on top: waits for approval; no stock moves until then.
        var before = (await StockTests.OnHandAsync(owner, Business, Store, item))!.Quantity;
        var big = Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 65m, Mrp: 100m, CostChangeReason: "New season crop"));
        var pending = await Purchasing.SaveAsync(receiver, Business, big);
        Assert.Equal(("PENDING_APPROVAL", true), (pending.Status, pending.NeedsApproval));
        Assert.Equal(before, (await StockTests.OnHandAsync(owner, Business, Store, item))!.Quantity);

        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        (await approver.PostJsonAsync($"/api/v1/approvals/{pending.ApprovalRequestId}/approve", new ApprovalDecisionRequest("Checked with supplier"))).EnsureSuccessStatusCode();
        var posted = await owner.GetJsonAsync<GrnDto>($"/api/v1/businesses/{Business}/grns/{pending.Id}");
        Assert.Equal("POSTED", posted.Status);
        Assert.Equal(before + 10, (await StockTests.OnHandAsync(owner, Business, Store, item))!.Quantity);
    }

    [Fact]
    public async Task Selling_below_landed_cost_is_blocked_with_the_loss_shown()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (product, pack) = await Sales.Pos.StockedProductAsync(owner, Business, Store, price: 52m, mrp: 55m, stock: 0);
        var supplier = await Purchasing.SupplierAsync(owner, Business);

        // Landed 53 a piece against a retail price of 52: blocked.
        var request = Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 53m, Mrp: 55m));
        var preview = await Purchasing.PreviewAsync(owner, Business, request);
        var below = preview.Lines[0].BelowCost!;
        Assert.Equal((52m, 53m, 1m), (below.SellingPrice, below.CostPerPack, below.LossPerPack));
        Assert.Contains(preview.Issues, i => i.Code == "grn.below_cost" && i.LineNumber == 1);
        var refused = await owner.PostJsonAsync($"/api/v1/businesses/{Business}/grns", request);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("grn.below_cost", await refused.ProblemCodeAsync());
        Assert.Null(await StockTests.OnHandAsync(owner, Business, Store, product));

        // Corrected selling price: accepted.
        var fixedPrice = await Purchasing.SaveAsync(owner, Business, request with { Lines = [request.Lines[0] with { SellingPrice = 54m }] });
        Assert.Equal("POSTED", fixedPrice.Status);
    }

    [Fact]
    public async Task Classification_must_fit_the_supplier_and_the_printed_total_must_match()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var item = await CatalogTests.CreateProductAsync(owner, Business);
        var pack = item.Variants.Single().Units.Single().Id;
        var unregistered = await Purchasing.SupplierAsync(owner, Business);
        var preview = await Purchasing.PreviewAsync(owner, Business, Purchasing.Request(Store, unregistered.Id, "GST_TAX_INVOICE", new GrnLineRequest(pack, 1, 10m)));
        Assert.Contains(preview.Issues, i => i.Code == "grn.supplier_not_registered");

        var mismatch = Purchasing.Request(Store, unregistered.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 10m)) with { SupplierInvoiceTotal = 110m };
        Assert.Contains((await Purchasing.PreviewAsync(owner, Business, mismatch)).Issues, i => i.Code == "grn.invoice_total_mismatch");
        var rounded = await Purchasing.SaveAsync(owner, Business, mismatch with { SupplierInvoiceTotal = 100.40m }); // 100.00 computed: 0.40 round-off
        Assert.Equal((0.40m, 100.40m), (rounded.RoundOff, rounded.InvoiceTotal));
    }

    [Fact]
    public async Task Database_keeps_receipts_fixed_and_verification_reconciles_them()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var a = await CatalogTests.CreateProductAsync(owner, Business);
        var b = await CatalogTests.CreateProductAsync(owner, Business);
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var grn = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED",
                new GrnLineRequest(a.Variants.Single().Units.Single().Id, 3, 10m), new GrnLineRequest(b.Variants.Single().Units.Single().Id, 7, 10m))
            with { Expenses = [new GrnExpenseRequest("LOADING", 10m, "EQUAL")] });

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[] { "UPDATE grns SET landed_total = 1 WHERE id = @id", "DELETE FROM grns WHERE id = @id", "UPDATE grn_lines SET rate = 1 WHERE grn_id = @id" })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("id", grn.Id);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
            }
        }

        var sql6 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "006_purchases.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql6, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand(
                         "ALTER TABLE grn_allocations DISABLE TRIGGER trg_grn_allocations_no_update_delete; " +
                         "UPDATE grn_allocations SET amount = amount + 1 WHERE expense_id IN (SELECT id FROM grn_expenses WHERE grn_id = @id) AND amount > 0; " +
                         "ALTER TABLE grn_allocations ENABLE TRIGGER trg_grn_allocations_no_update_delete;", admin, transaction))
        {
            tamper.Parameters.AddWithValue("id", grn.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql6, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("expenses not allocated exactly", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }
}

/// <summary>Own installation: a GST-registered business recovers GST on a tax invoice, so it is not part of the cost.</summary>
public sealed class GstPurchaseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Gst_on_a_tax_invoice_is_recoverable_but_on_a_bill_of_supply_it_is_cost()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (business, store) = await Sales.GstBillingTests.BusinessAsync(owner, "GSTBUY", "GST_REGULAR", SupermarketBilling.Domain.Tax.Gstin.Complete("33AAACB7777F1Z"));
        var item = await CatalogTests.CreateProductAsync(owner, business, gst: 18);
        var pack = item.Variants.Single().Units.Single().Id;
        var karnataka = await Purchasing.SupplierAsync(owner, business, SupermarketBilling.Domain.Tax.Gstin.Complete("29AABCK1111G1Z"), "29");

        var taxInvoice = await Purchasing.PreviewAsync(owner, business, Purchasing.Request(store, karnataka.Id, "GST_TAX_INVOICE", new GrnLineRequest(pack, 10, 100m)));
        Assert.Equal((true, true, 180m, 0m, 100m), (taxInvoice.IsInterState, taxInvoice.TaxRecoverable, taxInvoice.IgstTotal, taxInvoice.Lines[0].NonRecoverableTax, taxInvoice.Lines[0].LandedUnitCost));

        var pendingDocument = await Purchasing.PreviewAsync(owner, business, Purchasing.Request(store, karnataka.Id, "PENDING_DOCUMENT", new GrnLineRequest(pack, 10, 100m)));
        Assert.Equal((false, 180m, 118m), (pendingDocument.TaxRecoverable, pendingDocument.Lines[0].NonRecoverableTax, pendingDocument.Lines[0].LandedUnitCost));
    }

    [Fact]
    public async Task A_loss_leader_needs_the_setting_a_reason_and_another_managers_approval()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (business, store) = await Sales.GstBillingTests.BusinessAsync(owner, "LOSSLD", "NOT_GST_REGISTERED", null);
        var manager = await factory.CreateSignedInUserAsync("manager", businessId: business);
        using var approver = manager.Client;
        var (_, pack) = await Sales.Pos.StockedProductAsync(owner, business, store, price: 45m, mrp: 60m, stock: 0);
        var supplier = await Purchasing.SupplierAsync(owner, business);
        var below = Purchasing.Request(store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 50m, Mrp: 60m));

        Assert.Contains((await Purchasing.PreviewAsync(owner, business, below)).Issues, i => i.Code == "grn.below_cost");

        var settings = await owner.GetJsonAsync<PurchaseSettingsDto>($"/api/v1/businesses/{business}/purchase-settings");
        (await owner.PutJsonAsync($"/api/v1/businesses/{business}/purchase-settings", settings with { AllowLossLeader = true })).EnsureSuccessStatusCode();
        Assert.Contains((await Purchasing.PreviewAsync(owner, business, below)).Issues, i => i.Code == "grn.loss_leader_reason_required");

        var pending = await Purchasing.SaveAsync(owner, business, below with { Lines = [below.Lines[0] with { LossLeaderReason = "Diwali crowd-puller" }] });
        Assert.Equal("PENDING_APPROVAL", pending.Status);
        (await approver.PostJsonAsync($"/api/v1/approvals/{pending.ApprovalRequestId}/approve", new ApprovalDecisionRequest("For the festival week"))).EnsureSuccessStatusCode();
        Assert.Equal("POSTED", (await owner.GetJsonAsync<GrnDto>($"/api/v1/businesses/{business}/grns/{pending.Id}")).Status);
    }
}
