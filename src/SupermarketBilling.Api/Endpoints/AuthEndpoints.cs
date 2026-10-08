using SupermarketBilling.Api.Security;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Identity;

namespace SupermarketBilling.Api.Endpoints;

internal static class AuthEndpoints
{
    public const string AuthRateLimitPolicy = "auth";

    /// <param name="includeSetup">False on the archive server, which has its own setup.</param>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes, bool includeSetup = true)
    {
        var setup = routes.MapGroup("/api/v1/setup").WithTags("Setup").AllowAnonymous();
        setup.MapGet("/status", (SetupService service, CancellationToken ct) => service.GetStatusAsync(ct))
            .WithSummary("Whether initial setup (first business, store and owner) is still required.");
        if (includeSetup)
        {
            setup.MapPost("/", async (SetupRequest request, SetupService service, CancellationToken ct) =>
                {
                    await service.RunAsync(request, ct).ConfigureAwait(false);
                    return Results.NoContent();
                })
                .RequireRateLimiting(AuthRateLimitPolicy)
                .WithSummary("Creates the first business, store and owner. Requires the one-time setup code from the server.");
        }

        var auth = routes.MapGroup("/api/v1/auth").WithTags("Authentication");

        auth.MapPost("/login", async (LoginRequest request, AuthService service, HttpContext http, CancellationToken ct) =>
            {
                var outcome = await service.LoginAsync(
                    request,
                    new ClientInfo(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString()),
                    ct).ConfigureAwait(false);
                AuthCookies.Write(http.Response, outcome, AuthCookies.Options(http));
                return Results.Ok(outcome.Me);
            })
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Signs in. Sets an HttpOnly session cookie and a CSRF cookie; returns the signed-in user and session state.");

        auth.MapPost("/logout", async (AuthService service, HttpContext http, CancellationToken ct) =>
            {
                await service.LogoutAsync(ct).ConfigureAwait(false);
                AuthCookies.Clear(http.Response, AuthCookies.Options(http));
                return Results.NoContent();
            })
            .RequireAuthorization(SessionAuthentication.AnySessionPolicy);

        auth.MapGet("/me", (AuthService service, CancellationToken ct) => service.GetMeAsync(ct))
            .RequireAuthorization(SessionAuthentication.AnySessionPolicy)
            .WithSummary("The signed-in user, session state, businesses, roles and permissions.");

        auth.MapPost("/mfa/verify", (MfaCodeRequest request, AuthService service, CancellationToken ct) => service.VerifyMfaAsync(request, ct))
            .RequireAuthorization(SessionAuthentication.MfaPendingPolicy)
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Completes sign-in with an authenticator code or a recovery code.");

        auth.MapPost("/mfa/setup", (AuthService service, CancellationToken ct) => service.BeginMfaSetupAsync(ct))
            .RequireAuthorization(SessionAuthentication.CredentialSetupPolicy)
            .WithSummary("Starts MFA enrolment and returns the secret for the authenticator app.");

        auth.MapPost("/mfa/confirm", (MfaCodeRequest request, AuthService service, CancellationToken ct) => service.ConfirmMfaSetupAsync(request, ct))
            .RequireAuthorization(SessionAuthentication.CredentialSetupPolicy)
            .WithSummary("Confirms MFA enrolment with a code; returns one-time recovery codes (shown once).");

        auth.MapPost("/mfa/disable", async (MfaDisableRequest request, AuthService service, CancellationToken ct) =>
            {
                await service.DisableMfaAsync(request, ct).ConfigureAwait(false);
                return Results.NoContent();
            });

        auth.MapPost("/password/change", (ChangePasswordRequest request, AuthService service, CancellationToken ct) => service.ChangePasswordAsync(request, ct))
            .RequireAuthorization(SessionAuthentication.CredentialSetupPolicy)
            .WithSummary("Changes the signed-in user's password and signs out their other sessions.");

        auth.MapPost("/password/reset", async (ResetPasswordRequest request, AuthService service, CancellationToken ct) =>
            {
                await service.ResetPasswordAsync(request, ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Sets a new password using a one-time reset code issued by a manager.");

        auth.MapGet("/sessions", (AuthService service, CancellationToken ct) => service.ListSessionsAsync(ct));
        auth.MapDelete("/sessions/{sessionId:guid}", async (Guid sessionId, AuthService service, CancellationToken ct) =>
        {
            await service.RevokeOwnSessionAsync(sessionId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        return routes;
    }
}
