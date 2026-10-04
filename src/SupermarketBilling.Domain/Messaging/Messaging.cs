using System.Globalization;
using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Messaging;

public static class MessageChannels
{
    public const string WhatsApp = "WHATSAPP";
    public const string Sms = "SMS";

    public static readonly IReadOnlyList<string> All = [WhatsApp, Sms];
}

/// <summary>What a message is about (spec section 18). Each kind has fixed parameters, in this order.</summary>
public static class MessageKinds
{
    /// <summary>A credit invoice, with its PDF: invoice number, amount, due date, total balance.</summary>
    public const string CreditInvoice = "CREDIT_INVOICE";

    /// <summary>A confirmed collection: receipt number, amount, method, previous balance, current balance.</summary>
    public const string Receipt = "RECEIPT";

    public static readonly IReadOnlyList<string> All = [CreditInvoice, Receipt];

    public static IReadOnlyList<string> Parameters(string kind) => kind switch
    {
        CreditInvoice => ["party", "invoice_number", "amount", "due_date", "balance"],
        Receipt => ["party", "receipt_number", "amount", "method", "previous_balance", "current_balance"],
        _ => throw new DomainException("message.kind_invalid", $"Unknown message kind '{kind}'."),
    };

    public static string DefaultBody(string kind) => kind switch
    {
        CreditInvoice => "Dear {{party}}, invoice {{invoice_number}} for Rs. {{amount}} is due on {{due_date}}. Your total balance is Rs. {{balance}}.",
        Receipt => "Dear {{party}}, we received Rs. {{amount}} by {{method}} (receipt {{receipt_number}}). Previous balance Rs. {{previous_balance}}, now Rs. {{current_balance}}.",
        _ => throw new DomainException("message.kind_invalid", $"Unknown message kind '{kind}'."),
    };
}

public static class MessageStatus
{
    /// <summary>Waiting to be sent (also between retries).</summary>
    public const string Queued = "QUEUED";

    /// <summary>Accepted by the provider.</summary>
    public const string Sent = "SENT";

    public const string Delivered = "DELIVERED";
    public const string Read = "READ";

    /// <summary>Given up after the last retry, or reported failed by the provider.</summary>
    public const string Failed = "FAILED";

    /// <summary>Not sent at all: no consent, no number, or the channel is switched off. Kept for the audit trail.</summary>
    public const string Skipped = "SKIPPED";

    public static readonly IReadOnlyList<string> All = [Queued, Sent, Delivered, Read, Failed, Skipped];

    /// <summary>How far along a message is; provider reports that arrive late (delivered after read) never move it back.</summary>
    public static int Rank(string status) => status switch
    {
        Queued => 0,
        Sent => 1,
        Delivered => 2,
        Read => 3,
        _ => 4,
    };
}

/// <summary>
/// The wording of a message for a business, channel and kind: the template name the provider approved (WhatsApp), the
/// DLT template id (SMS in India), and the text with {{placeholders}} (sent as SMS text, kept for the audit trail).
/// </summary>
public sealed partial class MessageTemplate : ITenantOwned
{
    private MessageTemplate()
    {
        Kind = Channel = Body = LanguageCode = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Kind { get; private set; }

    public string Channel { get; private set; }

    public string? ProviderTemplateName { get; private set; }

    public string? DltTemplateId { get; private set; }

    public string LanguageCode { get; private set; }

    public string Body { get; private set; }

    public bool IsActive { get; private set; }

    public uint RowVersion { get; private set; }

    public static MessageTemplate Create(Guid businessId, string kind, string channel, DateTimeOffset now)
    {
        var template = new MessageTemplate { Id = Guid.CreateVersion7(now), BusinessId = businessId };
        template.Kind = MessageKinds.All.Contains(kind) ? kind : throw new DomainException("message.kind_invalid", $"Unknown message kind '{kind}'.");
        template.Channel = MessageChannels.All.Contains(channel) ? channel : throw new DomainException("message.channel_invalid", $"Unknown channel '{channel}'.");
        template.Update(null, null, "en", MessageKinds.DefaultBody(kind), true);
        return template;
    }

    public void Update(string? providerTemplateName, string? dltTemplateId, string languageCode, string body, bool isActive)
    {
        ProviderTemplateName = string.IsNullOrWhiteSpace(providerTemplateName) ? null
            : TemplateName().IsMatch(providerTemplateName.Trim()) ? providerTemplateName.Trim()
            : throw new DomainException("template.name_invalid", "A WhatsApp template name is lowercase letters, digits and underscores (max 100).");
        DltTemplateId = string.IsNullOrWhiteSpace(dltTemplateId) ? null
            : dltTemplateId.Trim() is { Length: <= 30 } d && d.All(char.IsAsciiDigit) ? d
            : throw new DomainException("template.dlt_invalid", "A DLT template id is up to 30 digits.");
        LanguageCode = (languageCode ?? string.Empty).Trim() is { Length: >= 2 and <= 10 } l ? l : throw new DomainException("template.language_invalid", "Give the language code, for example en or ta.");
        var text = (body ?? string.Empty).Trim();
        if (text.Length is 0 or > 1000)
        {
            throw new DomainException("template.body_invalid", "The message text is 1 to 1000 characters.");
        }

        var unknown = Placeholder().Matches(text).Select(m => m.Groups[1].Value).Where(p => !MessageKinds.Parameters(Kind).Contains(p)).Distinct().ToList();
        Body = unknown.Count == 0
            ? text
            : throw new DomainException("template.placeholder_invalid", $"Unknown placeholders: {string.Join(", ", unknown)}. Use {string.Join(", ", MessageKinds.Parameters(Kind).Select(p => "{{" + p + "}}"))}.");
        IsActive = isActive;
    }

    /// <summary>The text with the placeholders filled in.</summary>
    public static string Render(string body, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(body, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[a-z0-9_]{1,100}$")]
    private static partial Regex TemplateName();
}

/// <summary>
/// One message to one debtor about one document on one channel (an outbox row). It is written in the same transaction as
/// the invoice or receipt, so it exists exactly when the document does, and is sent afterwards; failing to send never
/// touches the document. One per document, kind and channel, so it is never sent twice.
/// </summary>
public sealed class OutboundMessage : ITenantOwned
{
    public const int MaxAttempts = 5;

    private OutboundMessage()
    {
        Channel = Kind = Status = ParametersJson = Body = DocumentNumber = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid DebtorId { get; private set; }

    public string Channel { get; private set; }

    public string Kind { get; private set; }

    public Guid DocumentId { get; private set; }

    public string DocumentNumber { get; private set; }

    public string? ToNumber { get; private set; }

    public string ParametersJson { get; private set; }

    /// <summary>The rendered text (what an SMS says; for WhatsApp, a readable copy of the template).</summary>
    public string Body { get; private set; }

    public string Status { get; private set; }

    public string? SkipReason { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public string? ProviderMessageId { get; private set; }

    /// <summary>SHA-256 of the attachment sent (the invoice PDF), so the exact document sent can be proven later.</summary>
    public string? AttachmentSha256 { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public DateTimeOffset? FailedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static OutboundMessage Queue(
        Guid businessId, Guid debtorId, string channel, string kind, Guid documentId, string documentNumber, string? toNumber, string parametersJson, string body,
        string? skipReason, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        DebtorId = debtorId,
        Channel = channel,
        Kind = kind,
        DocumentId = documentId,
        DocumentNumber = documentNumber,
        ToNumber = toNumber,
        ParametersJson = parametersJson,
        Body = body.Length <= 1000 ? body : body[..1000],
        Status = skipReason is null ? MessageStatus.Queued : MessageStatus.Skipped,
        SkipReason = skipReason,
        NextAttemptAtUtc = skipReason is null ? now : null,
        CreatedAtUtc = now,
    };

    public void Sent(string providerMessageId, string? attachmentSha256, DateTimeOffset now)
    {
        RequireStatus(MessageStatus.Queued);
        (Status, ProviderMessageId, AttachmentSha256, SentAtUtc, Attempts, NextAttemptAtUtc, LastError) =
            (MessageStatus.Sent, providerMessageId, attachmentSha256, now, Attempts + 1, null, null);
    }

    /// <summary>A failed attempt: retried later with growing delays (also while the server is offline), until the last.</summary>
    public void AttemptFailed(string error, bool permanent, DateTimeOffset now)
    {
        RequireStatus(MessageStatus.Queued);
        Attempts++;
        LastError = error.Length <= 500 ? error : error[..500];
        if (permanent || Attempts >= MaxAttempts)
        {
            (Status, FailedAtUtc, NextAttemptAtUtc) = (MessageStatus.Failed, now, null);
            return;
        }

        NextAttemptAtUtc = now + RetryDelay(Attempts);
    }

    /// <summary>A status the provider reports later. Never moves a message back (reports may arrive out of order).</summary>
    public bool Report(string status, string? error, DateTimeOffset at)
    {
        if (Status is MessageStatus.Queued or MessageStatus.Skipped or MessageStatus.Failed || MessageStatus.Rank(status) <= MessageStatus.Rank(Status))
        {
            return false;
        }

        switch (status)
        {
            case MessageStatus.Delivered:
                DeliveredAtUtc = at;
                break;
            case MessageStatus.Read:
                ReadAtUtc = at;
                DeliveredAtUtc ??= at;
                break;
            case MessageStatus.Failed:
                FailedAtUtc = at;
                LastError = error is { Length: > 500 } e ? e[..500] : error;
                break;
            default:
                return false;
        }

        Status = status;
        return true;
    }

    /// <summary>Puts a failed message back in the queue (by hand, after fixing what made it fail).</summary>
    public void Retry(DateTimeOffset now)
    {
        RequireStatus(MessageStatus.Failed);
        if (ProviderMessageId is not null)
        {
            throw new DomainException("message.already_sent", "The provider accepted this message; it is not sent again.");
        }

        (Status, Attempts, NextAttemptAtUtc, FailedAtUtc) = (MessageStatus.Queued, 0, now, null);
    }

    public static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1),
    };

    private void RequireStatus(string status)
    {
        if (Status != status)
        {
            throw new DomainException("message.status_invalid", $"The message is {Status.ToLowerInvariant()}, not {status.ToLowerInvariant()}.");
        }
    }
}

/// <summary>One step in a message's life (append-only): queued, each attempt, provider reports, retries.</summary>
public sealed class MessageEvent : ITenantOwned
{
    private MessageEvent()
    {
        Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid MessageId { get; private set; }

    public string Status { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset AtUtc { get; private set; }

    public static MessageEvent Record(Guid businessId, Guid messageId, string status, string? detail, DateTimeOffset now) => new()
    {
        Id = SequentialGuid.Next(now),
        BusinessId = businessId,
        MessageId = messageId,
        Status = status,
        Detail = detail is { Length: > 500 } d ? d[..500] : detail,
        AtUtc = now,
    };
}

/// <summary>Whether a business sends WhatsApp messages and SMS at all (each also needs the debtor's consent).</summary>
public sealed class MessagingSettings : ITenantOwned
{
    private MessagingSettings()
    {
    }

    public Guid BusinessId { get; private set; }

    public bool WhatsAppEnabled { get; private set; }

    public bool SmsEnabled { get; private set; }

    public bool SendInvoices { get; private set; }

    public bool SendReceipts { get; private set; }

    public uint RowVersion { get; private set; }

    public static MessagingSettings Default(Guid businessId) => new() { BusinessId = businessId, SendInvoices = true, SendReceipts = true };

    public void Change(bool whatsApp, bool sms, bool invoices, bool receipts) => (WhatsAppEnabled, SmsEnabled, SendInvoices, SendReceipts) = (whatsApp, sms, invoices, receipts);
}

public static class MessageFormat
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public static string Money(decimal value) => value.ToString("#,##,##0.00", India);

    public static string Date(DateOnly value) => value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
}
