using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class MakerCheckerTests(ApiFactory factory)
{
    private string Users => $"/api/v1/businesses/{factory.BusinessId}/users";

    [Fact]
    public async Task Privileged_grant_waits_for_a_different_person_and_applies_on_approval()
    {
        var (target, targetId, _, _) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        target.Dispose();
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        var requested = await owner.PostJsonAsync($"{Users}/{targetId}/roles", new GrantRoleRequest("accountant", null, "Month-end duties"));
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var outcome = (await requested.Content.ReadFromJsonAsync<GrantRoleResponse>(TestClient.Json))!;
        Assert.Equal("pending_approval", outcome.Outcome);

        // Not yet granted.
        var before = await owner.GetJsonAsync<List<UserDto>>(Users);
        Assert.DoesNotContain(before.Single(u => u.Id == targetId).Roles, r => r.RoleCode == "accountant");

        // The requester cannot approve their own request.
        var selfApprove = await owner.PostJsonAsync($"/api/v1/approvals/{outcome.ApprovalRequestId}/approve", new ApprovalDecisionRequest("me"));
        Assert.Equal(HttpStatusCode.Forbidden, selfApprove.StatusCode);

        // A different authorised person can.
        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        var queue = await approver.GetJsonAsync<List<ApprovalDto>>($"/api/v1/businesses/{factory.BusinessId}/approvals?status=pending");
        Assert.True(queue.Single(a => a.Id == outcome.ApprovalRequestId).CanDecide);
        (await approver.PostJsonAsync($"/api/v1/approvals/{outcome.ApprovalRequestId}/approve", new ApprovalDecisionRequest("Checked"))).EnsureSuccessStatusCode();

        var after = await owner.GetJsonAsync<List<UserDto>>(Users);
        Assert.Contains(after.Single(u => u.Id == targetId).Roles, r => r.RoleCode == "accountant");

        var second = await approver.PostJsonAsync($"/api/v1/approvals/{outcome.ApprovalRequestId}/approve", new ApprovalDecisionRequest("again"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        var audit = await owner.GetJsonAsync<List<AuditEventDto>>($"/api/v1/businesses/{factory.BusinessId}/audit?limit=50");
        Assert.Contains(audit, e => e.EventType == "approval.requested" && e.EntityId == outcome.ApprovalRequestId.ToString());
        Assert.Contains(audit, e => e.EventType == "approval.approved" && e.EntityId == outcome.ApprovalRequestId.ToString());
        Assert.Contains(audit, e => e.EventType == "role.granted" && e.PayloadJson.Contains(outcome.ApprovalRequestId.ToString()!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task New_user_whose_role_awaits_approval_is_still_listed_with_the_pending_role()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var username = $"p{Guid.NewGuid():N}"[..16];

        var created = await owner.PostJsonAsync(Users, new CreateUserRequest(username, "Pending Accountant", "Temporary-Pass-001", "accountant", null));
        await created.EnsureSuccessWithBodyAsync();
        var body = (await created.Content.ReadFromJsonAsync<ApiFactory.CreateUserResponseDto>(TestClient.Json))!;
        Assert.Equal("pending_approval", body.Role.Outcome);

        var listed = (await owner.GetJsonAsync<List<UserDto>>(Users)).Single(u => u.Username == username);
        Assert.Empty(listed.Roles);
        Assert.Equal(["Accountant"], listed.PendingRoles);

        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        (await approver.PostJsonAsync($"/api/v1/approvals/{body.Role.ApprovalRequestId}/approve", new ApprovalDecisionRequest(null))).EnsureSuccessStatusCode();

        var approved = (await owner.GetJsonAsync<List<UserDto>>(Users)).Single(u => u.Username == username);
        Assert.Contains(approved.Roles, r => r.RoleCode == "accountant");
        Assert.Empty(approved.PendingRoles);
    }

    [Fact]
    public async Task Rejection_needs_a_reason_and_grants_nothing()
    {
        var (target, targetId, _, _) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        target.Dispose();
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var outcome = (await (await owner.PostJsonAsync($"{Users}/{targetId}/roles", new GrantRoleRequest("auditor", null, null)))
            .Content.ReadFromJsonAsync<GrantRoleResponse>(TestClient.Json))!;
        Assert.Equal("pending_approval", outcome.Outcome);

        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        Assert.Equal(HttpStatusCode.BadRequest, (await approver.PostJsonAsync($"/api/v1/approvals/{outcome.ApprovalRequestId}/reject", new ApprovalDecisionRequest(" "))).StatusCode);
        (await approver.PostJsonAsync($"/api/v1/approvals/{outcome.ApprovalRequestId}/reject", new ApprovalDecisionRequest("Not needed"))).EnsureSuccessStatusCode();

        var users = await owner.GetJsonAsync<List<UserDto>>(Users);
        Assert.DoesNotContain(users.Single(u => u.Id == targetId).Roles, r => r.RoleCode == "auditor");
    }

    [Fact]
    public async Task Database_rejects_self_approval_even_when_the_application_is_bypassed()
    {
        var (target, targetId, _, _) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        target.Dispose();
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var outcome = (await (await owner.PostJsonAsync($"{Users}/{targetId}/roles", new GrantRoleRequest("accountant", null, null)))
            .Content.ReadFromJsonAsync<GrantRoleResponse>(TestClient.Json))!;
        Assert.Equal("pending_approval", outcome.Outcome);

        await using var db = await factory.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE approval_requests SET status = 'approved', decided_by_user_id = requested_by_user_id, decided_at_utc = now() WHERE id = @id", db);
        command.Parameters.AddWithValue("id", outcome.ApprovalRequestId!.Value);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_approval_requests_maker_checker", error.ConstraintName);
    }
}
