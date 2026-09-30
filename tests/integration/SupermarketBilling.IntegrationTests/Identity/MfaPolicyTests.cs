using System.Net;
using System.Net.Http.Json;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Security;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Identity;

/// <summary>Runs on its own installation, because switching the policy on affects the owner account.</summary>
public sealed class MfaPolicyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Business_can_require_mfa_for_privileged_users_but_not_for_others()
    {
        var (cashier, _, _, _) = await factory.CreateSignedInUserAsync("cashier", factory.MainStoreId);
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var business = await owner.GetJsonAsync<BusinessDto>($"/api/v1/businesses/{factory.BusinessId}");

        (await owner.PutJsonAsync($"/api/v1/businesses/{factory.BusinessId}", new UpdateBusinessRequest(
                business.LegalName, business.TradeName, business.StateCode, business.Gstin, business.Address,
                RequireMfaForPrivilegedUsers: true, business.RowVersion)))
            .EnsureSuccessStatusCode();

        // The owner (privileged) is now held at enrolment; the cashier (not privileged) is unaffected.
        var me = await owner.GetJsonAsync<MeResponse>("/api/v1/auth/me");
        Assert.Equal(SessionStates.MfaEnrolmentRequired, me.SessionState);
        Assert.True(me.MfaRequiredByPolicy);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v1/businesses/{factory.BusinessId}/users")).StatusCode);
        Assert.Equal(SessionStates.Active, (await cashier.GetJsonAsync<MeResponse>("/api/v1/auth/me")).SessionState);

        // Enrolling releases the owner, and MFA can then not be switched off while the policy applies.
        var setup = (await (await owner.PostJsonAsync("/api/v1/auth/mfa/setup", new { })).Content.ReadFromJsonAsync<MfaSetupResponse>(TestClient.Json))!;
        var secret = Totp.Base32Decode(setup.Secret);
        (await owner.PostJsonAsync("/api/v1/auth/mfa/confirm", new MfaCodeRequest(Totp.Compute(secret, Totp.StepAt(factory.Clock.GetUtcNow())))))
            .EnsureSuccessStatusCode();
        Assert.Equal(SessionStates.Active, (await owner.GetJsonAsync<MeResponse>("/api/v1/auth/me")).SessionState);

        factory.Clock.Advance(TimeSpan.FromSeconds(60));
        var disable = await owner.PostJsonAsync("/api/v1/auth/mfa/disable",
            new MfaDisableRequest(ApiFactory.OwnerPassword, Totp.Compute(secret, Totp.StepAt(factory.Clock.GetUtcNow()))));
        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        cashier.Dispose();
    }
}
