namespace SupermarketBilling.Application.Contracts;

/// <param name="WhatsAppReady">Whether a WhatsApp provider is configured for this installation (Simulated counts as configured).</param>
public sealed record MessagingSettingsDto(
    bool WhatsAppEnabled, bool SmsEnabled, bool SendInvoices, bool SendReceipts, string WhatsAppProvider, string SmsProvider, bool WhatsAppReady, uint RowVersion);

public sealed record UpdateMessagingSettingsRequest(bool WhatsAppEnabled, bool SmsEnabled, bool SendInvoices, bool SendReceipts, uint RowVersion);

public sealed record MessageTemplateDto(
    string Kind, string Channel, string? ProviderTemplateName, string? DltTemplateId, string LanguageCode, string Body, bool IsActive, IReadOnlyList<string> Placeholders);

public sealed record UpdateMessageTemplateRequest(string? ProviderTemplateName, string? DltTemplateId, string LanguageCode, string Body, bool IsActive);

public sealed record MessageEventDto(string Status, string? Detail, DateTimeOffset AtUtc);

public sealed record MessageDto(
    Guid Id, Guid DebtorId, string DebtorName, string Channel, string Kind, string DocumentNumber, string? ToNumber, string Body, string Status, string? SkipReason,
    int Attempts, string? LastError, DateTimeOffset CreatedAtUtc, DateTimeOffset? SentAtUtc, DateTimeOffset? DeliveredAtUtc, DateTimeOffset? ReadAtUtc,
    DateTimeOffset? FailedAtUtc, string? AttachmentSha256, IReadOnlyList<MessageEventDto> Events);
