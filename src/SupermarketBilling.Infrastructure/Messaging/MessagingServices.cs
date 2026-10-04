using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Messaging;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Persistence.Configurations;
using SupermarketBilling.Infrastructure.Sales;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Messaging;

/// <summary>Chooses the provider configured for a channel (none when the installation has not set one up).</summary>
public sealed class MessageProviders(IServiceProvider services, IOptions<MessagingOptions> options)
{
    public IMessageProvider? For(string channel) => channel switch
    {
        MessageChannels.WhatsApp => options.Value.WhatsApp.Provider switch
        {
            "Simulated" => services.GetRequiredService<SimulatedWhatsAppProvider>(),
            "Meta" => services.GetRequiredService<MetaWhatsAppProvider>(),
            _ => null,
        },
        MessageChannels.Sms => options.Value.Sms.Provider == "Simulated" ? services.GetRequiredService<SimulatedSmsProvider>() : null,
        _ => null,
    };
}

/// <summary>
/// Puts messages in the outbox inside the caller's transaction (the invoice's or receipt's): one per document and
/// channel the business sends on. A debtor without consent or a number gets a SKIPPED row, so the audit trail shows
/// why nothing was sent. Never fails the financial transaction for a messaging reason.
/// </summary>
public sealed class MessageOutbox(SupermarketBillingDbContext db, TimeProvider clock)
{
    public async Task QueueAsync(Guid businessId, Debtor debtor, string kind, Guid documentId, string documentNumber, IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        var settings = await db.MessagingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? MessagingSettings.Default(businessId);
        if (kind == MessageKinds.CreditInvoice ? !settings.SendInvoices : !settings.SendReceipts)
        {
            return;
        }

        // Invoices go on WhatsApp (they carry the PDF); confirmations of receipts on WhatsApp and SMS.
        var channels = kind == MessageKinds.CreditInvoice ? [MessageChannels.WhatsApp] : new[] { MessageChannels.WhatsApp, MessageChannels.Sms };
        var templates = await db.MessageTemplates.AsNoTracking().Where(t => t.BusinessId == businessId && t.Kind == kind).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        foreach (var channel in channels)
        {
            if (channel == MessageChannels.WhatsApp ? !settings.WhatsAppEnabled : !settings.SmsEnabled)
            {
                continue;
            }

            var template = templates.FirstOrDefault(t => t.Channel == channel);
            if (template is { IsActive: false })
            {
                continue;
            }

            var (number, consent) = channel == MessageChannels.WhatsApp ? (debtor.WhatsAppNumber, debtor.WhatsAppConsent) : (debtor.SmsNumber, debtor.SmsConsent);
            var skip = number is null ? "No number on record" : !consent ? "No consent to messages" : null;
            var body = MessageTemplate.Render(template?.Body ?? MessageKinds.DefaultBody(kind), values);
            var parameters = JsonSerializer.Serialize(MessageKinds.Parameters(kind).Select(p => values.GetValueOrDefault(p, string.Empty)).ToList());
            var message = OutboundMessage.Queue(businessId, debtor.Id, channel, kind, documentId, documentNumber, number, parameters, body, skip, now);
            db.OutboundMessages.Add(message);
            db.MessageEvents.Add(MessageEvent.Record(businessId, message.Id, message.Status, skip, now));
        }
    }
}

/// <summary>
/// Sends what is due in the outbox of the current tenant. Each message is claimed with FOR UPDATE SKIP LOCKED and kept
/// locked while it is sent, so two senders never send the same message; a failure is retried later with growing
/// delays, and never touches the invoice or receipt.
/// </summary>
public sealed partial class MessageDispatcher(
    SupermarketBillingDbContext db,
    MessageProviders providers,
    BillingService billing,
    TimeProvider clock,
    ILogger<MessageDispatcher> logger)
{
    public async Task<int> DispatchDueAsync(int max, CancellationToken cancellationToken)
    {
        // A channel without a provider is not attempted: its messages wait (and keep their attempts) until one is set up.
        var ready = MessageChannels.All.Where(c => providers.For(c) is not null).ToArray();
        var handled = 0;
        while (handled < max && ready.Length > 0)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var message = (await db.OutboundMessages
                    .FromSql($"SELECT *, xmin FROM outbound_messages WHERE status = 'QUEUED' AND next_attempt_at_utc <= {now} AND channel = ANY({ready}) ORDER BY next_attempt_at_utc LIMIT 1 FOR UPDATE SKIP LOCKED")
                    .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            if (message is null)
            {
                break;
            }

            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            handled++;
        }

        return handled;
    }

    private async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var provider = providers.For(message.Channel);
        try
        {
            if (provider is null)
            {
                throw new MessageProviderException($"No {message.Channel} provider is set up for this installation.", permanent: false);
            }

            var template = await db.MessageTemplates.AsNoTracking()
                .FirstOrDefaultAsync(t => t.BusinessId == message.BusinessId && t.Kind == message.Kind && t.Channel == message.Channel, cancellationToken).ConfigureAwait(false);
            MessageAttachment? attachment = null;
            if (message.Kind == MessageKinds.CreditInvoice)
            {
                // The invoice never changes, so the PDF made now is the same document every time.
                var invoice = await billing.InvoiceAsync(message.DocumentId, cancellationToken).ConfigureAwait(false);
                var timeZone = await db.Stores.AsNoTracking().Where(s => s.Id == invoice.StoreId).Select(s => s.TimeZone).FirstAsync(cancellationToken).ConfigureAwait(false);
                attachment = new MessageAttachment(Documents.InvoicePdf.Render(invoice, timeZone), $"{invoice.Number}.pdf", "application/pdf");
            }

            var parameters = JsonSerializer.Deserialize<List<string>>(message.ParametersJson) ?? [];
            var id = await provider.SendAsync(new OutgoingMessage(message.ToNumber!, template?.ProviderTemplateName, template?.LanguageCode ?? "en", parameters,
                message.Body, template?.DltTemplateId, attachment), cancellationToken).ConfigureAwait(false);
            message.Sent(id, attachment is null ? null : Convert.ToHexStringLower(SHA256.HashData(attachment.Content)), now);
            db.ProviderMessageRefs.Add(new ProviderMessageRef { ProviderMessageId = id, TenantId = await TenantOfAsync(message, cancellationToken).ConfigureAwait(false), MessageId = message.Id });
            db.MessageEvents.Add(MessageEvent.Record(message.BusinessId, message.Id, MessageStatus.Sent, $"{provider.Name}: {id}", now));
        }
        catch (Exception e) when (e is MessageProviderException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var permanent = e is MessageProviderException { Permanent: true };
            message.AttemptFailed(e.Message, permanent, now);
            db.MessageEvents.Add(MessageEvent.Record(message.BusinessId, message.Id, message.Status == MessageStatus.Failed ? MessageStatus.Failed : "RETRY",
                $"Attempt {message.Attempts}: {e.Message}", now));
            LogAttemptFailed(logger, message.Id, message.Attempts, e.Message);
        }
    }

    private async Task<Guid> TenantOfAsync(OutboundMessage message, CancellationToken cancellationToken) =>
        await db.Businesses.AsNoTracking().Where(b => b.Id == message.BusinessId).Select(b => EF.Property<Guid>(b, TenancyModel.TenantIdProperty))
            .FirstAsync(cancellationToken).ConfigureAwait(false);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} attempt {Attempt} failed: {Reason}")]
    private static partial void LogAttemptFailed(ILogger logger, Guid messageId, int attempt, string reason);
}

/// <summary>Sends the outbox of every tenant every few seconds (each tenant in its own scope, under its own row-level security).</summary>
public sealed partial class MessagingWorker(IServiceScopeFactory scopes, IOptions<MessagingOptions> options, ILogger<MessagingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.DispatchIntervalSeconds)));
        do
        {
            try
            {
                await DispatchAllAsync(scopes, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogRoundFailed(logger, e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One sending round over all tenants (also used by tests and diagnostics).</summary>
    public static async Task<int> DispatchAllAsync(IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await ActiveTenantsAsync(scope.ServiceProvider.GetRequiredService<SupermarketBillingDbContext>(), cancellationToken).ConfigureAwait(false);
        }

        var sent = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SupermarketBillingDbContext>();
            await scope.ServiceProvider.GetRequiredService<TenantContext>().SetAsync(tenantId, db, cancellationToken).ConfigureAwait(false);
            sent += await scope.ServiceProvider.GetRequiredService<MessageDispatcher>().DispatchDueAsync(100, cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }

    /// <summary>Tenants are protected by row-level security; background work lists them through a function that returns only ids.</summary>
    internal static Task<List<Guid>> ActiveTenantsAsync(SupermarketBillingDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<Guid>($"SELECT sb_active_tenants() AS \"Value\"").ToListAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message sending round failed: {Reason}")]
    private static partial void LogRoundFailed(ILogger logger, string reason);
}

/// <summary>
/// Delivery reports and replies from the WhatsApp Business Platform. Every call must carry a valid X-Hub-Signature-256
/// (HMAC-SHA256 of the body with the app secret). Reports move messages forward only; a reply of STOP turns off the
/// debtor's WhatsApp consent.
/// </summary>
public sealed class WhatsAppWebhook(SupermarketBillingDbContext db, IServiceScopeFactory scopes, IOptions<MessagingOptions> options, TimeProvider clock)
{
    private static readonly string[] StopWords = ["STOP", "UNSUBSCRIBE", "STOP ALL", "OPT OUT"];

    public string Verify(string? mode, string? token, string? challenge)
    {
        var expected = options.Value.WhatsApp.VerifyToken;
        return mode == "subscribe" && !string.IsNullOrEmpty(expected) && challenge is not null &&
               CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token ?? string.Empty), Encoding.UTF8.GetBytes(expected))
            ? challenge
            : throw AppException.Forbidden("The webhook verification token is wrong.");
    }

    public static bool SignatureValid(byte[] body, string? header, string secret)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrEmpty(secret) || header is null || !header.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        byte[] given;
        try
        {
            given = Convert.FromHexString(header["sha256=".Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public async Task HandleAsync(byte[] body, string? signature, CancellationToken cancellationToken)
    {
        if (!SignatureValid(body, signature, options.Value.WhatsApp.AppSecret))
        {
            throw new AppException(ErrorKind.Unauthorized, "webhook.signature_invalid", "The webhook signature is missing or wrong.");
        }

        using var document = JsonDocument.Parse(body);
        foreach (var value in Values(document.RootElement))
        {
            if (value.TryGetProperty("statuses", out var statuses))
            {
                foreach (var status in statuses.EnumerateArray())
                {
                    await ApplyStatusAsync(status, cancellationToken).ConfigureAwait(false);
                }
            }

            if (value.TryGetProperty("messages", out var messages))
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var text = message.TryGetProperty("text", out var t) && t.TryGetProperty("body", out var b) ? b.GetString()?.Trim().ToUpperInvariant() : null;
                    if (text is not null && StopWords.Contains(text) && message.TryGetProperty("from", out var from))
                    {
                        await OptOutAsync("+" + from.GetString(), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private static IEnumerable<JsonElement> Values(JsonElement root)
    {
        if (!root.TryGetProperty("entry", out var entries))
        {
            yield break;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes))
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (change.TryGetProperty("value", out var value))
                {
                    yield return value;
                }
            }
        }
    }

    private async Task ApplyStatusAsync(JsonElement status, CancellationToken cancellationToken)
    {
        var id = status.TryGetProperty("id", out var i) ? i.GetString() : null;
        var reported = (status.TryGetProperty("status", out var s) ? s.GetString() : null) switch
        {
            "delivered" => MessageStatus.Delivered,
            "read" => MessageStatus.Read,
            "failed" => MessageStatus.Failed,
            _ => null,
        };
        if (id is null || reported is null)
        {
            return;
        }

        var reference = await db.Database.SqlQuery<MessageRef>($"SELECT tenant_id, message_id FROM sb_provider_message_ref({id})")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            return;
        }

        var at = status.TryGetProperty("timestamp", out var ts) && long.TryParse(ts.GetString(), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : clock.GetUtcNow();
        var error = status.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0
            ? string.Join("; ", errors.EnumerateArray().Select(e => e.TryGetProperty("title", out var title) ? title.GetString() : e.ToString()))
            : null;

        // Each report is applied in its own scope, under the tenant of its message.
        await using var scope = scopes.CreateAsyncScope();
        var scoped = scope.ServiceProvider.GetRequiredService<SupermarketBillingDbContext>();
        await scope.ServiceProvider.GetRequiredService<TenantContext>().SetAsync(reference.TenantId, scoped, cancellationToken).ConfigureAwait(false);
        await using var transaction = await scoped.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var message = (await scoped.OutboundMessages.FromSql($"SELECT *, xmin FROM outbound_messages WHERE id = {reference.MessageId} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (message is not null && message.Report(reported, error, at))
        {
            scoped.MessageEvents.Add(MessageEvent.Record(message.BusinessId, message.Id, reported, error, clock.GetUtcNow()));
            await scoped.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record MessageRef(Guid TenantId, Guid MessageId);

    private async Task OptOutAsync(string number, CancellationToken cancellationToken)
    {
        var tenants = await MessagingWorker.ActiveTenantsAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            var scoped = scope.ServiceProvider.GetRequiredService<SupermarketBillingDbContext>();
            await scope.ServiceProvider.GetRequiredService<TenantContext>().SetAsync(tenantId, scoped, cancellationToken).ConfigureAwait(false);
            var debtors = await scoped.Debtors.Where(d => d.WhatsAppNumber == number && d.WhatsAppConsent).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (debtors.Count == 0)
            {
                continue;
            }

            var audit = scope.ServiceProvider.GetRequiredService<AuditRecorder>();
            foreach (var debtor in debtors.Where(d => d.OptOut(MessageChannels.WhatsApp, clock.GetUtcNow())))
            {
                audit.Record("debtor.opted_out", "debtor", debtor.Id, debtor.BusinessId, details: new { channel = MessageChannels.WhatsApp, via = "reply" });
            }

            await scoped.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Messaging set-up and the message log (with each message's history), and retrying failed messages.</summary>
public sealed class MessagingService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    AuditRecorder audit,
    IOptions<MessagingOptions> options,
    TimeProvider clock)
{
    public async Task<MessagingSettingsDto> SettingsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.MessagingView, businessId, cancellationToken).ConfigureAwait(false);
        var s = await db.MessagingSettings.AsNoTracking().FirstOrDefaultAsync(x => x.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? MessagingSettings.Default(businessId);
        return ToDto(s);
    }

    public async Task<MessagingSettingsDto> UpdateSettingsAsync(Guid businessId, UpdateMessagingSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.MessagingManage, businessId, cancellationToken).ConfigureAwait(false);
        var settings = await db.MessagingSettings.FirstOrDefaultAsync(x => x.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            settings = MessagingSettings.Default(businessId);
            db.MessagingSettings.Add(settings);
        }
        else
        {
            db.Entry(settings).Property(s => s.RowVersion).OriginalValue = request.RowVersion;
        }

        settings.Change(request.WhatsAppEnabled, request.SmsEnabled, request.SendInvoices, request.SendReceipts);
        audit.Record("messaging.settings_changed", "business", businessId, businessId, details: request);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(settings);
    }

    public async Task<IReadOnlyList<MessageTemplateDto>> TemplatesAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.MessagingView, businessId, cancellationToken).ConfigureAwait(false);
        var saved = await db.MessageTemplates.AsNoTracking().Where(t => t.BusinessId == businessId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (from kind in MessageKinds.All
                from channel in kind == MessageKinds.CreditInvoice ? [MessageChannels.WhatsApp] : MessageChannels.All
                let t = saved.FirstOrDefault(x => x.Kind == kind && x.Channel == channel) ?? MessageTemplate.Create(businessId, kind, channel, clock.GetUtcNow())
                select ToDto(t)).ToList();
    }

    public async Task<MessageTemplateDto> UpdateTemplateAsync(Guid businessId, string kind, string channel, UpdateMessageTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.MessagingManage, businessId, cancellationToken).ConfigureAwait(false);
        kind = kind.ToUpperInvariant();
        channel = channel.ToUpperInvariant();
        if (kind == MessageKinds.CreditInvoice && channel != MessageChannels.WhatsApp)
        {
            throw AppException.Validation("template.channel_invalid", "Invoices are sent on WhatsApp only (they carry the PDF).");
        }

        var template = await db.MessageTemplates.FirstOrDefaultAsync(t => t.BusinessId == businessId && t.Kind == kind && t.Channel == channel, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (template is null)
            {
                template = MessageTemplate.Create(businessId, kind, channel, clock.GetUtcNow());
                db.MessageTemplates.Add(template);
            }

            template.Update(request.ProviderTemplateName, request.DltTemplateId, request.LanguageCode, request.Body, request.IsActive);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        audit.Record("messaging.template_changed", "business", businessId, businessId, details: new { kind, channel, request.ProviderTemplateName, request.DltTemplateId, request.IsActive });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(template);
    }

    public async Task<IReadOnlyList<MessageDto>> MessagesAsync(Guid businessId, string? status, Guid? debtorId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.MessagingView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.OutboundMessages.AsNoTracking().Where(m => m.BusinessId == businessId);
        query = string.IsNullOrWhiteSpace(status) ? query : query.Where(m => m.Status == status);
        query = debtorId is { } d ? query.Where(m => m.DebtorId == d) : query;
        var rows = await (from m in query
                          join x in db.Debtors.AsNoTracking() on m.DebtorId equals x.Id
                          orderby m.CreatedAtUtc descending
                          select new { Message = m, Debtor = x.TradeName ?? x.LegalName }).Take(300).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = rows.Select(r => r.Message.Id).ToList();
        var events = await db.MessageEvents.AsNoTracking().Where(e => ids.Contains(e.MessageId)).OrderBy(e => e.AtUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r =>
        {
            var m = r.Message;
            return new MessageDto(m.Id, m.DebtorId, r.Debtor, m.Channel, m.Kind, m.DocumentNumber, m.ToNumber, m.Body, m.Status, m.SkipReason, m.Attempts, m.LastError,
                m.CreatedAtUtc, m.SentAtUtc, m.DeliveredAtUtc, m.ReadAtUtc, m.FailedAtUtc, m.AttachmentSha256,
                events.Where(e => e.MessageId == m.Id).Select(e => new MessageEventDto(e.Status, e.Detail, e.AtUtc)).ToList());
        }).ToList();
    }

    public async Task RetryAsync(Guid businessId, Guid messageId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.MessagingManage, businessId, cancellationToken).ConfigureAwait(false);
        var message = await db.OutboundMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Message");
        var now = clock.GetUtcNow();
        try
        {
            message.Retry(now);
        }
        catch (DomainException e)
        {
            throw AppException.Conflict(e.Code, e.Message);
        }

        db.MessageEvents.Add(MessageEvent.Record(businessId, message.Id, MessageStatus.Queued, "Retried by hand", now));
        audit.Record("messaging.retried", "outbound_message", message.Id, businessId);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    private MessagingSettingsDto ToDto(MessagingSettings s) => new(s.WhatsAppEnabled, s.SmsEnabled, s.SendInvoices, s.SendReceipts, options.Value.WhatsApp.Provider,
        options.Value.Sms.Provider, options.Value.WhatsApp.Provider is "Simulated" or "Meta", s.RowVersion);

    private static MessageTemplateDto ToDto(MessageTemplate t) =>
        new(t.Kind, t.Channel, t.ProviderTemplateName, t.DltTemplateId, t.LanguageCode, t.Body, t.IsActive, MessageKinds.Parameters(t.Kind));

    private async Task RequireAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        if (!(await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false)).Contains(businessId))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }
}
