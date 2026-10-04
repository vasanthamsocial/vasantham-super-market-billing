using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Messaging;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;
using SupermarketBilling.IntegrationTests.Sales;

namespace SupermarketBilling.IntegrationTests.Messaging;

[Collection(ApiTestGroup.Name)]
public sealed class MessagingTests(ApiFactory factory)
{
    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    private string Base => $"/api/v1/businesses/{Business}";

    private SimulatedMessaging Simulator => factory.Services.GetRequiredService<SimulatedMessaging>();

    private Task<int> DispatchAsync() => MessagingWorker.DispatchAllAsync(factory.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None);

    private static string Mobile() => "9" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task EnableAsync(TestClient owner)
    {
        var settings = await owner.GetJsonAsync<MessagingSettingsDto>($"{Base}/messaging/settings");
        (await owner.PutJsonAsync($"{Base}/messaging/settings", new UpdateMessagingSettingsRequest(true, true, true, true, settings.RowVersion))).EnsureSuccessStatusCode();
    }

    private async Task<DebtorDto> DebtorAsync(TestClient owner, string mobile, bool whatsApp = true, bool sms = true, decimal opening = 0)
    {
        var code = $"M{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var response = await owner.PostJsonAsync($"{Base}/debtors", new CreateDebtorRequest(code, $"Message Party {code}", null, null, "33",
            WhatsAppNumber: mobile, SmsNumber: mobile, WhatsAppConsent: whatsApp, SmsConsent: sms, CreditPeriodDays: 10, CreditLimit: 10_000m,
            OpeningBalance: opening == 0 ? null : opening, OpeningBalanceDate: new DateOnly(2026, 9, 1)));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<DebtorDto>(TestClient.Json))!;
    }

    private async Task<List<MessageDto>> MessagesAsync(TestClient owner, Guid debtorId) => await owner.GetJsonAsync<List<MessageDto>>($"{Base}/messages?debtorId={debtorId}");

    // Other tests switch SMS on for receipts, so a debtor without SMS consent also gets a skipped SMS record.
    private async Task<List<MessageDto>> WhatsAppAsync(TestClient owner, Guid debtorId) => [.. (await MessagesAsync(owner, debtorId)).Where(m => m.Channel == "WHATSAPP")];

    private static string Sign(string body) => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiFactory.WebhookSecret), Encoding.UTF8.GetBytes(body)));

    private static async Task<HttpResponseMessage> WebhookAsync(TestClient client, string body, string? signature = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/messaging/whatsapp/webhook", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Hub-Signature-256", signature ?? Sign(body));
        return await client.Http.SendAsync(request);
    }

    private static string Statuses(params (string Id, string Status, long At)[] statuses) =>
        "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"value\":{\"statuses\":[" +
        string.Join(",", statuses.Select(s => $"{{\"id\":\"{s.Id}\",\"status\":\"{s.Status}\",\"timestamp\":\"{s.At}\"}}")) + "]}}]}]}";

    [Fact]
    public async Task A_credit_invoice_goes_to_the_debtor_on_whatsapp_once_with_its_pdf_and_delivery_is_tracked()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        await EnableAsync(owner);
        var mobile = Mobile();
        var debtor = await DebtorAsync(owner, mobile);
        var (_, pack) = await Pos.StockedProductAsync(owner, Business, Store, price: 100m);
        var (browser, _) = await Pos.CounterBrowserAsync(factory, Business, Store);
        InvoiceDto invoice;
        using (browser)
        {
            invoice = await Pos.IssueAsync(browser, Pos.Issue(Pos.Cart(new CartLineRequest(pack, 2)) with { DebtorId = debtor.Id }, 200m, new PaymentRequest("ON_ACCOUNT", 200m, null)));
        }

        var queued = Assert.Single(await MessagesAsync(owner, debtor.Id));
        Assert.Equal(("WHATSAPP", "CREDIT_INVOICE", "QUEUED", invoice.Number), (queued.Channel, queued.Kind, queued.Status, queued.DocumentNumber));

        Assert.True(await DispatchAsync() >= 1);
        await DispatchAsync(); // nothing more for this invoice: never sent twice
        var sent = Simulator.Sent.Where(s => s.Message.To == "+91" + mobile).ToList();
        var (_, providerId, outgoing) = Assert.Single(sent);
        Assert.Equal([debtor.DisplayName, invoice.Number, "200.00", invoice.DueDate!.Value.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture), "200.00"],
            outgoing.Parameters);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(outgoing.Attachment!.Content[..4]), StringComparison.Ordinal);
        var message = Assert.Single(await MessagesAsync(owner, debtor.Id));
        Assert.Equal(("SENT", Convert.ToHexStringLower(SHA256.HashData(outgoing.Attachment.Content))), (message.Status, message.AttachmentSha256));

        // Delivery reports, signed by the provider; a late "delivered" after "read" does not move it back.
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebhookAsync(anonymous, Statuses((providerId, "delivered", 1_790_000_000)), "sha256=00")).StatusCode);
        (await WebhookAsync(anonymous, Statuses((providerId, "read", 1_790_000_100)))).EnsureSuccessStatusCode();
        (await WebhookAsync(anonymous, Statuses((providerId, "delivered", 1_790_000_050)))).EnsureSuccessStatusCode();
        message = Assert.Single(await MessagesAsync(owner, debtor.Id));
        Assert.Equal(("READ", true), (message.Status, message.DeliveredAtUtc is not null));
        Assert.Equal(["QUEUED", "SENT", "READ"], message.Events.Select(e => e.Status));

        // The provider's URL verification.
        Assert.Equal("12345", await (await anonymous.GetAsync("/api/v1/messaging/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=verify-me&hub.challenge=12345")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync("/api/v1/messaging/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1")).StatusCode);
    }

    [Fact]
    public async Task A_receipt_is_confirmed_on_whatsapp_and_sms_with_the_balances_and_without_consent_nothing_is_sent()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        await EnableAsync(owner);
        (await owner.PutJsonAsync($"{Base}/messaging/templates/RECEIPT/SMS", new UpdateMessageTemplateRequest(null, "1107160000000012345", "en",
            "Rcvd Rs.{{amount}} ({{method}}) receipt {{receipt_number}}. Bal Rs.{{current_balance}} -STORE", true))).EnsureSuccessStatusCode();
        Assert.Equal("template.placeholder_invalid", await (await owner.PutJsonAsync($"{Base}/messaging/templates/RECEIPT/SMS",
            new UpdateMessageTemplateRequest(null, null, "en", "Hello {{nobody}}", true))).ProblemCodeAsync());

        var mobile = Mobile();
        var debtor = await DebtorAsync(owner, mobile, opening: 1000m);
        var receipt = await owner.PostJsonAsync($"{Base}/debtor-receipts", new DebtorReceiptRequest(debtor.Id, "UPI", 400m, Store, "UPI-1", IdempotencyKey: Guid.NewGuid().ToString("N")));
        await receipt.EnsureSuccessWithBodyAsync();
        var number = (await receipt.Content.ReadFromJsonAsync<DebtorReceiptDto>(TestClient.Json))!.Number;

        await DispatchAsync();
        var sent = Simulator.Sent.Where(s => s.Message.To == "+91" + mobile).ToList();
        var whatsApp = sent.Single(s => s.Channel == "WHATSAPP").Message;
        Assert.Equal([debtor.DisplayName, number, "400.00", "upi (UPI-1)", "1,000.00", "600.00"], whatsApp.Parameters);
        var sms = sent.Single(s => s.Channel == "SMS").Message;
        Assert.Equal(($"Rcvd Rs.400.00 (upi (UPI-1)) receipt {number}. Bal Rs.600.00 -STORE", "1107160000000012345"), (sms.Body, sms.DltTemplateId));

        // No consent: recorded as skipped, with why.
        var quiet = await DebtorAsync(owner, Mobile(), whatsApp: false, sms: false, opening: 100m);
        (await owner.PostJsonAsync($"{Base}/debtor-receipts", new DebtorReceiptRequest(quiet.Id, "CASH", 50m, Store, IdempotencyKey: Guid.NewGuid().ToString("N"))))
            .EnsureSuccessStatusCode();
        Assert.All(await MessagesAsync(owner, quiet.Id), m => Assert.Equal(("SKIPPED", "No consent to messages"), (m.Status, m.SkipReason)));
    }

    [Fact]
    public async Task Sending_failures_are_retried_later_and_never_touch_the_receipt()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        await EnableAsync(owner);
        var debtor = await DebtorAsync(owner, Mobile(), sms: false, opening: 500m);
        Simulator.FailNext(new MessageProviderException("Network is unreachable", permanent: false));
        (await owner.PostJsonAsync($"{Base}/debtor-receipts", new DebtorReceiptRequest(debtor.Id, "CASH", 100m, Store, IdempotencyKey: Guid.NewGuid().ToString("N"))))
            .EnsureSuccessStatusCode();
        await DispatchAsync();
        var waiting = Assert.Single(await WhatsAppAsync(owner, debtor.Id));
        Assert.Equal(("QUEUED", 1, "Network is unreachable"), (waiting.Status, waiting.Attempts, waiting.LastError));
        Assert.Equal(400m, (await owner.GetJsonAsync<DebtorDto>($"{Base}/debtors/{debtor.Id}")).Balance); // the receipt stands

        await DispatchAsync(); // too early for the next attempt
        Assert.Equal("QUEUED", Assert.Single(await WhatsAppAsync(owner, debtor.Id)).Status);
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await DispatchAsync();
        Assert.Equal("SENT", Assert.Single(await WhatsAppAsync(owner, debtor.Id)).Status);

        // A permanent refusal fails at once; fixed by hand, it is sent again.
        var other = await DebtorAsync(owner, Mobile(), sms: false, opening: 500m);
        Simulator.FailNext(new MessageProviderException("Template not approved", permanent: true));
        (await owner.PostJsonAsync($"{Base}/debtor-receipts", new DebtorReceiptRequest(other.Id, "CASH", 100m, Store, IdempotencyKey: Guid.NewGuid().ToString("N"))))
            .EnsureSuccessStatusCode();
        await DispatchAsync();
        var failed = Assert.Single(await WhatsAppAsync(owner, other.Id));
        Assert.Equal("FAILED", failed.Status);
        (await owner.PostJsonAsync($"{Base}/messages/{failed.Id}/retry", new { })).EnsureSuccessStatusCode();
        await DispatchAsync();
        Assert.Equal("SENT", Assert.Single(await WhatsAppAsync(owner, other.Id)).Status);
    }

    [Fact]
    public async Task Senders_running_at_the_same_time_send_each_message_exactly_once()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        await EnableAsync(owner);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(DispatchAsync))); // warm up: first-time query compilation would otherwise stagger the senders
        var debtors = new List<(DebtorDto Debtor, string Mobile)>();
        for (var i = 0; i < 6; i++)
        {
            var mobile = Mobile();
            var debtor = await DebtorAsync(owner, mobile, sms: false, opening: 500m);
            (await owner.PostJsonAsync($"{Base}/debtor-receipts", new DebtorReceiptRequest(debtor.Id, "CASH", 10m, Store, IdempotencyKey: Guid.NewGuid().ToString("N"))))
                .EnsureSuccessStatusCode();
            debtors.Add((debtor, mobile));
        }

        Simulator.Latency = TimeSpan.FromMilliseconds(150);
        try
        {
            Assert.Equal(6, (await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(DispatchAsync)))).Sum());
        }
        finally
        {
            Simulator.Latency = TimeSpan.Zero;
        }

        foreach (var (debtor, mobile) in debtors)
        {
            Assert.Single(Simulator.Sent, s => s.Message.To == "+91" + mobile);
            Assert.Equal("SENT", Assert.Single(await WhatsAppAsync(owner, debtor.Id)).Status);
        }
    }

    [Fact]
    public async Task A_stop_reply_turns_off_the_debtors_whatsapp_and_the_database_keeps_messages_as_sent()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        await EnableAsync(owner);
        var mobile = Mobile();
        var debtor = await DebtorAsync(owner, mobile, sms: false, opening: 300m);
        using var anonymous = factory.CreateBrowserClient();
        var stop = "{\"entry\":[{\"changes\":[{\"value\":{\"messages\":[{\"from\":\"91" + mobile + "\",\"type\":\"text\",\"text\":{\"body\":\"stop\"}}]}}]}]}";
        (await WebhookAsync(anonymous, stop)).EnsureSuccessStatusCode();
        Assert.False((await owner.GetJsonAsync<DebtorDto>($"{Base}/debtors/{debtor.Id}")).WhatsAppConsent);

        (await owner.PostJsonAsync($"{Base}/debtor-receipts", new DebtorReceiptRequest(debtor.Id, "CASH", 100m, Store, IdempotencyKey: Guid.NewGuid().ToString("N"))))
            .EnsureSuccessStatusCode();
        var skipped = Assert.Single(await WhatsAppAsync(owner, debtor.Id));
        Assert.Equal("SKIPPED", skipped.Status);

        await using (var db = await factory.OpenAppConnectionAsync())
        {
            foreach (var sql in new[] { "UPDATE outbound_messages SET body = 'changed' WHERE id = @id", "DELETE FROM outbound_messages WHERE id = @id",
                         "UPDATE outbound_messages SET status = 'QUEUED', skip_reason = NULL WHERE id = @id" })
            {
                await using var command = new NpgsqlCommand(sql, db);
                command.Parameters.AddWithValue("id", skipped.Id);
                Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
        }

        await DispatchAsync();
        var sql13 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "013_messaging.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql13, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand("DELETE FROM provider_message_refs WHERE message_id IN (SELECT id FROM outbound_messages WHERE status = 'SENT' LIMIT 1)", admin, transaction))
        {
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql13, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("status does not agree", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }
}
