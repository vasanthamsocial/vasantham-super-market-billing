using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Api;

[Collection(ApiTestGroup.Name)]
public sealed class HealthAndSystemEndpointTests(ApiFactory factory)
{
    [Fact]
    public async Task Liveness_is_healthy_without_checking_dependencies()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task Readiness_reports_database_and_schema_healthy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        var checks = body.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("Healthy", checks["database"]);
        Assert.Equal("Healthy", checks["schema"]);
    }

    [Fact]
    public async Task Readiness_is_unavailable_when_the_database_cannot_be_reached()
    {
        // Port 1 is never a PostgreSQL server; the check must fail fast and report 503, not hang or crash.
        await using var unreachable = new ApiFactory("Host=127.0.0.1;Port=1;Database=x_test;Username=x;Password=x;Timeout=2");
        using var client = unreachable.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"Unhealthy\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task System_info_reports_version_and_archive_feature_disabled_by_default()
    {
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/system/info", UriKind.Relative));

        Assert.Equal("SupermarketBilling", info.GetProperty("application").GetString());
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("version").GetString()));
        Assert.False(info.GetProperty("archiveWebEnabled").GetBoolean());
        var serverTime = info.GetProperty("serverTimeUtc").GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, serverTime.Offset);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/system/info", UriKind.Relative));

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_system_endpoint()
    {
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/system/info", out _));
    }
}
