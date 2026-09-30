using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SupermarketBilling.Application.Auditing;
using SupermarketBilling.Domain.Auditing;
using SupermarketBilling.IntegrationTests.Infrastructure;

namespace SupermarketBilling.IntegrationTests.Database;

/// <summary>
/// Verifies database-level guarantees using the least-privilege runtime account the API itself uses.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class AuditTrailImmutabilityTests(ApiFactory factory)
{
    [Fact]
    public async Task Audit_events_can_be_appended_and_receive_a_sequence()
    {
        var auditEvent = await AppendAsync("test.appended");

        await using var connection = await OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT sequence, payload_json::text FROM audit_events WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", auditEvent.Id);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetInt64(0) > 0);
        Assert.Equal("{\"source\": \"integration-test\"}", reader.GetString(1));
    }

    [Theory]
    [InlineData("UPDATE audit_events SET event_type = 'tampered' WHERE id = @id")]
    [InlineData("DELETE FROM audit_events WHERE id = @id")]
    public async Task Audit_events_reject_update_and_delete(string sql)
    {
        var auditEvent = await AppendAsync("test.immutable");

        await using var connection = await OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", auditEvent.Id);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_events_cannot_be_truncated_by_the_runtime_account()
    {
        await using var connection = await OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand("TRUNCATE audit_events", connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        // The runtime account lacks the TRUNCATE privilege; the trigger is a second line of defence.
        Assert.Contains(error.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.RestrictViolation });
    }

    [Fact]
    public async Task Audit_payload_must_be_a_json_object()
    {
        await using var connection = await OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO audit_events (id, occurred_at_utc, event_type, payload_json) " +
            "VALUES (gen_random_uuid(), now(), 'test.bad-payload', '[1,2,3]'::jsonb)", connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Theory]
    [InlineData("CREATE TABLE should_not_exist (id int)")]
    [InlineData("DROP TABLE audit_events")]
    [InlineData("ALTER TABLE audit_events DISABLE TRIGGER trg_audit_events_no_update_delete")]
    public async Task Runtime_account_cannot_change_the_schema(string sql)
    {
        await using var connection = await OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private async Task<AuditEvent> AppendAsync(string eventType)
    {
        await using var scope = await factory.CreateTenantScopeAsync();
        var trail = scope.ServiceProvider.GetRequiredService<IAuditTrail>();
        var auditEvent = AuditEvent.Create(
            DateTimeOffset.UtcNow,
            eventType,
            entityType: "test",
            entityId: Guid.NewGuid().ToString(),
            payloadJson: "{\"source\":\"integration-test\"}");
        await trail.AppendAsync(auditEvent, CancellationToken.None);
        return auditEvent;
    }

    private Task<NpgsqlConnection> OpenAppConnectionAsync() => factory.OpenAppConnectionAsync();

}
