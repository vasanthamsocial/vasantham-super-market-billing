using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Messaging;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>WhatsApp and SMS: settings, templates, the message log, and the provider's webhook.</summary>
internal static class MessagingEndpoints
{
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder routes)
    {
        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}").WithTags("Messaging");
        business.MapGet("/messaging/settings", (Guid businessId, MessagingService s, CancellationToken ct) => s.SettingsAsync(businessId, ct));
        business.MapPut("/messaging/settings", (Guid businessId, UpdateMessagingSettingsRequest r, MessagingService s, CancellationToken ct) => s.UpdateSettingsAsync(businessId, r, ct));
        business.MapGet("/messaging/templates", (Guid businessId, MessagingService s, CancellationToken ct) => s.TemplatesAsync(businessId, ct));
        business.MapPut("/messaging/templates/{kind}/{channel}", (Guid businessId, string kind, string channel, UpdateMessageTemplateRequest r, MessagingService s, CancellationToken ct) =>
                s.UpdateTemplateAsync(businessId, kind, channel, r, ct))
            .WithSummary("The approved WhatsApp template name, the DLT template id (SMS) and the text with {{placeholders}}.");
        business.MapGet("/messages", (Guid businessId, string? status, Guid? debtorId, MessagingService s, CancellationToken ct) => s.MessagesAsync(businessId, status, debtorId, ct))
            .WithSummary("Messages to debtors with their delivery state and history.");
        business.MapPost("/messages/{messageId:guid}/retry", async (Guid businessId, Guid messageId, MessagingService s, CancellationToken ct) =>
        {
            await s.RetryAsync(businessId, messageId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // The WhatsApp Business Platform calls these: no session; the signature (or verify token) is the authentication.
        var webhook = routes.MapGroup("/api/v1/messaging/whatsapp/webhook").WithTags("Messaging").AllowAnonymous();
        webhook.MapGet("/", (HttpRequest request, WhatsAppWebhook w) =>
                Results.Text(w.Verify(request.Query["hub.mode"], request.Query["hub.verify_token"], request.Query["hub.challenge"]), "text/plain"))
            .WithSummary("Webhook URL verification by the provider.");
        webhook.MapPost("/", async (HttpRequest request, WhatsAppWebhook w, CancellationToken ct) =>
            {
                using var buffer = new MemoryStream();
                await request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
                await w.HandleAsync(buffer.ToArray(), request.Headers["X-Hub-Signature-256"].ToString(), ct).ConfigureAwait(false);
                return Results.Ok();
            })
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024 * 1024))
            .WithSummary("Delivery reports and replies (STOP opts the debtor out). Requires a valid X-Hub-Signature-256.");
        return routes;
    }
}
