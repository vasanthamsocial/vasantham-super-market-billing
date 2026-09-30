using System.Net;
using System.Net.Http.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Identity;

/// <summary>Setup validation on an empty installation of its own, so it always runs before any setup.</summary>
public sealed class SetupValidationTests(EmptyApiFactory factory) : IClassFixture<EmptyApiFactory>
{
    [Fact]
    public async Task Setup_rejects_invalid_business_data_without_creating_anything()
    {
        using var client = factory.CreateBrowserClient();
        Assert.True((await client.GetJsonAsync<SetupStatusResponse>("/api/v1/setup/status")).SetupRequired);
        var request = ApiFactory.NewSetupRequest(await File.ReadAllTextAsync(factory.SetupCodeFile)) with
        {
            Business = new CreateBusinessRequest("TEST", "Test", null, "33", "33ABCDE1234F1Z0", null),
        };

        var response = await client.PostJsonAsync("/api/v1/setup", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("gstin.invalid", await response.ProblemCodeAsync());
        Assert.True((await client.GetJsonAsync<SetupStatusResponse>("/api/v1/setup/status")).SetupRequired);
        Assert.True(File.Exists(factory.SetupCodeFile), "A failed setup must not consume the setup code.");
    }
}

/// <summary>Licence limit, on its own installation so other tests cannot use up the allowance first.</summary>
public sealed class LicenceTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Business_count_is_limited_by_the_licence()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        // The factory licenses 3 businesses and setup created 1.
        for (var i = 0; i < 2; i++)
        {
            var created = await owner.PostJsonAsync("/api/v1/businesses", new CreateBusinessRequest($"LIC{i}", "Licence Test", null, "33", null, null));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var overLimit = await owner.PostJsonAsync("/api/v1/businesses", new CreateBusinessRequest("LIC9", "Too Many", null, "33", null, null));

        Assert.Equal(HttpStatusCode.Conflict, overLimit.StatusCode);
        Assert.Equal("license.max_businesses", await overLimit.ProblemCodeAsync());
        Assert.Equal(3, (await owner.GetJsonAsync<List<BusinessDto>>("/api/v1/businesses")).Count);
    }
}

/// <summary>
/// Separation of duties with two owners, on its own installation: an approver may only approve changes within
/// their own permissions, and the right person can.
/// </summary>
public sealed class SeparationOfDutiesTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Approver_cannot_approve_a_grant_beyond_their_own_permissions_but_a_second_owner_can()
    {
        // A second owner: granted with a recorded waiver, because at that moment only owner1 could approve it.
        var (secondOwner, _, _, _) = await factory.CreateSignedInUserAsync("owner");
        var (target, targetId, _, _) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        target.Dispose();
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        var requested = await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/users/{targetId}/roles", new GrantRoleRequest("support_admin", null, "IT support"));
        var outcome = (await requested.Content.ReadFromJsonAsync<GrantRoleResponse>(TestClient.Json))!;
        Assert.Equal("pending_approval", outcome.Outcome);

        // The manager-approver lacks system.diagnostics, which the support role grants.
        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        var queue = await approver.GetJsonAsync<List<ApprovalDto>>($"/api/v1/businesses/{factory.BusinessId}/approvals?status=pending");
        Assert.False(queue.Single(a => a.Id == outcome.ApprovalRequestId).CanDecide);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await approver.PostJsonAsync($"/api/v1/approvals/{outcome.ApprovalRequestId}/approve", new ApprovalDecisionRequest("ok"))).StatusCode);

        using (secondOwner)
        {
            (await secondOwner.PostJsonAsync($"/api/v1/approvals/{outcome.ApprovalRequestId}/approve", new ApprovalDecisionRequest("Approved by second owner")))
                .EnsureSuccessStatusCode();
        }

        var users = await owner.GetJsonAsync<List<UserDto>>($"/api/v1/businesses/{factory.BusinessId}/users");
        Assert.Contains(users.Single(u => u.Id == targetId).Roles, r => r.RoleCode == "support_admin");

        var audit = await owner.GetJsonAsync<List<AuditEventDto>>($"/api/v1/businesses/{factory.BusinessId}/audit?limit=200");
        Assert.Contains(audit, e => e.EventType == "role.granted" && e.PayloadJson.Contains("waived_no_other_approver", StringComparison.Ordinal));
    }
}
