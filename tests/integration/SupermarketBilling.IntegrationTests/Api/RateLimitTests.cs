using System.Net;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Api;

/// <summary>Own installation with low limits: sign-in attempts and all other requests are limited per client.</summary>
public sealed class RateLimitTests(RateLimitedApiFactory factory) : IClassFixture<RateLimitedApiFactory>
{
    [Fact]
    public async Task Sign_in_attempts_and_requests_are_limited_per_client()
    {
        using var client = factory.CreateBrowserClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < RateLimitedApiFactory.AuthPermitPerMinute + 2; i++)
        {
            statuses.Add((await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest("nobody", "wrong-password"))).StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(RateLimitedApiFactory.AuthPermitPerMinute));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < RateLimitedApiFactory.PermitPerMinute + 2 && last != HttpStatusCode.TooManyRequests; i++)
        {
            last = (await client.GetAsync("/api/v1/setup/status")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
