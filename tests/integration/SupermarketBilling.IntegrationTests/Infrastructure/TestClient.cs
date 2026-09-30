using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SupermarketBilling.IntegrationTests.Infrastructure;

/// <summary>HttpClient wrapper with JSON helpers and access to the cookies the "browser" holds.</summary>
public sealed class TestClient(HttpClient client, BrowserCookieHandler cookies) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http => client;

    public BrowserCookieHandler Cookies => cookies;

    public Task<HttpResponseMessage> GetAsync(Uri uri) => client.GetAsync(uri);

    public Task<HttpResponseMessage> GetAsync(string path) => client.GetAsync(new Uri(path, UriKind.Relative));

    public async Task<T> GetJsonAsync<T>(string path)
    {
        var response = await GetAsync(path);
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public Task<HttpResponseMessage> PostJsonAsync<T>(string path, T body) =>
        client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, Json);

    public Task<HttpResponseMessage> PutJsonAsync<T>(string path, T body) =>
        client.PutAsJsonAsync(new Uri(path, UriKind.Relative), body, Json);

    public Task<HttpResponseMessage> DeleteAsync(string path) => client.DeleteAsync(new Uri(path, UriKind.Relative));

    public void Dispose() => client.Dispose();
}

/// <summary>
/// Keeps cookies like a browser and, like the web app's API client, echoes the readable sb_csrf cookie in the
/// X-CSRF-Token header on state-changing requests (unless <see cref="SendCsrfHeader"/> is turned off).
/// </summary>
public sealed class BrowserCookieHandler : DelegatingHandler
{
    public CookieContainer Container { get; } = new();

    public bool SendCsrfHeader { get; set; } = true;

    public List<string> SetCookieHeaders { get; } = [];

    public string? Get(string name) =>
        Container.GetAllCookies().FirstOrDefault(c => c.Name == name && !c.Expired && c.Value.Length > 0)?.Value;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri!;
        var cookieHeader = Container.GetCookieHeader(uri);
        if (cookieHeader.Length > 0)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        if (SendCsrfHeader && request.Method != HttpMethod.Get && Get("sb_csrf") is { } csrf)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                SetCookieHeaders.Add(setCookie);
                Container.SetCookies(uri, setCookie);
            }
        }

        return response;
    }
}

public static class HttpResponseExtensions
{
    /// <summary>Like EnsureSuccessStatusCode, but includes the problem body in the failure message.</summary>
    public static async Task EnsureSuccessWithBodyAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{(int)response.StatusCode} {response.StatusCode}: {body}");
        }
    }

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
