using System.Net;
using System.Net.Http.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Identity;

/// <summary>First-run setup on an empty installation (its own database).</summary>
public sealed class SetupTests(EmptyApiFactory factory) : IClassFixture<EmptyApiFactory>
{
    [Fact]
    public async Task Setup_requires_the_server_side_code_runs_once_and_then_allows_owner_sign_in()
    {
        using var client = factory.CreateBrowserClient();

        var status = await client.GetJsonAsync<SetupStatusResponse>("/api/v1/setup/status");
        Assert.True(status.SetupRequired);
        Assert.True(File.Exists(factory.SetupCodeFile), "The setup code file must be created while no users exist.");
        var setupCode = (await File.ReadAllTextAsync(factory.SetupCodeFile)).Trim();
        Assert.DoesNotContain(factory.LogMessages, m => m.Contains(setupCode, StringComparison.Ordinal));

        var wrongCode = await client.PostJsonAsync("/api/v1/setup", ApiFactory.NewSetupRequest("AAAA-BBBB-CCCC-DDDD"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongCode.StatusCode);
        Assert.Equal("setup.code_invalid", await wrongCode.ProblemCodeAsync());

        var code = await File.ReadAllTextAsync(factory.SetupCodeFile);

        // Two simultaneous attempts: exactly one may succeed.
        var attempts = await Task.WhenAll(
            client.PostJsonAsync("/api/v1/setup", ApiFactory.NewSetupRequest(code)),
            factory.CreateBrowserClient().PostJsonAsync("/api/v1/setup", ApiFactory.NewSetupRequest(code)));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(attempts, r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);

        Assert.False(File.Exists(factory.SetupCodeFile), "The setup code must be deleted after use.");
        Assert.False((await client.GetJsonAsync<SetupStatusResponse>("/api/v1/setup/status")).SetupRequired);

        var again = await client.PostJsonAsync("/api/v1/setup", ApiFactory.NewSetupRequest(code));
        Assert.True(again.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);

        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var me = await owner.GetJsonAsync<MeResponse>("/api/v1/auth/me");
        Assert.Equal(SessionStates.Active, me.SessionState);
        var membership = Assert.Single(me.Memberships);
        Assert.Equal(ApiFactory.BusinessCode, membership.BusinessCode);
        Assert.Contains(membership.Roles, r => r.RoleCode == "owner" && r.StoreId == null);

        var audit = await owner.GetJsonAsync<List<AuditEventDto>>($"/api/v1/businesses/{membership.BusinessId}/audit");
        Assert.Contains(audit, e => e.EventType == "setup.completed");
    }
}
