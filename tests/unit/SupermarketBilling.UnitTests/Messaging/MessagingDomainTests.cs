using System.Security.Cryptography;
using System.Text;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Messaging;
using SupermarketBilling.Infrastructure.Messaging;

namespace SupermarketBilling.UnitTests.Messaging;

public sealed class MessagingDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 6, 0, 0, TimeSpan.Zero);

    private static OutboundMessage Queued() =>
        OutboundMessage.Queue(Guid.NewGuid(), Guid.NewGuid(), MessageChannels.WhatsApp, MessageKinds.Receipt, Guid.NewGuid(), "MAIN/RCT/000001", "+919800000000", "[]", "text", null, Now);

    [Fact]
    public void Templates_accept_only_the_kinds_placeholders_and_render_them()
    {
        var template = MessageTemplate.Create(Guid.NewGuid(), MessageKinds.Receipt, MessageChannels.Sms, Now);
        Assert.Equal(MessageKinds.DefaultBody(MessageKinds.Receipt), template.Body);
        Assert.Equal("template.placeholder_invalid", Assert.Throws<DomainException>(() => template.Update(null, null, "en", "Due {{due_date}}", true)).Code);
        Assert.Equal("template.name_invalid", Assert.Throws<DomainException>(() => template.Update("Receipt Template", null, "en", "x", true)).Code);
        Assert.Equal("template.dlt_invalid", Assert.Throws<DomainException>(() => template.Update(null, "11071-ABC", "en", "x", true)).Code);
        Assert.Equal("template.body_invalid", Assert.Throws<DomainException>(() => template.Update(null, null, "en", "  ", true)).Code);
        Assert.Equal("message.kind_invalid", Assert.Throws<DomainException>(() => MessageTemplate.Create(Guid.NewGuid(), "PROMOTION", MessageChannels.Sms, Now)).Code);

        template.Update(" receipt_v2 ", " 1107160000000012345 ", "ta", "Rcvd Rs.{{amount}} {{receipt_number}} {{amount}}", true);
        Assert.Equal(("receipt_v2", "1107160000000012345", "ta"), (template.ProviderTemplateName, template.DltTemplateId, template.LanguageCode));
        Assert.Equal("Rcvd Rs.10.00 RCT/1 10.00 {{left}}",
            MessageTemplate.Render(template.Body + " {{left}}", new Dictionary<string, string> { ["amount"] = "10.00", ["receipt_number"] = "RCT/1" }));
    }

    [Fact]
    public void A_failed_attempt_is_retried_with_growing_delays_until_the_last()
    {
        var message = Queued();
        Assert.Equal((MessageStatus.Queued, Now), (message.Status, message.NextAttemptAtUtc));
        var delays = new List<TimeSpan>();
        for (var i = 1; i < OutboundMessage.MaxAttempts; i++)
        {
            message.AttemptFailed("timeout", permanent: false, Now);
            delays.Add(message.NextAttemptAtUtc!.Value - Now);
        }

        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)], delays);
        Assert.Equal(MessageStatus.Queued, message.Status);
        message.AttemptFailed(new string('x', 600), permanent: false, Now);
        Assert.Equal((MessageStatus.Failed, (DateTimeOffset?)null, 500), (message.Status, message.NextAttemptAtUtc, message.LastError!.Length));

        // Fixed by hand, it goes back in the queue with fresh attempts.
        message.Retry(Now);
        Assert.Equal((MessageStatus.Queued, 0, (DateTimeOffset?)Now), (message.Status, message.Attempts, message.NextAttemptAtUtc));

        var refused = Queued();
        refused.AttemptFailed("Template not approved", permanent: true, Now);
        Assert.Equal((MessageStatus.Failed, 1), (refused.Status, refused.Attempts));
    }

    [Fact]
    public void Provider_reports_only_move_a_message_forward_and_an_accepted_message_is_never_resent()
    {
        var message = Queued();
        Assert.False(message.Report(MessageStatus.Delivered, null, Now)); // not sent yet
        message.Sent("wamid.1", "abc", Now);
        Assert.Equal("message.status_invalid", Assert.Throws<DomainException>(() => message.Sent("wamid.2", null, Now)).Code);

        Assert.True(message.Report(MessageStatus.Read, null, Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(2), message.DeliveredAtUtc); // read implies delivered
        Assert.False(message.Report(MessageStatus.Delivered, null, Now.AddMinutes(1))); // late report
        Assert.Equal(MessageStatus.Read, message.Status);
        Assert.True(message.Report(MessageStatus.Failed, "undeliverable", Now.AddMinutes(3)));
        Assert.False(message.Report(MessageStatus.Read, null, Now.AddMinutes(4)));

        Assert.Equal("message.already_sent", Assert.Throws<DomainException>(() => message.Retry(Now)).Code);
        Assert.Equal("message.status_invalid", Assert.Throws<DomainException>(() => Queued().Retry(Now)).Code);
    }

    [Fact]
    public void A_skipped_message_is_never_due_or_sent()
    {
        var skipped = OutboundMessage.Queue(Guid.NewGuid(), Guid.NewGuid(), MessageChannels.Sms, MessageKinds.CreditInvoice, Guid.NewGuid(), "MAIN/INV/000001", null, "[]",
            new string('y', 1200), "No number", Now);
        Assert.Equal((MessageStatus.Skipped, (DateTimeOffset?)null, 1000), (skipped.Status, skipped.NextAttemptAtUtc, skipped.Body.Length));
        Assert.Throws<DomainException>(() => skipped.Sent("x", null, Now));
        Assert.Throws<DomainException>(() => skipped.AttemptFailed("x", false, Now));
        Assert.False(skipped.Report(MessageStatus.Delivered, null, Now));
    }

    [Fact]
    public void Amounts_use_indian_grouping_and_dates_are_day_first()
    {
        Assert.Equal(("12,34,567.50", "0.00"), (MessageFormat.Money(1234567.5m), MessageFormat.Money(0m)));
        Assert.Equal("05-11-2026", MessageFormat.Date(new DateOnly(2026, 11, 5)));
    }

    [Fact]
    public void A_webhook_call_is_accepted_only_with_the_signature_of_its_exact_body()
    {
        var body = Encoding.UTF8.GetBytes("{\"entry\":[]}");
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("app-secret"), body)); // sb-audit: test-fixture
        Assert.True(WhatsAppWebhook.SignatureValid(body, signature, "app-secret"));
        Assert.True(WhatsAppWebhook.SignatureValid(body, signature.ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal), "app-secret"));
        Assert.False(WhatsAppWebhook.SignatureValid(Encoding.UTF8.GetBytes("{\"entry\":[1]}"), signature, "app-secret"));
        Assert.False(WhatsAppWebhook.SignatureValid(body, signature, "other-secret"));
        Assert.False(WhatsAppWebhook.SignatureValid(body, signature, string.Empty)); // no secret configured: nothing is accepted
        Assert.False(WhatsAppWebhook.SignatureValid(body, signature[7..], "app-secret"));
        Assert.False(WhatsAppWebhook.SignatureValid(body, "sha256=zz", "app-secret"));
        Assert.False(WhatsAppWebhook.SignatureValid(body, null, "app-secret"));
    }
}
