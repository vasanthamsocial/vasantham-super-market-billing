using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Identity;

namespace SupermarketBilling.Api.Security;

internal static class SessionAuthentication
{
    public const string Scheme = "sb-session";
    public const string SessionCookie = "sb_session";
    public const string CsrfCookie = "sb_csrf";
    public const string CsrfHeader = "X-CSRF-Token";

    public const string SessionIdClaim = "sb:sid";
    public const string StateClaim = "sb:state";

    /// <summary>Fully signed-in sessions. This is the default and fallback policy for every endpoint.</summary>
    public const string ActivePolicy = "active-session";

    /// <summary>Any valid session, including ones waiting for MFA or a password change (me, logout).</summary>
    public const string AnySessionPolicy = "any-session";

    /// <summary>Sessions waiting for the MFA code.</summary>
    public const string MfaPendingPolicy = "mfa-pending";

    /// <summary>Sessions allowed to set up credentials: active, forced password change, forced MFA enrolment.</summary>
    public const string CredentialSetupPolicy = "credential-setup";

    public static void AddPolicies(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        options.AddPolicy(ActivePolicy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(StateClaim, SessionStates.Active));
        options.AddPolicy(AnySessionPolicy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        options.AddPolicy(MfaPendingPolicy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(StateClaim, SessionStates.MfaRequired));
        options.AddPolicy(CredentialSetupPolicy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()
            .RequireClaim(StateClaim, SessionStates.Active, SessionStates.PasswordChangeRequired, SessionStates.MfaEnrolmentRequired));
        options.DefaultPolicy = options.GetPolicy(ActivePolicy)!;
        options.FallbackPolicy = options.DefaultPolicy;
    }
}

/// <summary>Authenticates requests from the opaque session cookie, validated against the sessions table.</summary>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionAuthentication.SessionCookie, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await sessions.ValidateAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            return AuthenticateResult.Fail("Session is invalid or expired.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
                new Claim(ClaimTypes.Name, session.Username),
                new Claim(SessionAuthentication.SessionIdClaim, session.SessionId.ToString()),
                new Claim(SessionAuthentication.StateClaim, session.State),
            ],
            SessionAuthentication.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SessionAuthentication.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status401Unauthorized, "not_signed_in", "Please sign in.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        var state = Context.User.FindFirst(SessionAuthentication.StateClaim)?.Value;
        return state is null or SessionStates.Active
            ? WriteProblemAsync(StatusCodes.Status403Forbidden, "forbidden", "You do not have permission to do this.")
            : WriteProblemAsync(StatusCodes.Status403Forbidden, state, "Finish signing in first (" + state.Replace('_', ' ') + ").");
    }

    private Task WriteProblemAsync(int status, string code, string detail)
    {
        Response.StatusCode = status;
        return Results.Problem(detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(Context);
    }
}

/// <summary>
/// Anti-forgery: every state-changing request made with a session cookie must echo the CSRF token (from the
/// readable sb_csrf cookie) in the X-CSRF-Token header. A cross-site page cannot read the cookie, so it cannot
/// forge the header. SameSite=Strict cookies are a second layer.
/// </summary>
internal sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SessionService sessions)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method)
            && context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst(SessionAuthentication.SessionIdClaim)?.Value, out var sessionId))
        {
            var header = context.Request.Headers[SessionAuthentication.CsrfHeader].ToString();
            if (!await sessions.ValidateCsrfAsync(sessionId, header, context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await Results.Problem(
                        detail: "The security token is missing or wrong. Reload the page and try again.",
                        statusCode: StatusCodes.Status403Forbidden,
                        extensions: new Dictionary<string, object?> { ["code"] = "csrf" })
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
