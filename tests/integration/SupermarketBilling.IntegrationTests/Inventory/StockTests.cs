using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Inventory;

[Collection(ApiTestGroup.Name)]
public sealed class StockTests(ApiFactory factory)
{
    private string Stock => $"/api/v1/businesses/{factory.BusinessId}/stock";

    internal static PostStockDocumentRequest Doc(string type, Guid storeId, params StockLineRequest[] lines) =>
        new(type, storeId, null, "Integration test posting", null, Guid.NewGuid().ToString("N"), false, lines);

    internal static StockLineRequest Line(ProductDetailDto product, decimal quantity, string? direction = null, decimal? cost = null) =>
        new(product.Variants.Single().Id, product.Variants.Single().Units.Single(u => u.IsBase).Id, quantity, direction, cost);

    internal static async Task<StockDocumentDto> PostAsync(TestClient client, Guid businessId, PostStockDocumentRequest request)
    {
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/stock/documents", request);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<StockDocumentDto>(TestClient.Json))!;
    }

    internal static async Task<StockOnHandDto?> OnHandAsync(TestClient client, Guid businessId, Guid storeId, ProductDetailDto product) =>
        (await client.GetJsonAsync<List<StockOnHandDto>>($"/api/v1/businesses/{businessId}/stock/on-hand?storeId={storeId}&search={product.Code}"))
            .SingleOrDefault(s => s.VariantId == product.Variants.Single().Id);

    [Fact]
    public async Task Fifo_issues_take_the_oldest_cost_first_and_every_movement_is_on_the_ledger()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);

        var opening = await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 10, cost: 5m)));
        Assert.Matches(@"^[A-Z0-9-]+/OPN/\d{6}$", opening.Number);
        await PostAsync(owner, factory.BusinessId, Doc("ADJUSTMENT", factory.MainStoreId, Line(product, 10, "IN", 7m)));
        var damage = await PostAsync(owner, factory.BusinessId, Doc("DAMAGE", factory.MainStoreId, Line(product, 15)));

        // 10 from the first layer at 5, then 5 from the second at 7.
        Assert.Collection(damage.Movements,
            m => Assert.Equal((-10m, 5m, -50m, 10m), (m.Quantity, m.UnitCost, m.Value, m.BalanceAfter)),
            m => Assert.Equal((-5m, 7m, -35m, 5m), (m.Quantity, m.UnitCost, m.Value, m.BalanceAfter)));

        var onHand = await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product);
        Assert.Equal(5m, onHand!.Quantity);
        Assert.Equal(35m, onHand.Value);

        var ledger = await owner.GetJsonAsync<List<LedgerEntryDto>>($"{Stock}/ledger?storeId={factory.MainStoreId}&variantId={product.Variants.Single().Id}");
        Assert.Equal(4, ledger.Count);
        Assert.Equal(5m, ledger.Sum(e => e.Quantity));
        Assert.Equal(opening.Number, ledger.Last().DocumentNumber);
    }

    [Fact]
    public async Task Opening_stock_can_only_be_entered_once_and_needs_a_cost()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        var url = $"{Stock}/documents";

        var noCost = await owner.PostJsonAsync(url, Doc("OPENING", factory.MainStoreId, Line(product, 5)));
        Assert.Equal("stock.cost_required", await noCost.ProblemCodeAsync());

        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 5, cost: 3m)));
        var again = await owner.PostJsonAsync(url, Doc("OPENING", factory.MainStoreId, Line(product, 5, cost: 3m)));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("stock.opening_exists", await again.ProblemCodeAsync());

        // An adjustment in without a cost uses the current cost.
        var adjusted = await PostAsync(owner, factory.BusinessId, Doc("ADJUSTMENT", factory.MainStoreId, Line(product, 2, "IN")));
        Assert.Equal(3m, adjusted.Movements.Single().UnitCost);
    }

    [Fact]
    public async Task Retrying_with_the_same_key_returns_the_same_document_and_a_changed_request_is_refused()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 50, cost: 2m)));

        var request = Doc("WASTAGE", factory.MainStoreId, Line(product, 4));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(owner, factory.BusinessId, request)));
        Assert.Single(attempts.Select(a => a.Id).Distinct());
        Assert.Equal(46m, (await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product))!.Quantity);

        var changed = await owner.PostJsonAsync($"{Stock}/documents", request with { Lines = [Line(product, 5)] });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("idempotency.mismatch", await changed.ProblemCodeAsync());
    }

    [Fact]
    public async Task Concurrent_issues_never_take_more_stock_than_there_is()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 100, cost: 1m)));

        // Different document types use different number series, so only the stock balance lock serialises them.
        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(i =>
            owner.PostJsonAsync($"{Stock}/documents", (i % 3) switch
            {
                0 => Doc("DAMAGE", factory.MainStoreId, Line(product, 5)),
                1 => Doc("WASTAGE", factory.MainStoreId, Line(product, 5)),
                _ => Doc("ADJUSTMENT", factory.MainStoreId, Line(product, 5, "OUT")),
            })));
        Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("stock.insufficient", await refused.ProblemCodeAsync());
        }

        Assert.Equal(0m, (await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product))!.Quantity);
        var numbers = await owner.GetJsonAsync<List<StockDocumentSummaryDto>>($"{Stock}/documents?storeId={factory.MainStoreId}");
        Assert.Equal(numbers.Count, numbers.Select(n => n.Number).Distinct().Count());
    }

    [Fact]
    public async Task Negative_stock_follows_the_rule_loosening_needs_approval_and_tightening_applies_at_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 2, cost: 4m)));
        var issue = Doc("DAMAGE", factory.MainStoreId, Line(product, 5));

        var refused = await owner.PostJsonAsync($"{Stock}/documents", issue);
        Assert.Equal("stock.insufficient", await refused.ProblemCodeAsync());

        // Loosening for this product: waits for the second person.
        var request = await owner.PostJsonAsync($"{Stock}/negative-rules",
            new SetNegativeStockRuleRequest(null, product.Id, "WARN_OVERRIDE", null, "Supplier delivers before the paperwork"));
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var pending = (await request.Content.ReadFromJsonAsync<SetNegativeStockRuleResponse>(TestClient.Json))!;
        Assert.Equal("stock.insufficient", await (await owner.PostJsonAsync($"{Stock}/documents", issue with { NegativeStockOverride = true })).ProblemCodeAsync());

        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        (await approver.PostJsonAsync($"/api/v1/approvals/{pending.ApprovalRequestId}/approve", new ApprovalDecisionRequest("ok"))).EnsureSuccessStatusCode();

        Assert.Equal("stock.insufficient", await (await owner.PostJsonAsync($"{Stock}/documents", issue)).ProblemCodeAsync());
        var cashier = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        using (cashier.Client)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Client.PostJsonAsync($"{Stock}/documents", issue with { NegativeStockOverride = true })).StatusCode);
        }

        var overridden = await PostAsync(owner, factory.BusinessId, issue with { NegativeStockOverride = true });
        Assert.Equal(-3m, overridden.Movements[^1].BalanceAfter);
        Assert.True((await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product))!.IsNegative);

        // The next receipt first covers the 3 already sold; only 7 of it is left on the shelf.
        await PostAsync(owner, factory.BusinessId, Doc("ADJUSTMENT", factory.MainStoreId, Line(product, 10, "IN", 6m)));
        var after = await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product);
        Assert.Equal((7m, 42m), (after!.Quantity, after.Value));

        // Tightening back to "not allowed" needs nobody else.
        var tighten = await owner.PostJsonAsync($"{Stock}/negative-rules",
            new SetNegativeStockRuleRequest(null, product.Id, "DISABLED", null, "Paperwork fixed"));
        Assert.Equal(HttpStatusCode.OK, tighten.StatusCode);
        var rules = await owner.GetJsonAsync<List<NegativeStockRuleDto>>($"{Stock}/negative-rules");
        Assert.Equal("DISABLED", Assert.Single(rules, r => r.ProductId == product.Id && r.IsActive).Mode);
        Assert.Equal("stock.insufficient",
            await (await owner.PostJsonAsync($"{Stock}/documents", Doc("DAMAGE", factory.MainStoreId, Line(product, 8)) with { NegativeStockOverride = true })).ProblemCodeAsync());
    }

    [Fact]
    public async Task Negative_stock_with_a_limit_stops_at_the_limit()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        var set = await owner.PostJsonAsync($"{Stock}/negative-rules",
            new SetNegativeStockRuleRequest(factory.MainStoreId, product.Id, "ENABLED_WITH_LIMIT", 3, "Fast mover, counted daily"));
        var pending = (await set.Content.ReadFromJsonAsync<SetNegativeStockRuleResponse>(TestClient.Json))!;
        using (var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword))
        {
            (await approver.PostJsonAsync($"/api/v1/approvals/{pending.ApprovalRequestId}/approve", new ApprovalDecisionRequest(null))).EnsureSuccessStatusCode();
        }

        var first = await PostAsync(owner, factory.BusinessId, Doc("DAMAGE", factory.MainStoreId, Line(product, 3)));
        Assert.Equal(-3m, first.Movements.Single().BalanceAfter);
        Assert.Null(first.Movements.Single().BatchNumber);
        var beyond = await owner.PostJsonAsync($"{Stock}/documents", Doc("DAMAGE", factory.MainStoreId, Line(product, 1)));
        Assert.Equal("stock.insufficient", await beyond.ProblemCodeAsync());
    }

    [Fact]
    public async Task Transfers_move_stock_with_its_cost_and_both_stores_see_the_document()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        var target = await CreateStoreAsync(owner);
        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 10, cost: 4m)));
        await PostAsync(owner, factory.BusinessId, Doc("ADJUSTMENT", factory.MainStoreId, Line(product, 5, "IN", 6m)));

        var transfer = await PostAsync(owner, factory.BusinessId,
            Doc("TRANSFER", factory.MainStoreId, Line(product, 12)) with { TargetStoreId = target });
        Assert.Equal(0m, transfer.Movements.Sum(m => m.Quantity));
        Assert.Equal(0m, transfer.Movements.Sum(m => m.Value));

        var source = await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product);
        Assert.Equal((3m, 18m), (source!.Quantity, source.Value));
        var destination = await OnHandAsync(owner, factory.BusinessId, target, product);
        Assert.Equal((12m, 52m), (destination!.Quantity, destination.Value));

        var seenByTarget = await owner.GetJsonAsync<List<StockDocumentSummaryDto>>($"{Stock}/documents?storeId={target}");
        Assert.Contains(seenByTarget, d => d.Id == transfer.Id);

        var toSelf = await owner.PostJsonAsync($"{Stock}/documents", Doc("TRANSFER", target, Line(product, 1)) with { TargetStoreId = target });
        Assert.Equal("transfer.target_invalid", await toSelf.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_count_posts_the_difference_from_the_book_quantity()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 10, cost: 5m)));

        var loss = await PostAsync(owner, factory.BusinessId, Doc("COUNT", factory.MainStoreId, Line(product, 7)));
        Assert.Equal(("COUNT_LOSS", -3m), (loss.Movements.Single().MovementType, loss.Movements.Single().Quantity));
        var gain = await PostAsync(owner, factory.BusinessId, Doc("COUNT", factory.MainStoreId, Line(product, 12)));
        Assert.Equal(("COUNT_GAIN", 5m, 5m), (gain.Movements.Single().MovementType, gain.Movements.Single().Quantity, gain.Movements.Single().UnitCost));
        var same = await PostAsync(owner, factory.BusinessId, Doc("COUNT", factory.MainStoreId, Line(product, 12)));
        Assert.Empty(same.Movements);
        await PostAsync(owner, factory.BusinessId, Doc("COUNT", factory.MainStoreId, Line(product, 0)));
        Assert.Equal(0m, (await OnHandAsync(owner, factory.BusinessId, factory.MainStoreId, product))!.Quantity);

        var zeroDamage = await owner.PostJsonAsync($"{Stock}/documents", Doc("DAMAGE", factory.MainStoreId, Line(product, 0)));
        Assert.Equal("stock.quantity_invalid", await zeroDamage.ProblemCodeAsync());
    }

    [Fact]
    public async Task Batch_tracked_items_need_a_batch_and_a_named_batch_cannot_be_overdrawn()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId, tracksBatches: true, tracksExpiry: true);
        var url = $"{Stock}/documents";

        Assert.Equal("batch.required", await (await owner.PostJsonAsync(url, Doc("OPENING", factory.MainStoreId, Line(product, 5, cost: 2m)))).ProblemCodeAsync());
        Assert.Equal("batch.expiry_required", await (await owner.PostJsonAsync(url,
            Doc("OPENING", factory.MainStoreId, Line(product, 5, cost: 2m) with { BatchNumber = "B1" }))).ProblemCodeAsync());

        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId,
            Line(product, 5, cost: 2m) with { BatchNumber = "b1", ExpiresOn = new DateOnly(2027, 1, 31) }));
        await PostAsync(owner, factory.BusinessId, Doc("ADJUSTMENT", factory.MainStoreId,
            Line(product, 8, "IN", 3m) with { BatchNumber = "B2", ExpiresOn = new DateOnly(2026, 11, 30) }));

        var batches = await owner.GetJsonAsync<List<BatchStockDto>>($"{Stock}/batches?storeId={factory.MainStoreId}");
        var b2 = Assert.Single(batches, b => b.VariantId == product.Variants.Single().Id && b.BatchNumber == "B2");
        Assert.Equal((8m, 60), (b2.Quantity, b2.DaysToExpiry!.Value));
        Assert.Contains(batches, b => b.VariantId == product.Variants.Single().Id && b.BatchNumber == "B1"); // stored upper-case

        var fromB2 = await PostAsync(owner, factory.BusinessId, Doc("DAMAGE", factory.MainStoreId, Line(product, 3) with { BatchId = b2.BatchId }));
        Assert.Equal(("B2", 3m), (fromB2.Movements.Single().BatchNumber, fromB2.Movements.Single().UnitCost));

        var tooMuch = await owner.PostJsonAsync(url, Doc("DAMAGE", factory.MainStoreId, Line(product, 6) with { BatchId = b2.BatchId }));
        Assert.Equal("stock.batch_insufficient", await tooMuch.ProblemCodeAsync());

        var conflicting = await owner.PostJsonAsync(url, Doc("ADJUSTMENT", factory.MainStoreId,
            Line(product, 1, "IN", 3m) with { BatchNumber = "B2", ExpiresOn = new DateOnly(2026, 12, 31) }));
        Assert.Equal("batch.expiry_mismatch", await conflicting.ProblemCodeAsync());
    }

    [Fact]
    public async Task Fractional_quantities_follow_the_stock_unit()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pieces = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        var loose = await CatalogTests.CreateProductAsync(owner, factory.BusinessId, unit: "KG");

        var half = await owner.PostJsonAsync($"{Stock}/documents", Doc("OPENING", factory.MainStoreId, Line(pieces, 1.5m, cost: 2m)));
        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);

        var weighed = await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(loose, 12.345m, cost: 80m)));
        Assert.Equal((12.345m, 987.6m), (weighed.Movements.Single().Quantity, weighed.Movements.Single().Value));
    }

    [Fact]
    public async Task Stock_posting_needs_the_permission_for_that_store()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        var otherStore = await CreateStoreAsync(owner);

        var cashier = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        using (cashier.Client)
        {
            var refused = await cashier.Client.PostJsonAsync($"{Stock}/documents", Doc("OPENING", factory.MainStoreId, Line(product, 1, cost: 1m)));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            (await cashier.Client.GetAsync(new Uri($"{Stock}/on-hand?storeId={factory.MainStoreId}", UriKind.Relative))).EnsureSuccessStatusCode();
        }

        var keeper = await factory.CreateSignedInUserAsync("inventory_operator", factory.MainStoreId);
        using (keeper.Client)
        {
            await PostAsync(keeper.Client, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 1, cost: 1m)));
            var elsewhere = await keeper.Client.PostJsonAsync($"{Stock}/documents", Doc("OPENING", otherStore, Line(product, 1, cost: 1m)));
            Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);
            var settings = await keeper.Client.PostJsonAsync($"{Stock}/negative-rules",
                new SetNegativeStockRuleRequest(factory.MainStoreId, product.Id, "WARN_OVERRIDE", null, "Trying to loosen"));
            Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        }
    }

    [Fact]
    public async Task Valuation_method_is_locked_once_stock_has_moved()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 1, cost: 1m)));

        var settings = await owner.GetJsonAsync<InventorySettingsDto>($"{Stock}/settings");
        Assert.Equal(("FIFO", true), (settings.ValuationMethod, settings.ValuationLocked));
        var change = await owner.PutJsonAsync($"{Stock}/settings", new UpdateInventorySettingsRequest("WEIGHTED_AVERAGE"));
        Assert.Equal("stock.valuation_locked", await change.ProblemCodeAsync());
    }

    [Fact]
    public async Task Database_keeps_the_stock_ledger_and_cost_layers_tamper_proof()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        var opening = await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 3, cost: 1m)));
        await PostAsync(owner, factory.BusinessId, Doc("DAMAGE", factory.MainStoreId, Line(product, 1)));

        await using var db = await factory.OpenAppConnectionAsync();
        foreach (var sql in new[]
        {
            "UPDATE stock_ledger SET quantity = 30, value = 30 WHERE document_id = @id",
            "DELETE FROM stock_ledger WHERE document_id = @id",
            "UPDATE stock_documents SET reason = 'changed' WHERE id = @id",
            "DELETE FROM stock_documents WHERE id = @id",
            "UPDATE cost_layers SET remaining_quantity = original_quantity WHERE id IN (SELECT layer_id FROM stock_ledger WHERE document_id = @id)",
            "UPDATE cost_layers SET unit_cost = 0 WHERE id IN (SELECT layer_id FROM stock_ledger WHERE document_id = @id)",
            "DELETE FROM cost_layers WHERE id IN (SELECT layer_id FROM stock_ledger WHERE document_id = @id)",
        })
        {
            await using var command = new NpgsqlCommand(sql, db);
            command.Parameters.AddWithValue("id", opening.Id);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task Database_verification_reconciles_balances_layers_and_numbering()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var product = await CatalogTests.CreateProductAsync(owner, factory.BusinessId);
        await PostAsync(owner, factory.BusinessId, Doc("OPENING", factory.MainStoreId, Line(product, 4, cost: 2m)));
        await PostAsync(owner, factory.BusinessId, Doc("WASTAGE", factory.MainStoreId, Line(product, 1)));

        var sql = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "database", "verification", "002_inventory.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql, admin))
        {
            await verify.ExecuteNonQueryAsync(); // raises if anything is out of step
        }

        // And it does catch a balance that no longer matches its ledger.
        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand("UPDATE stock_balances SET quantity = quantity + 1 WHERE variant_id = @v", admin, transaction))
        {
            tamper.Parameters.AddWithValue("v", product.Variants.Single().Id);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql, admin, transaction);
        var error = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("stock balances not equal to their ledger", error.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    private async Task<Guid> CreateStoreAsync(TestClient owner)
    {
        var code = $"S{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var response = await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stores",
            new CreateStoreRequest(code, $"Branch {code}", "33", null, null));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<StoreDto>(TestClient.Json))!.Id;
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SupermarketBilling.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
