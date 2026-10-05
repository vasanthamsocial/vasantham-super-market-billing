using System.Text;
using Microsoft.Extensions.Time.Testing;
using SupermarketBilling.CounterAgent;
using SupermarketBilling.Domain.Sales;
using static SupermarketBilling.UnitTests.Sales.OfflineBillingTests;

namespace SupermarketBilling.UnitTests.CounterAgent;

/// <summary>The counter agent's offline billing: numbering, retries, restarts, encryption at rest, delivery to the server.</summary>
public sealed class OfflineEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sb-offline-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly List<OfflineEngine> _engines = [];

    public void Dispose()
    {
        _engines.ForEach(e => e.Dispose());
        Directory.Delete(_directory, recursive: true);
    }

    private OfflineEngine Engine()
    {
        var engine = OfflineEngine.Open(_directory, _clock);
        _engines.Add(engine);
        return engine;
    }

    private static IssueOfflineRequest Request(decimal total, params (Guid Pack, decimal Quantity, decimal? Mrp)[] lines) =>
        new(Guid.CreateVersion7(), Cart(lines), [new PaymentInput("CASH", total, null)], total);

    [Fact]
    public async Task Without_a_pack_nothing_is_billed()
    {
        var engine = Engine();
        var status = await engine.StatusAsync(default);
        Assert.False(status.Ready);
        Assert.Contains("no offline price list", status.Refusal, StringComparison.Ordinal);
        Assert.Contains("no offline price list", (await Assert.ThrowsAsync<OfflineException>(() => engine.IssueAsync(Request(52m, (Atta, 1, null)), default))).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bills_take_the_next_numbers_and_a_retry_gets_the_same_bill()
    {
        var engine = Engine();
        await engine.SetPackAsync(Pack(next: 5), default);
        var request = Request(52m, (Atta, 1, null));
        var first = await engine.IssueAsync(request, default);
        var retry = await engine.IssueAsync(request, default);
        var second = await engine.IssueAsync(Request(40m, (Tomato, 1, null)), default);
        Assert.Equal(("C1/OF-000005", first.Id), (first.Number, retry.Id));
        Assert.Equal("C1/OF-000006", second.Number);
        Assert.Equal("C1/OF-000007", (await engine.StatusAsync(default)).NextNumber);

        // The total the cashier collected must be the bill's.
        Assert.Contains("not Rs. 50.00", (await Assert.ThrowsAsync<OfflineException>(() => engine.IssueAsync(Request(50m, (Atta, 1, null)), default))).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bills_survive_a_restart_and_leave_only_once_the_server_has_them()
    {
        var engine = Engine();
        await engine.SetPackAsync(Pack(), default);
        var a = await engine.IssueAsync(Request(52m, (Atta, 1, null)), default);
        var b = await engine.IssueAsync(Request(104m, (Atta, 2, null)), default);

        var restarted = Engine();
        Assert.Equal([a.Number, b.Number], (await restarted.PendingAsync(default)).Select(x => x.Number));
        var status = await restarted.AcknowledgeAsync(new OfflineAckRequest([new OfflineAck(a.Id, "POSTED"), new OfflineAck(b.Id, "NOT_PROCESSED")]), default);
        Assert.Equal((1, 104m), (status.Pending, status.PendingAmount));
        Assert.Equal(b.Id, Assert.Single(await restarted.PendingAsync(default)).Id);
        Assert.Equal("C1/OF-000003", (await restarted.IssueAsync(Request(40m, (Tomato, 1, null)), default)).Number);
    }

    [Fact]
    public async Task Nothing_on_disk_can_be_read_without_the_windows_user()
    {
        var engine = Engine();
        await engine.SetPackAsync(Pack(), default);
        await engine.IssueAsync(Request(52m, (Atta, 1, null)), default);
        var files = Directory.GetFiles(_directory);
        Assert.Contains(files, f => Path.GetFileName(f).StartsWith("bill-", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var text = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file));
            Assert.DoesNotContain("Atta", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Priya", text, StringComparison.Ordinal);
            Assert.DoesNotContain("C1/OF", text, StringComparison.Ordinal);
        }

        // A changed file is refused rather than read.
        var bill = files.First(f => Path.GetFileName(f).StartsWith("bill-", StringComparison.Ordinal));
        var bytes = await File.ReadAllBytesAsync(bill);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(bill, bytes);
        await Assert.ThrowsAsync<OfflineException>(() => Engine().PendingAsync(default));
    }

    [Fact]
    public async Task A_newer_pack_never_moves_the_numbers_back_and_one_from_another_shift_waits_for_the_bills()
    {
        var engine = Engine();
        var pack = Pack();
        await engine.SetPackAsync(pack, default);
        await engine.IssueAsync(Request(52m, (Atta, 1, null)), default);

        // Made before the bill reached the server: it still says 1 is next.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var status = await engine.SetPackAsync(Pack() with { CreatedAtUtc = _clock.GetUtcNow() }, default);
        Assert.Equal("C1/OF-000002", status.NextNumber);

        // An older pack is ignored.
        Assert.Equal(_clock.GetUtcNow(), (await engine.SetPackAsync(pack, default)).PackCreatedAtUtc);

        // Another shift's pack waits until this shift's bills have gone.
        var other = Pack(shift: Guid.NewGuid()) with { CreatedAtUtc = _clock.GetUtcNow().AddMinutes(1) };
        Assert.Contains("another shift", (await Assert.ThrowsAsync<OfflineException>(() => engine.SetPackAsync(other, default))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_counter_stops_at_its_limits()
    {
        var engine = Engine();
        await engine.SetPackAsync(Pack(bills: 2), default);
        await engine.IssueAsync(Request(52m, (Atta, 1, null)), default);
        await engine.IssueAsync(Request(52m, (Atta, 1, null)), default);
        Assert.Contains("at most 2 bills", (await Assert.ThrowsAsync<OfflineException>(() => engine.IssueAsync(Request(52m, (Atta, 1, null)), default))).Message,
            StringComparison.Ordinal);
        Assert.False((await engine.StatusAsync(default)).Ready);
    }

    [Fact]
    public async Task Items_are_found_by_barcode_or_name()
    {
        var engine = Engine();
        await engine.SetPackAsync(Pack(), default);
        Assert.Equal(Atta, Assert.Single(await engine.FindAsync("8901234567894", default)).VariantUnitId);
        Assert.Equal(Tomato, Assert.Single(await engine.FindAsync("toma", default)).VariantUnitId);
        Assert.Empty(await engine.FindAsync("nothing", default));
    }
}
