using System.Net;
using System.Text;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Catalog;

[Collection(ApiTestGroup.Name)]
public sealed class RequestValidationTests(ApiFactory factory)
{
    [Fact]
    public async Task Unreadable_fields_are_named_in_the_error_without_echoing_the_value()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        const string json = """{"code":"BADUNIT","name":"x","baseUnitId":"not-a-guid-secret","hsnSac":"1101","supplyType":"TAXABLE","gstRatePercent":5,"cessRatePercent":0}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await owner.Http.PostAsync(new Uri($"/api/v1/businesses/{factory.BusinessId}/catalog/products", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("baseUnitId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-guid-secret", body, StringComparison.Ordinal);
    }
}