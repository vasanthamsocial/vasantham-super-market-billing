using System.Net;
using System.Net.Http.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class AuthorizationTests(ApiFactory factory)
{
    private string Users => $"/api/v1/businesses/{factory.BusinessId}/users";

    [Fact]
    public async Task Cashier_cannot_view_or_manage_users()
    {
        var (cashier, _, _, _) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        using (cashier)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync(Users)).StatusCode);
            var create = await cashier.PostJsonAsync(Users, new CreateUserRequest("sneaky1", "Sneaky", "Some-Password-123", "cashier", factory.MainStoreId));
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/businesses/{factory.BusinessId}/audit")).StatusCode);
        }
    }

    [Fact]
    public async Task Other_businesses_are_invisible_not_just_forbidden()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var created = await owner.PostJsonAsync("/api/v1/businesses", new CreateBusinessRequest(UniqueCode("ISO"), "Isolated Traders", null, "29", null, null));
        await created.EnsureSuccessWithBodyAsync();
        var other = (await created.Content.ReadFromJsonAsync<BusinessDto>(TestClient.Json))!;

        var (manager, _, _, _) = await factory.CreateSignedInUserAsync("manager");
        using (manager)
        {
            var visible = await manager.GetJsonAsync<List<BusinessDto>>("/api/v1/businesses");
            Assert.DoesNotContain(visible, b => b.Id == other.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/businesses/{other.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/businesses/{other.Id}/users")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/businesses/{other.Id}/stores")).StatusCode);
            var intrude = await manager.PostJsonAsync($"/api/v1/businesses/{other.Id}/stores", new CreateStoreRequest("X1", "Intruder", "29", null, null));
            Assert.Equal(HttpStatusCode.NotFound, intrude.StatusCode);
        }
    }

    [Fact]
    public async Task Store_limited_manager_works_only_within_their_store()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var otherStore = await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stores", new CreateStoreRequest(UniqueCode("S"), "Second Store", "33", null, null));
        await otherStore.EnsureSuccessWithBodyAsync();
        var secondStoreId = (await otherStore.Content.ReadFromJsonAsync<StoreDto>(TestClient.Json))!.Id;

        var (storeManager, _, _, _) = await factory.CreateSignedInUserAsync("manager", factory.MainStoreId);
        using (storeManager)
        {
            var stores = await storeManager.GetJsonAsync<List<StoreDto>>($"/api/v1/businesses/{factory.BusinessId}/stores");
            Assert.Equal([factory.MainStoreId], stores.Select(s => s.Id));

            var ownStore = await storeManager.PostJsonAsync(Users, new CreateUserRequest($"c{Guid.NewGuid():N}"[..16], "Own Store Cashier", "Some-Password-123", "cashier", factory.MainStoreId));
            Assert.Equal(HttpStatusCode.Created, ownStore.StatusCode);

            var otherStoreUser = await storeManager.PostJsonAsync(Users, new CreateUserRequest($"c{Guid.NewGuid():N}"[..16], "Other Store", "Some-Password-123", "cashier", secondStoreId));
            Assert.Equal(HttpStatusCode.Forbidden, otherStoreUser.StatusCode);

            var businessWide = await storeManager.PostJsonAsync(Users, new CreateUserRequest($"c{Guid.NewGuid():N}"[..16], "Everywhere", "Some-Password-123", "cashier", null));
            Assert.Equal(HttpStatusCode.Forbidden, businessWide.StatusCode);

            var newStore = await storeManager.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stores", new CreateStoreRequest(UniqueCode("T"), "Not allowed", "33", null, null));
            Assert.Equal(HttpStatusCode.Forbidden, newStore.StatusCode);
        }
    }

    [Fact]
    public async Task Nobody_can_escalate_beyond_their_own_permissions()
    {
        var (manager, managerId, _, _) = await factory.CreateSignedInUserAsync("manager");
        using (manager)
        {
            var (target, targetId, _, _) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
            target.Dispose();

            var grantOwner = await manager.PostJsonAsync($"{Users}/{targetId}/roles", new GrantRoleRequest("owner", null, "please"));
            Assert.Equal(HttpStatusCode.Forbidden, grantOwner.StatusCode);

            var grantSupport = await manager.PostJsonAsync($"{Users}/{targetId}/roles", new GrantRoleRequest("support_admin", null, null));
            Assert.Equal(HttpStatusCode.Forbidden, grantSupport.StatusCode);

            var selfGrant = await manager.PostJsonAsync($"{Users}/{managerId}/roles", new GrantRoleRequest("accountant", null, null));
            Assert.Equal(HttpStatusCode.Forbidden, selfGrant.StatusCode);

            var owners = await manager.GetJsonAsync<List<UserDto>>(Users);
            var ownerId = owners.Single(u => u.Username == ApiFactory.OwnerUsername).Id;
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostJsonAsync($"{Users}/{ownerId}/password-reset", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutJsonAsync($"{Users}/{ownerId}/active", new SetUserActiveRequest(false))).StatusCode);
        }

        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var me = await owner.GetJsonAsync<MeResponse>("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutJsonAsync($"{Users}/{me.UserId}/active", new SetUserActiveRequest(false))).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_sessions_immediately()
    {
        var (cashier, cashierId, username, password) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        (await owner.PutJsonAsync($"{Users}/{cashierId}/active", new SetUserActiveRequest(false))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("/api/v1/auth/me")).StatusCode);
        using var retry = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await retry.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, password))).StatusCode);
        cashier.Dispose();
    }

    [Fact]
    public async Task Duplicate_store_codes_are_rejected_with_a_readable_conflict()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        var response = await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stores", new CreateStoreRequest("MAIN", "Duplicate", "33", null, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already has a store with this code", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static string UniqueCode(string prefix) => (prefix + Guid.NewGuid().ToString("N")[..8]).ToUpperInvariant()[..Math.Min(12, prefix.Length + 8)];
}
