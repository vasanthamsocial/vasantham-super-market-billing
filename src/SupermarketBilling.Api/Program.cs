using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using SupermarketBilling.Api;
using SupermarketBilling.Api.Endpoints;
using SupermarketBilling.Api.Errors;
using SupermarketBilling.Api.Health;
using SupermarketBilling.Api.Security;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Infrastructure;
using SupermarketBilling.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// The web apps forward browser requests to the API. Client addresses are taken from X-Forwarded-For only
// when the request comes from a trusted proxy (loopback by default), so per-client rate limits stay correct.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Every endpoint requires a fully signed-in session unless it explicitly allows otherwise.
builder.Services.AddAuthentication(SessionAuthentication.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthentication.Scheme, null);
builder.Services.AddAuthorization(SessionAuthentication.AddPolicies);

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<SupermarketBillingDbContext>("database", tags: [HealthTags.Ready])
    .AddCheck<PendingMigrationsHealthCheck>("schema", tags: [HealthTags.Ready]);

var permitPerMinute = builder.Configuration.GetValue("RateLimiting:PermitPerMinute", 600);
var authPermitPerMinute = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Sign-in, MFA, reset and setup: a much lower per-client limit against password guessing.
    options.AddPolicy(AuthEndpoints.AuthRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfMiddleware>();

// Liveness: the process is up. No dependencies are checked.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync,
}).AllowAnonymous();

// Readiness: the database is reachable and the schema is current.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthTags.Ready),
    ResponseWriter = HealthResponseWriter.WriteAsync,
}).AllowAnonymous();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Api:ExposeOpenApi"))
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "SupermarketBilling API v1");
        options.RoutePrefix = "swagger";
    });
}

app.MapSystemEndpoints();

// Archive server (D-042): the same program in archive mode, against the archive database, serves only sign-in and the
// archive. It is a licensed feature: without ARCHIVE_WEB_ENABLED=true the archive endpoints do not exist.
var archiveServer = app.Configuration.GetValue<bool>("Archive:Server");
app.MapAuthEndpoints(includeSetup: !archiveServer);
if (archiveServer)
{
    if (app.Configuration.GetValue<bool>("ARCHIVE_WEB_ENABLED"))
    {
        app.MapArchiveServerEndpoints();
    }
}
else
{
    app.MapAdministrationEndpoints();
    app.MapCatalogEndpoints();
    app.MapStockEndpoints();
    app.MapSalesEndpoints();
    app.MapPurchaseEndpoints();
    app.MapAccountsEndpoints();
    app.MapCollectionEndpoints();
    app.MapOfflineEndpoints();
    app.MapOfflineBillingEndpoints();
    app.MapMonthCloseEndpoints();
    app.MapMessagingEndpoints();
    app.MapDispatchEndpoints();
    app.MapReportEndpoints();
}

var environmentName = app.Environment.EnvironmentName;
app.Lifetime.ApplicationStarted.Register(() =>
    StartupLog.Started(app.Logger, SystemEndpoints.ApplicationVersion, environmentName));

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
