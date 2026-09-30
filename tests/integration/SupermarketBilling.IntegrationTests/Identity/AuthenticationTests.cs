using System.Net;
using System.Net.Http.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class AuthenticationTests(ApiFactory factory)
{
    [Fact]
    public async Task Login_sets_httponly_strict_session_cookie_and_returns_no_tokens_in_the_body()
    {
        using var client = factory.CreateBrowserClient();

        var response = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword));
        await response.EnsureSuccessWithBodyAsync();
        var body = await response.Content.ReadAsStringAsync();

        var sessionCookie = Assert.Single(client.Cookies.SetCookieHeaders, h => h.StartsWith("sb_session=", StringComparison.Ordinal));
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);

        var csrfCookie = Assert.Single(client.Cookies.SetCookieHeaders, h => h.StartsWith("sb_csrf=", StringComparison.Ordinal));
        Assert.DoesNotContain("httponly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", csrfCookie, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(client.Cookies.Get("sb_session")!, body, StringComparison.Ordinal);
        Assert.DoesNotContain(client.Cookies.Get("sb_csrf")!, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_get_the_same_answer()
    {
        using var client = factory.CreateBrowserClient();

        var unknown = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest("nobody-here", "Some-Password-123"));
        var wrong = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(ApiFactory.OwnerUsername, "Wrong-Password-123"));

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await unknown.ProblemCodeAsync(), await wrong.ProblemCodeAsync());
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_and_unlocks_after_the_lockout_period()
    {
        var (client, _, username, password) = await factory.CreateSignedInUserAsync("cashier");
        client.Dispose();
        using var anon = factory.CreateBrowserClient();

        for (var i = 0; i < 5; i++)
        {
            await anon.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, "Definitely-Wrong-1"));
        }

        var whileLocked = await anon.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, password));
        Assert.Equal(HttpStatusCode.Locked, whileLocked.StatusCode);
        Assert.Equal("account_locked", await whileLocked.ProblemCodeAsync());

        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        var afterLockout = await anon.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, password));
        Assert.Equal(HttpStatusCode.OK, afterLockout.StatusCode);
    }

    [Fact]
    public async Task Parallel_guessing_cannot_bypass_the_lockout()
    {
        var (client, _, username, password) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        client.Dispose();

        // Twelve wrong passwords at the same moment: none may be lost to a concurrency conflict.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var attacker = factory.CreateBrowserClient();
            return (await attacker.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, $"Guess-number-{i:D3}"))).StatusCode;
        }));

        Assert.All(attempts, status => Assert.Contains(status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Locked }));
        using var owner = factory.CreateBrowserClient();
        var correct = await owner.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, password));
        Assert.Equal(HttpStatusCode.Locked, correct.StatusCode);
    }

    [Fact]
    public async Task Simultaneous_correct_sign_ins_all_succeed()
    {
        var (client, _, username, password) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        client.Dispose();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var counter = factory.CreateBrowserClient();
            return (await counter.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, password))).StatusCode;
        }));

        Assert.All(attempts, status => Assert.Equal(HttpStatusCode.OK, status));
    }

    [Fact]
    public async Task Api_requires_sign_in_by_default()
    {
        using var anonymous = factory.CreateBrowserClient();

        var response = await anonymous.GetAsync("/api/v1/businesses");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("not_signed_in", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task State_changing_requests_without_the_csrf_header_are_rejected()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var store = new CreateStoreRequest($"C{Random.Shared.Next(1000, 9999)}", "CSRF test store", "33", null, null);

        owner.Cookies.SendCsrfHeader = false;
        var forged = await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stores", store);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal("csrf", await forged.ProblemCodeAsync());

        owner.Http.DefaultRequestHeaders.Add("X-CSRF-Token", "not-the-real-token");
        var wrongToken = await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stores", store);
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);
        owner.Http.DefaultRequestHeaders.Remove("X-CSRF-Token");

        owner.Cookies.SendCsrfHeader = true;
        var genuine = await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/stores", store);
        Assert.Equal(HttpStatusCode.Created, genuine.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session_on_the_server()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var stolenCookie = owner.Cookies.Get("sb_session")!;

        (await owner.PostJsonAsync("/api/v1/auth/logout", new { })).EnsureSuccessStatusCode();

        using var attacker = factory.CreateBrowserClient();
        attacker.Http.DefaultRequestHeaders.Add("Cookie", $"sb_session={stolenCookie}");
        var reuse = await attacker.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Idle_sessions_expire()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/auth/me")).StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task New_users_must_change_their_temporary_password_before_using_the_system()
    {
        var username = $"n{Guid.NewGuid():N}"[..20];
        using (var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword))
        {
            (await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/users",
                new CreateUserRequest(username, "New Cashier", "Temporary-Pass-001", "cashier", factory.MainStoreId))).EnsureSuccessStatusCode();
        }

        using var user = await factory.LoginAsync(username, "Temporary-Pass-001");
        var me = await user.GetJsonAsync<MeResponse>("/api/v1/auth/me");
        Assert.Equal(SessionStates.PasswordChangeRequired, me.SessionState);
        var blocked = await user.GetAsync("/api/v1/businesses");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal(SessionStates.PasswordChangeRequired, await blocked.ProblemCodeAsync());

        var weak = await user.PostJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest("Temporary-Pass-001", "short"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var changed = await user.PostJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest("Temporary-Pass-001", "My-Own-Password-9"));
        var after = await changed.Content.ReadFromJsonAsync<MeResponse>(TestClient.Json);
        Assert.Equal(SessionStates.Active, after!.SessionState);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/businesses")).StatusCode);
    }

    [Fact]
    public async Task Manager_issued_reset_code_works_once_and_signs_out_existing_sessions()
    {
        var (user, userId, username, oldPassword) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);

        var issued = await owner.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/users/{userId}/password-reset", new { });
        await issued.EnsureSuccessWithBodyAsync();
        var reset = (await issued.Content.ReadFromJsonAsync<PasswordResetIssuedResponse>(TestClient.Json))!;
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", reset.ResetCode);

        using var anon = factory.CreateBrowserClient();
        var used = await anon.PostJsonAsync("/api/v1/auth/password/reset", new ResetPasswordRequest(username, reset.ResetCode.ToLowerInvariant(), "Brand-New-Password-7"));
        Assert.Equal(HttpStatusCode.NoContent, used.StatusCode);

        var reused = await anon.PostJsonAsync("/api/v1/auth/password/reset", new ResetPasswordRequest(username, reset.ResetCode, "Another-Password-8"));
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, oldPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, "Brand-New-Password-7"))).StatusCode);

        // The code never appears in any log, and it is not stored anywhere readable.
        Assert.DoesNotContain(factory.LogMessages, m => m.Contains(reset.ResetCode, StringComparison.OrdinalIgnoreCase));
        user.Dispose();
    }

    [Fact]
    public async Task Passwords_and_session_tokens_never_appear_in_logs()
    {
        var wrongPassword = $"Wrong-{Guid.NewGuid():N}";
        using var anon = factory.CreateBrowserClient();
        await anon.PostJsonAsync("/api/v1/auth/login", new LoginRequest(ApiFactory.OwnerUsername, wrongPassword));
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        await owner.GetAsync("/api/v1/auth/me");

        var session = owner.Cookies.Get("sb_session")!;
        var csrf = owner.Cookies.Get("sb_csrf")!;
        foreach (var secret in new[] { ApiFactory.OwnerPassword, wrongPassword, session, csrf })
        {
            Assert.DoesNotContain(factory.LogMessages, m => m.Contains(secret, StringComparison.Ordinal));
        }
    }
}
