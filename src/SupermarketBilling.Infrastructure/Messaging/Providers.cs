using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SupermarketBilling.Domain.Messaging;

namespace SupermarketBilling.Infrastructure.Messaging;

/// <summary>Bound from the "Messaging" configuration section. Credentials come from the environment, never source control.</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>Whether the background sender runs (off in tests, which send on demand).</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int DispatchIntervalSeconds { get; set; } = 30;

    public WhatsAppOptions WhatsApp { get; set; } = new();

    public SmsOptions Sms { get; set; } = new();

    public sealed class WhatsAppOptions
    {
        /// <summary>None (not set up), Simulated (development and tests) or Meta (the WhatsApp Business Cloud API).</summary>
        public string Provider { get; set; } = "None";

        public string GraphBaseUrl { get; set; } = "https://graph.facebook.com/v21.0/";

        public string PhoneNumberId { get; set; } = string.Empty;

        public string AccessToken { get; set; } = string.Empty;

        /// <summary>Signs the webhook calls (X-Hub-Signature-256); required to accept delivery reports.</summary>
        public string AppSecret { get; set; } = string.Empty;

        /// <summary>Echoed back when Meta verifies the webhook URL.</summary>
        public string VerifyToken { get; set; } = string.Empty;
    }

    public sealed class SmsOptions
    {
        /// <summary>None or Simulated (an Indian SMS provider with DLT registration is chosen later, O-003).</summary>
        public string Provider { get; set; } = "None";

        public string SenderId { get; set; } = string.Empty;
    }
}

public sealed record MessageAttachment(byte[] Content, string FileName, string MediaType);

/// <summary>What a provider is asked to send.</summary>
public sealed record OutgoingMessage(
    string To, string? TemplateName, string LanguageCode, IReadOnlyList<string> Parameters, string Body, string? DltTemplateId, MessageAttachment? Attachment);

/// <summary>A provider refused or could not be reached. Permanent failures are not retried (for example an invalid number).</summary>
public sealed class MessageProviderException(string message, bool permanent, Exception? inner = null) : Exception(message, inner)
{
    public bool Permanent { get; } = permanent;
}

public interface IMessageProvider
{
    string Channel { get; }

    string Name { get; }

    /// <summary>Sends and returns the provider's message id.</summary>
    Task<string> SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// A stand-in provider for development and tests: records what would be sent and answers with an id. Failures can be
/// scripted to exercise retries.
/// </summary>
public sealed class SimulatedMessaging
{
    private readonly ConcurrentQueue<MessageProviderException> _failures = new();

    public ConcurrentBag<(string Channel, string Id, OutgoingMessage Message)> Sent { get; } = [];

    /// <summary>How long a send takes, as over a real network (tests use it to make concurrent senders overlap).</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    public void FailNext(MessageProviderException failure) => _failures.Enqueue(failure);

    internal async Task<string> SendAsync(string channel, OutgoingMessage message, CancellationToken cancellationToken)
    {
        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        }

        if (_failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        var id = $"sim.{channel.ToLowerInvariant()}.{Guid.NewGuid():N}";
        Sent.Add((channel, id, message));
        return id;
    }
}

internal sealed class SimulatedWhatsAppProvider(SimulatedMessaging simulator) : IMessageProvider
{
    public string Channel => MessageChannels.WhatsApp;

    public string Name => "Simulated";

    public Task<string> SendAsync(OutgoingMessage message, CancellationToken cancellationToken) => simulator.SendAsync(Channel, message, cancellationToken);
}

internal sealed class SimulatedSmsProvider(SimulatedMessaging simulator) : IMessageProvider
{
    public string Channel => MessageChannels.Sms;

    public string Name => "Simulated";

    public Task<string> SendAsync(OutgoingMessage message, CancellationToken cancellationToken) => simulator.SendAsync(Channel, message, cancellationToken);
}

/// <summary>
/// The WhatsApp Business Cloud API (Meta, the official platform; spec section 18). Uploads the PDF as media, then sends
/// the approved template with the document in its header and the values as body parameters.
/// </summary>
internal sealed class MetaWhatsAppProvider(HttpClient http, IOptions<MessagingOptions> options) : IMessageProvider
{
    public const string HttpClientName = "meta-whatsapp";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Channel => MessageChannels.WhatsApp;

    public string Name => "Meta";

    public async Task<string> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value.WhatsApp;
        if (string.IsNullOrEmpty(settings.AccessToken) || string.IsNullOrEmpty(settings.PhoneNumberId))
        {
            throw new MessageProviderException("WhatsApp is not configured (access token and phone number id).", permanent: false);
        }

        if (message.TemplateName is null)
        {
            throw new MessageProviderException("No approved WhatsApp template name is set for this message.", permanent: true);
        }

        string? mediaId = null;
        if (message.Attachment is { } attachment)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("whatsapp"), "messaging_product");
            form.Add(new StringContent(attachment.MediaType), "type");
            var file = new ByteArrayContent(attachment.Content);
            file.Headers.ContentType = new MediaTypeHeaderValue(attachment.MediaType);
            form.Add(file, "file", attachment.FileName);
            mediaId = (await PostAsync<MediaResponse>($"{settings.PhoneNumberId}/media", form, settings.AccessToken, cancellationToken).ConfigureAwait(false)).Id;
        }

        var components = new List<object>();
        if (mediaId is not null)
        {
            components.Add(new { type = "header", parameters = new[] { new { type = "document", document = new { id = mediaId, filename = message.Attachment!.FileName } } } });
        }

        components.Add(new { type = "body", parameters = message.Parameters.Select(p => new { type = "text", text = p }).ToArray() });
        var body = new
        {
            messaging_product = "whatsapp",
            to = message.To.TrimStart('+'),
            type = "template",
            template = new { name = message.TemplateName, language = new { code = message.LanguageCode }, components },
        };
        using var content = JsonContent.Create(body, options: Json);
        var sent = await PostAsync<SendResponse>($"{settings.PhoneNumberId}/messages", content, settings.AccessToken, cancellationToken).ConfigureAwait(false);
        return (sent.Messages is { Count: > 0 } list ? list[0].Id : null) ?? throw new MessageProviderException("WhatsApp answered without a message id.", permanent: false);
    }

    private async Task<T> PostAsync<T>(string path, HttpContent content, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Value.WhatsApp.GraphBaseUrl), path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            // Offline or the provider unreachable: queued for a retry.
            throw new MessageProviderException($"WhatsApp could not be reached: {e.Message}", permanent: false, e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var permanent = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
                throw new MessageProviderException($"WhatsApp refused ({(int)response.StatusCode}): {(text.Length > 300 ? text[..300] : text)}", permanent);
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new MessageProviderException("WhatsApp answered with nothing.", permanent: false);
        }
    }

    private sealed record MediaResponse(string Id);

    private sealed record SendResponse(IReadOnlyList<SentMessage>? Messages);

    private sealed record SentMessage(string Id);
}
