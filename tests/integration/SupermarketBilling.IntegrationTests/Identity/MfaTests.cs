using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Security;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class MfaTests(ApiFactory factory)
{
    [Fact]
    public async Task Mfa_enrolment_then_sign_in_requires_a_valid_unreplayable_code()
    {
        var (client, _, username, password) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        byte[] secret;
        List<string> recoveryCodes;
        using (client)
        {
            var setup = (await (await client.PostJsonAsync("/api/v1/auth/mfa/setup", new { })).Content.ReadFromJsonAsync<MfaSetupResponse>(TestClient.Json))!;
            Assert.StartsWith("otpauth://totp/SupermarketBilling:", setup.OtpAuthUri, StringComparison.Ordinal);
            secret = Totp.Base32Decode(setup.Secret);

            var wrong = await client.PostJsonAsync("/api/v1/auth/mfa/confirm", new MfaCodeRequest("000000"));
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

            var confirm = await client.PostJsonAsync("/api/v1/auth/mfa/confirm", new MfaCodeRequest(Totp.Compute(secret, Totp.StepAt(factory.Clock.GetUtcNow()))));
            await confirm.EnsureSuccessWithBodyAsync();
            recoveryCodes = [.. (await confirm.Content.ReadFromJsonAsync<MfaConfirmResponse>(TestClient.Json))!.RecoveryCodes];
            Assert.Equal(10, recoveryCodes.Count);
        }

        // The secret is stored encrypted, never as the Base32 value the user scanned.
        await using (var db = new NpgsqlConnection(factory.AppConnectionString))
        {
            await db.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT mfa_secret_protected FROM users WHERE username = @u", db);
            command.Parameters.AddWithValue("u", username);
            var stored = (string)(await command.ExecuteScalarAsync())!;
            Assert.StartsWith("v1.", stored, StringComparison.Ordinal);
            Assert.DoesNotContain(Totp.Base32Encode(secret), stored, StringComparison.Ordinal);
        }

        factory.Clock.Advance(TimeSpan.FromSeconds(60));
        using var second = await factory.LoginAsync(username, password);
        Assert.Equal(SessionStates.MfaRequired, (await second.GetJsonAsync<MeResponse>("/api/v1/auth/me")).SessionState);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync("/api/v1/businesses")).StatusCode);

        var bad = await second.PostJsonAsync("/api/v1/auth/mfa/verify", new MfaCodeRequest("123456"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var code = Totp.Compute(secret, Totp.StepAt(factory.Clock.GetUtcNow()));
        var good = await second.PostJsonAsync("/api/v1/auth/mfa/verify", new MfaCodeRequest(code));
        await good.EnsureSuccessWithBodyAsync();
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/businesses")).StatusCode);

        // The same code cannot be used again, even in a new session within its 30 seconds.
        using var replay = await factory.LoginAsync(username, password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.PostJsonAsync("/api/v1/auth/mfa/verify", new MfaCodeRequest(code))).StatusCode);

        // A recovery code works exactly once.
        var recovery = await replay.PostJsonAsync("/api/v1/auth/mfa/verify", new MfaCodeRequest(recoveryCodes[0].ToLowerInvariant()));
        await recovery.EnsureSuccessWithBodyAsync();
        using var third = await factory.LoginAsync(username, password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await third.PostJsonAsync("/api/v1/auth/mfa/verify", new MfaCodeRequest(recoveryCodes[0]))).StatusCode);

        foreach (var secretValue in recoveryCodes.Append(Totp.Base32Encode(secret)))
        {
            Assert.DoesNotContain(factory.LogMessages, m => m.Contains(secretValue, StringComparison.Ordinal));
        }

        // Lost phone: a manager resets two-step verification, the user is signed out and signs in with the password alone.
        var me = await third.GetJsonAsync<MeResponse>("/api/v1/auth/me");
        using (var manager = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword))
        {
            var reset = await manager.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/users/{me.UserId}/mfa-reset", new { });
            await reset.EnsureSuccessWithBodyAsync();
            Assert.False((await reset.Content.ReadFromJsonAsync<UserDto>(TestClient.Json))!.MfaEnabled);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/auth/me")).StatusCode);
        using var afterReset = await factory.LoginAsync(username, password);
        Assert.Equal(SessionStates.Active, (await afterReset.GetJsonAsync<MeResponse>("/api/v1/auth/me")).SessionState);
    }

    [Fact]
    public async Task Managers_cannot_reset_mfa_of_people_above_them()
    {
        using var manager = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        var users = await manager.GetJsonAsync<List<UserDto>>($"/api/v1/businesses/{factory.BusinessId}/users");
        var ownerId = users.Single(u => u.Username == ApiFactory.OwnerUsername).Id;

        var response = await manager.PostJsonAsync($"/api/v1/businesses/{factory.BusinessId}/users/{ownerId}/mfa-reset", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
