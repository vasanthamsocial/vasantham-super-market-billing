namespace SupermarketBilling.Api.Security;

/// <summary>Adds defensive HTTP response headers to every API response.</summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // The API serves JSON only, so it can forbid all content. Swagger UI (development) needs its own assets.
    internal const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            if (!context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase))
            {
                headers.ContentSecurityPolicy = ApiContentSecurityPolicy;
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
