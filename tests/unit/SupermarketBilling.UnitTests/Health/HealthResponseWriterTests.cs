using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SupermarketBilling.Api.Health;

namespace SupermarketBilling.UnitTests.Health;

public sealed class HealthResponseWriterTests
{
    [Fact]
    public async Task Writes_status_and_checks_without_exception_details()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var secretBearingException = new InvalidOperationException("Host=db;Password=pw");
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["database"] = new(HealthStatus.Unhealthy, "Database unreachable.", TimeSpan.FromMilliseconds(12.34), secretBearingException, null),
            },
            TimeSpan.FromMilliseconds(15));

        await HealthResponseWriter.WriteAsync(context, report);

        context.Response.Body.Position = 0;
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("Unhealthy", root.GetProperty("status").GetString());
        var check = Assert.Single(root.GetProperty("checks").EnumerateArray());
        Assert.Equal("database", check.GetProperty("name").GetString());
        Assert.Equal("Database unreachable.", check.GetProperty("description").GetString());
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }
}
