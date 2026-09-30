using System.Security.Claims;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Api.Security;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private HttpContext Context => accessor.HttpContext ?? throw new InvalidOperationException("No HTTP request in progress.");

    public bool IsAuthenticated => accessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    public Guid UserId => Guid.Parse(Claim(ClaimTypes.NameIdentifier));

    public Guid SessionId => Guid.Parse(Claim(SessionAuthentication.SessionIdClaim));

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string CorrelationId => accessor.HttpContext?.TraceIdentifier ?? "background";

    private string Claim(string type) =>
        Context.User.FindFirst(type)?.Value ?? throw new InvalidOperationException("The request is not authenticated.");
}

/// <summary>Writes and clears the session and CSRF cookies.</summary>
internal static class AuthCookies
{
    public static void Write(HttpResponse response, LoginOutcome outcome, SecurityOptions options)
    {
        // Session cookie: HttpOnly (scripts cannot read it), SameSite=Strict, no Expires (ends with the browser).
        response.Cookies.Append(SessionAuthentication.SessionCookie, outcome.SessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });

        // CSRF cookie: readable by the app's own scripts so they can echo it in the X-CSRF-Token header.
        response.Cookies.Append(SessionAuthentication.CsrfCookie, outcome.CsrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = options.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
    }

    public static void Clear(HttpResponse response, SecurityOptions options)
    {
        foreach (var name in new[] { SessionAuthentication.SessionCookie, SessionAuthentication.CsrfCookie })
        {
            response.Cookies.Delete(name, new CookieOptions { Path = "/", Secure = options.SecureCookies, SameSite = SameSiteMode.Strict });
        }
    }

    public static SecurityOptions Options(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
}
