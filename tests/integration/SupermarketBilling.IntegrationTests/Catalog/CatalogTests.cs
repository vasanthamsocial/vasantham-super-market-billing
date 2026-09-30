using System.Net;
using System.Net.Http.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Catalog;

[Collection(ApiTestGroup.Name)]
public sealed class CatalogTests(ApiFactory factory)
{
    private string Catalog => $"/api/v1/businesses/{factory.BusinessId}/catalog";

    internal static async Task<Guid> UnitIdAsync(TestClient client, Guid businessId, string code) =>
        (await client.GetJsonAsync<List<UnitDto>>($"/api/v1/businesses/{businessId}/catalog/units")).Single(u => u.Code == code).Id;

    internal static async Task<ProductDetailDto> CreateProductAsync(
        TestClient client, Guid businessId, string? barcode = null, decimal? mrp = null, string supply = "TAXABLE", decimal gst = 5)
    {
        var code = $"P{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/catalog/products", new CreateProductRequest(
            code, $"Test product {code}", null, null, null, await UnitIdAsync(client, businessId, "PCS"), "1101", supply, gst, 0,
            false, false, false, false, new CreateVariantRequest(null, null, barcode, mrp)));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(TestClient.Json))!;
    }

    [Fact]
    public async Task New_business_starts_with_default_units_and_its_tax_registration()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        var units = await owner.GetJsonAsync<List<UnitDto>>($"{Catalog}/units");
        Assert.Contains(units, u => u.Code == "PCS" && u.DecimalPlaces == 0);
        Assert.Contains(units, u => u.Code == "KG" && u.DecimalPlaces == 3);

        var history = await owner.GetJsonAsync<List<TaxRegistrationDto>>($"/api/v1/businesses/{factory.BusinessId}/tax-registrations");
        var initial = history.Last();
        Assert.Equal("NOT_GST_REGISTERED", initial.Mode); // setup gave no GSTIN
        Assert.Null(initial.ApprovalRequestId);
    }

    [Fact]
    public async Task Product_with_barcode_mrp_and_box_pack_can_be_found_by_scan_and_search()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var barcode = Ean13(Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var product = await CreateProductAsync(owner, factory.BusinessId, barcode, 55m);
        var variant = Assert.Single(product.Variants);
        Assert.True(Assert.Single(variant.Units).IsBase);
        Assert.Equal(55m, Assert.Single(variant.Mrps).Mrp);

        // A box of 12 with its own barcode.
        var box = await (await owner.PostJsonAsync($"{Catalog}/variants/{variant.Id}/units",
            new AddVariantUnitRequest(await UnitIdAsync(owner, factory.BusinessId, "BOX"), 12m))).Content.ReadFromJsonAsync<VariantUnitDto>(TestClient.Json);
        var boxBarcode = Ean13(Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await owner.PostJsonAsync($"{Catalog}/variants/{variant.Id}/barcodes", new AddBarcodeRequest(box!.Id, boxBarcode))).EnsureSuccessStatusCode();

        var scanned = await owner.GetJsonAsync<BarcodeLookupDto>($"{Catalog}/lookup?barcode={boxBarcode}");
        Assert.Equal(variant.Id, scanned.VariantId);
        Assert.Equal("BOX", scanned.UnitCode);
        Assert.Equal(12m, scanned.FactorToBase);

        var byName = await owner.GetJsonAsync<List<ProductSummaryDto>>($"{Catalog}/products?search={Uri.EscapeDataString(product.Name[..14].ToLowerInvariant())}");
        Assert.Contains(byName, p => p.Id == product.Id);
        var byBarcode = await owner.GetJsonAsync<List<ProductSummaryDto>>($"{Catalog}/products?search={barcode}");
        Assert.Equal(product.Id, Assert.Single(byBarcode).Id);
    }

    [Fact]
    public async Task Barcodes_are_checked_and_cannot_be_used_twice_while_active()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var barcode = Ean13(Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var first = await CreateProductAsync(owner, factory.BusinessId, barcode);
        var second = await CreateProductAsync(owner, factory.BusinessId);
        var secondVariant = second.Variants.Single();
        var secondBase = secondVariant.Units.Single().Id;

        var duplicate = await owner.PostJsonAsync($"{Catalog}/variants/{secondVariant.Id}/barcodes", new AddBarcodeRequest(secondBase, barcode));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("barcode.in_use", await duplicate.ProblemCodeAsync());

        var misread = await owner.PostJsonAsync($"{Catalog}/variants/{secondVariant.Id}/barcodes",
            new AddBarcodeRequest(secondBase, barcode[..12] + ((barcode[12] - '0' + 1) % 10)));
        Assert.Equal("barcode.check_digit", await misread.ProblemCodeAsync());

        // Once retired on the first product, the manufacturer's reused code can move to the second.
        var firstBarcode = first.Variants.Single().Barcodes.Single();
        (await owner.PostJsonAsync($"{Catalog}/barcodes/{firstBarcode.Id}/deactivate", new { })).EnsureSuccessStatusCode();
        (await owner.PostJsonAsync($"{Catalog}/variants/{secondVariant.Id}/barcodes", new AddBarcodeRequest(secondBase, barcode))).EnsureSuccessStatusCode();
        Assert.Equal(secondVariant.Id, (await owner.GetJsonAsync<BarcodeLookupDto>($"{Catalog}/lookup?barcode={barcode}")).VariantId);
    }

    [Fact]
    public async Task Store_cashier_can_scan_and_view_but_not_change_the_catalogue()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var barcode = Ean13(Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await CreateProductAsync(owner, factory.BusinessId, barcode);
        var (cashier, _, _, _) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        using (cashier)
        {
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync($"{Catalog}/lookup?barcode={barcode}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync($"{Catalog}/products")).StatusCode);
            var create = await cashier.PostJsonAsync($"{Catalog}/brands", new CreateBrandRequest("Not allowed"));
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }
    }

    [Fact]
    public async Task Tax_classification_rules_are_enforced_and_edits_detect_conflicts()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var bad = await owner.PostJsonAsync($"{Catalog}/products", new CreateProductRequest(
            "EXEMPT5", "Exempt with rate", null, null, null, await UnitIdAsync(owner, factory.BusinessId, "KG"), "0713", "EXEMPT", 5, 0, false, false, false, false));
        Assert.Equal("product.rate_not_allowed", await bad.ProblemCodeAsync());

        var product = await CreateProductAsync(owner, factory.BusinessId);
        var update = new UpdateProductRequest(product.Name + " v2", null, null, null, "1101", "TAXABLE", 12, 0, false, false, false, false, true, product.RowVersion);
        (await owner.PutJsonAsync($"{Catalog}/products/{product.Id}", update)).EnsureSuccessStatusCode();

        var stale = await owner.PutJsonAsync($"{Catalog}/products/{product.Id}", update with { Name = "Lost update" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("concurrency", await stale.ProblemCodeAsync());
    }

    internal static string Ean13(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }

        return first12 + ((10 - (sum % 10)) % 10);
    }
}
