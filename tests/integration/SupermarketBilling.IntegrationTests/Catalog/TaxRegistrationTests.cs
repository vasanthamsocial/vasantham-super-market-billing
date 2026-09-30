using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Catalog;

/// <summary>Tax-registration changes, on their own installation (they change the business's legal status).</summary>
public sealed class TaxRegistrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private string Url => $"/api/v1/businesses/{factory.BusinessId}/tax-registrations";

    private static readonly string Gstin33 = Gstin.Complete("33AABCT1234F1Z");

    [Fact]
    public async Task Accountant_prepares_and_someone_else_approves_a_change_that_starts_in_the_future()
    {
        // Setup: an accountant appointed by the owner (the approval is waived: nobody else could approve it).
        var (accountant, _, _, _) = await factory.CreateSignedInUserAsync("accountant");
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var tomorrow = DateOnly.FromDateTime(factory.Clock.GetUtcNow().AddDays(2).UtcDateTime);

        // Managers approve but cannot prepare; the accountant must confirm a backup.
        using (var manager = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword))
        {
            var notReviewer = await manager.PostJsonAsync($"{Url}/change-requests", Change(tomorrow));
            Assert.Equal(HttpStatusCode.Forbidden, notReviewer.StatusCode);
        }

        using (accountant)
        {
            Assert.Equal("tax_mode.backup_required", await (await accountant.PostJsonAsync($"{Url}/change-requests", Change(tomorrow) with { BackupConfirmed = false })).ProblemCodeAsync());
            var yesterday = DateOnly.FromDateTime(factory.Clock.GetUtcNow().AddDays(-2).UtcDateTime);
            Assert.Equal("tax_mode.backdated", await (await accountant.PostJsonAsync($"{Url}/change-requests", Change(yesterday))).ProblemCodeAsync());
            Assert.Equal("tax_mode.gstin_required", await (await accountant.PostJsonAsync($"{Url}/change-requests", Change(tomorrow) with { Gstin = null })).ProblemCodeAsync());

            var requested = await accountant.PostJsonAsync($"{Url}/change-requests", Change(tomorrow));
            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
            var approvalId = (await requested.Content.ReadFromJsonAsync<TaxRegistrationChangeResponse>(TestClient.Json))!.ApprovalRequestId;

            Assert.Equal("tax_mode.pending", await (await accountant.PostJsonAsync($"{Url}/change-requests", Change(tomorrow))).ProblemCodeAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostJsonAsync($"/api/v1/approvals/{approvalId}/approve", new ApprovalDecisionRequest(null))).StatusCode);

            (await owner.PostJsonAsync($"/api/v1/approvals/{approvalId}/approve", new ApprovalDecisionRequest("Certificate checked"))).EnsureSuccessStatusCode();
        }

        var history = await owner.GetJsonAsync<List<TaxRegistrationDto>>(Url);
        Assert.Equal(2, history.Count);
        var change = history.Single(h => h.Mode == "GST_REGULAR");
        Assert.Equal(tomorrow, change.EffectiveFrom);
        Assert.Equal(Gstin33, change.Gstin);
        Assert.False(change.IsCurrent); // takes effect in the future; today's invoices keep the current mode
        Assert.True(history.Single(h => h.Mode == "NOT_GST_REGISTERED").IsCurrent);

        // History cannot be edited, even with direct database access.
        await using var db = await factory.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE tax_registrations SET mode = 'GST_COMPOSITION'", db);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
    }

    private static TaxRegistrationChangeRequest Change(DateOnly effectiveFrom) =>
        new("GST_REGULAR", Gstin33, effectiveFrom, "Registered for GST: turnover above the threshold", "REG-06 certificate", BackupConfirmed: true);
}
