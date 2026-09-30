using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using SupermarketBilling.Api;
using SupermarketBilling.Api.Endpoints;
using SupermarketBilling.Api.Health;
using SupermarketBilling.Api.Security;
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
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<SupermarketBillingDbContext>("database", tags: [HealthTags.Ready])
    .AddCheck<PendingMigrationsHealthCheck>("schema", tags: [HealthTags.Ready]);

var permitPerMinute = builder.Configuration.GetValue("RateLimiting:PermitPerMinute", 600);
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

// Liveness: the process is up. No dependencies are checked.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

// Readiness: the database is reachable and the schema is current.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthTags.Ready),
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Api:ExposeOpenApi"))
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "SupermarketBilling API v1");
        options.RoutePrefix = "swagger";
    });
}

app.MapSystemEndpoints();

var environmentName = app.Environment.EnvironmentName;
app.Lifetime.ApplicationStarted.Register(() =>
    StartupLog.Started(app.Logger, SystemEndpoints.ApplicationVersion, environmentName));

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;

