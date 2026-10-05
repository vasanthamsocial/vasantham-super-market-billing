using System.Net.Http.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Reporting;

/// <summary>
/// One GST-registered business shared by the report tests (the test installation is licensed for a few businesses
/// only). Each test class works in stores, items and suppliers of its own, so their figures do not mix where they are
/// checked; business-wide checks compare reports with each other rather than with fixed totals. Its first manager
/// approves the other privileged staff the tests need.
/// </summary>
internal static class ReportBusiness
{
    private static readonly SemaphoreSlim Once = new(1, 1);
    private static (Guid Business, string Manager, string Password)? created;

    public static async Task<(Guid Business, string Manager, string Password)> EnsureAsync(ApiFactory factory)
    {
        await Once.WaitAsync();
        try
        {
            if (created is { } done)
            {
                return done;
            }

            using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
            var (business, _) = await GstBillingTests.BusinessAsync(owner, $"RPT{Random.Shared.Next(100, 999)}", "GST_REGULAR",
                SupermarketBilling.Domain.Tax.Gstin.Complete($"33AAACR{Random.Shared.Next(1000, 9999)}R1Z"));
            var manager = await factory.CreateSignedInUserAsync("manager", businessId: business);
            manager.Client.Dispose();
            created = (business, manager.Username, manager.Password);
            return created.Value;
        }
        finally
        {
            Once.Release();
        }
    }

    /// <summary>The store with this code, created on first use (a retried set-up finds it again).</summary>
    public static async Task<Guid> StoreAsync(ApiFactory factory, Guid business, string code, string name)
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var existing = (await owner.GetJsonAsync<List<StoreDto>>($"/api/v1/businesses/{business}/stores")).FirstOrDefault(s => s.Code == code);
        if (existing is not null)
        {
            return existing.Id;
        }

        var response = await owner.PostJsonAsync($"/api/v1/businesses/{business}/stores", new CreateStoreRequest(code, name, "33", null, null));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<StoreDto>(TestClient.Json))!.Id;
    }

    /// <summary>A signed-in user of the business; a privileged role is approved by its first manager (someone other than the owner).</summary>
    public static async Task<(TestClient Client, string Username, string Password)> UserAsync(ApiFactory factory, string role, Guid? storeId = null)
    {
        var (business, manager, managerPassword) = await EnsureAsync(factory);
        var username = $"r{Guid.NewGuid():N}"[..20];
        ApiFactory.CreateUserResponseDto body;
        using (var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword))
        {
            var response = await owner.PostJsonAsync($"/api/v1/businesses/{business}/users", new CreateUserRequest(username, "Test " + role, "Temporary-Pass-001", role, storeId));
            await response.EnsureSuccessWithBodyAsync();
            body = (await response.Content.ReadFromJsonAsync<ApiFactory.CreateUserResponseDto>(TestClient.Json))!;
        }

        if (body.Role.Outcome == "pending_approval")
        {
            using var approver = await factory.LoginAsync(manager, managerPassword);
            await (await approver.PostJsonAsync($"/api/v1/approvals/{body.Role.ApprovalRequestId}/approve", new ApprovalDecisionRequest("report test"))).EnsureSuccessWithBodyAsync();
        }

        var client = await factory.LoginAsync(username, "Temporary-Pass-001");
        await (await client.PostJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest("Temporary-Pass-001", "Report-User-Password-9"))).EnsureSuccessWithBodyAsync();
        return (client, username, "Report-User-Password-9");
    }
}
