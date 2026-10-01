using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SupermarketBilling.CounterAgent;

/// <summary>
/// Counter agent entry point. Settings come from counter-agent.json in %LOCALAPPDATA%\SupermarketBilling (or the
/// path in SB_COUNTER_AGENT_CONFIG). On first run the file is created with a random pairing token and every device
/// switched off; the token is never logged, only where to find it.
/// </summary>
public static class AgentProgram
{
    public static async Task Main(string[] args)
    {
        var configPath = Environment.GetEnvironmentVariable("SB_COUNTER_AGENT_CONFIG")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupermarketBilling", "counter-agent.json");
        var created = EnsureConfigFile(configPath);
        var app = Build(args, builder => builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: false));
        var options = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
        AgentLog.Settings(app.Logger, configPath, created ? " (new: copy the pairing token into the POS hardware settings)" : string.Empty);
        var origins = string.Join(", ", options.AllowedOrigins);
        AgentLog.Listening(app.Logger, options.Url, origins);
        await app.RunAsync(options.Url).ConfigureAwait(false);
    }

    /// <summary>Builds the agent (also used by tests, which supply their own settings).</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = WebApplication.CreateSlimBuilder(args);
        configure(builder);
        builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var url) || !url.IsLoopback)
        {
            throw new InvalidOperationException("The counter agent must listen on 127.0.0.1 (loopback) only.");
        }

        if (options.PairingToken.Length < 32)
        {
            throw new InvalidOperationException("The pairing token is missing or too short (at least 32 characters).");
        }

        app.Use(AgentSecurity.Middleware);
        AgentEndpoints.Map(app);
        return app;
    }

    private static bool EnsureConfigFile(string path)
    {
        if (File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new JsonObject
        {
            [AgentOptions.Section] = new JsonObject
            {
                ["Url"] = "http://127.0.0.1:47800",
                ["AllowedOrigins"] = new JsonArray("http://localhost:3000"),
                ["PairingToken"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
                ["Printer"] = new JsonObject { ["Transport"] = "None", ["Name"] = "", ["Columns"] = 48 },
                ["DrawerEnabled"] = false,
                ["Scale"] = new JsonObject { ["Transport"] = "None", ["SerialPort"] = "COM3", ["BaudRate"] = 9600 },
                ["Display"] = new JsonObject { ["Transport"] = "None", ["SerialPort"] = "COM4", ["BaudRate"] = 9600, ["Columns"] = 20 },
            },
        };
        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return true;
    }
}

internal static partial class AgentLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Counter agent settings: {Path}{Created}")]
    public static partial void Settings(ILogger logger, string path, string created);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening on {Url} for {Origins}")]
    public static partial void Listening(ILogger logger, string url, string origins);
}

/// <summary>
/// Only the configured billing pages may use the agent: requests must come from an allowed origin (browsers send it
/// and pages cannot forge it) and carry the pairing token. Chrome's private-network preflight is answered for
/// allowed origins only.
/// </summary>
internal static class AgentSecurity
{
    public const string TokenHeader = "X-Agent-Token";

    public static async Task Middleware(HttpContext context, Func<Task> next)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<AgentOptions>>().Value;
        var origin = context.Request.Headers.Origin.ToString();
        var allowed = origin.Length > 0 && options.AllowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This page is not allowed to use the counter agent." }).ConfigureAwait(false);
            return;
        }

        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.Vary = "Origin";
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.Headers.AccessControlAllowMethods = "GET, POST";
            context.Response.Headers.AccessControlAllowHeaders = $"Content-Type, {TokenHeader}";
            context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            context.Response.Headers.AccessControlMaxAge = "600";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var token = context.Request.Headers[TokenHeader].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(options.PairingToken)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Wrong pairing token. Copy it from counter-agent.json into the POS hardware settings." }).ConfigureAwait(false);
            return;
        }

        try
        {
            await next().ConfigureAwait(false);
        }
        catch (DeviceException e)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = e.Message }).ConfigureAwait(false);
        }
    }
}

internal static class AgentEndpoints
{
    public sealed record DisplayRequest(string Line1, string Line2);

    public static void Map(WebApplication app)
    {
        app.MapGet("/status", (IOptions<AgentOptions> o) => new
        {
            version = typeof(AgentEndpoints).Assembly.GetName().Version?.ToString(),
            printer = o.Value.Printer.Transport.ToString(),
            drawer = o.Value.DrawerEnabled,
            scale = o.Value.Scale.Transport.ToString(),
            display = o.Value.Display.Transport.ToString(),
        });

        app.MapPost("/receipt", async (PrintReceiptRequest request, IOptions<AgentOptions> o, CancellationToken ct) =>
        {
            var options = o.Value;
            await ByteSinks.For(options.Printer, "receipt printer")
                .SendAsync(ReceiptFormatter.Format(request.Invoice, options.Printer.Columns, request.OpenDrawer && options.DrawerEnabled), ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        app.MapPost("/drawer/open", async (IOptions<AgentOptions> o, CancellationToken ct) =>
        {
            if (!o.Value.DrawerEnabled)
            {
                throw new DeviceException("No cash drawer is set up on this counter.");
            }

            await ByteSinks.For(o.Value.Printer, "receipt printer (drawer port)").SendAsync(new EscPos().OpenDrawer().ToArray(), ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        app.MapGet("/scale/weight", async (IOptions<AgentOptions> o, CancellationToken ct) =>
            await ScaleReader.ReadAsync(o.Value.Scale, ct).ConfigureAwait(false));

        app.MapPost("/display", async (DisplayRequest request, IOptions<AgentOptions> o, CancellationToken ct) =>
        {
            await ByteSinks.For(o.Value.Display, "customer display")
                .SendAsync(CustomerDisplay.Show(request.Line1, request.Line2, o.Value.Display.Columns), ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }
}
