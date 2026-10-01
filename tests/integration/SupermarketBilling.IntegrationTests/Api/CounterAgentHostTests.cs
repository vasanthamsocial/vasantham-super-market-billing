using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using SupermarketBilling.CounterAgent;

namespace SupermarketBilling.IntegrationTests.Api;

/// <summary>The counter agent as the billing page uses it: only from the allowed page, only with the pairing token.</summary>
public sealed class CounterAgentHostTests : IAsyncLifetime
{
    private const string Origin = "http://store-server:3000";
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef"; // sb-audit: test-fixture (in-memory test agent only)
    private readonly string _printerFile = Path.Combine(Path.GetTempPath(), $"sb-agent-printer-{Guid.NewGuid():N}.bin");
    private readonly string _displayFile = Path.Combine(Path.GetTempPath(), $"sb-agent-display-{Guid.NewGuid():N}.bin");
    private WebApplication? _app;
    private HttpClient? _client;

    private HttpClient Client => _client!;

    public async Task InitializeAsync()
    {
        _app = AgentProgram.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Url"] = "http://127.0.0.1:47800",
                ["Agent:AllowedOrigins:0"] = Origin,
                ["Agent:PairingToken"] = Token,
                ["Agent:Printer:Transport"] = "File",
                ["Agent:Printer:FilePath"] = _printerFile,
                ["Agent:DrawerEnabled"] = "true",
                ["Agent:Scale:Transport"] = "Simulated",
                ["Agent:Scale:SimulatedWeightKg"] = "0.750",
                ["Agent:Display:Transport"] = "File",
                ["Agent:Display:FilePath"] = _displayFile,
            });
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        File.Delete(_printerFile);
        File.Delete(_displayFile);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body = null, string? origin = Origin, string? token = Token)
    {
        var request = new HttpRequestMessage(method, path);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (token is not null)
        {
            request.Headers.Add("X-Agent-Token", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    [Fact]
    public async Task Prints_the_receipt_opens_the_drawer_reads_the_scale_and_shows_the_total()
    {
        var invoice = new ReceiptInvoice(
            "C1-000001", "INVOICE", "NOT_GST_REGISTERED", DateTimeOffset.UtcNow, "C1", "Priya", "Test Traders", "1 Main Road", null, null, null, false,
            [new ReceiptLine("Marie Biscuits", 2, "PCS", 52m, 55m, 104m, 0, "TAXABLE")], 0, 104m, 0, 0, 0, 0, 0, 104m, [new ReceiptPayment("CASH", 200m, null)], 96m, null);
        var printed = await Client.SendAsync(Request(HttpMethod.Post, "/receipt", new PrintReceiptRequest(invoice, OpenDrawer: true)));
        Assert.Equal(HttpStatusCode.NoContent, printed.StatusCode);
        var bytes = await File.ReadAllBytesAsync(_printerFile);
        Assert.Contains("C1-000001", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x1B, (byte)'p', 0, 25, 250 }, bytes.AsSpan()[^5..].ToArray());

        var weight = await (await Client.SendAsync(Request(HttpMethod.Get, "/scale/weight"))).Content.ReadFromJsonAsync<Weight>();
        Assert.Equal(new Weight(0.750m, true), weight);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.SendAsync(Request(HttpMethod.Post, "/display", new { line1 = "Total", line2 = "Rs. 154.00" }))).StatusCode);
        Assert.Contains("Rs. 154.00", Encoding.Latin1.GetString(await File.ReadAllBytesAsync(_displayFile)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_pages_and_requests_without_the_token_are_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(Request(HttpMethod.Post, "/drawer/open", origin: "http://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(Request(HttpMethod.Post, "/drawer/open", origin: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Request(HttpMethod.Post, "/drawer/open", token: "wrong"))).StatusCode);
        Assert.False(File.Exists(_printerFile)); // nothing reached the printer
    }

    [Fact]
    public async Task The_private_network_preflight_is_answered_for_the_billing_page_only()
    {
        var preflight = Request(HttpMethod.Options, "/receipt", token: null);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Private-Network", "true");
        var answer = await Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal(Origin, answer.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", answer.Headers.GetValues("Access-Control-Allow-Private-Network").Single());

        var foreign = Request(HttpMethod.Options, "/receipt", origin: "http://evil.example", token: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public void The_agent_refuses_to_listen_beyond_this_computer() =>
        Assert.Throws<InvalidOperationException>(() => AgentProgram.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:Url"] = "http://0.0.0.0:47800", ["Agent:PairingToken"] = Token });
        }));
}
