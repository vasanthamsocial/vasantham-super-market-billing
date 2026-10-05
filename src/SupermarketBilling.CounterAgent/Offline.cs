using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;

namespace SupermarketBilling.CounterAgent;

/// <summary>A refusal to bill offline, with a message for the cashier.</summary>
public sealed class OfflineException(string message) : Exception(message);

/// <summary>
/// Files on this PC, each encrypted with Windows DPAPI for the user the agent runs as: unreadable by other users, on
/// other PCs, or when copied off the disk, and a changed file fails to open. Written to a temporary file and then moved,
/// so a power cut leaves the old file or the new one, never half of one.
/// </summary>
internal sealed class OfflineStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SupermarketBilling offline billing v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    public OfflineStore(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Offline billing keeps its bills with Windows data protection: it runs on Windows only.");
        }

        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Save<T>(string name, T value)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(_directory, name);
        var temporary = path + ".tmp";
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value, Json), Entropy, DataProtectionScope.CurrentUser);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public T? Load<T>(string name)
        where T : class
    {
        var path = Path.Combine(_directory, name);
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser), Json);
        }
        catch (CryptographicException e)
        {
            throw new OfflineException($"The offline file {name} cannot be opened (changed, or from another Windows user): {e.Message}");
        }
    }

    public void Delete(string name) => File.Delete(Path.Combine(_directory, name));

    public IEnumerable<string> Names(string prefix) =>
        Directory.EnumerateFiles(_directory, prefix + "*.bin").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal);
}

public sealed record IssueOfflineRequest(Guid Id, OfflineCart Cart, IReadOnlyList<PaymentInput> Payments, decimal ExpectedGrandTotal);

public sealed record OfflineAck(Guid Id, string Status);

public sealed record OfflineAckRequest(IReadOnlyList<OfflineAck> Results);

public sealed record OfflineItemMatch(Guid VariantUnitId, string Name, string UnitCode, IReadOnlyList<decimal> Mrps, string? Barcode);

/// <param name="Refusal">Why a bill cannot be issued offline now (no pack, too old, limits), or null.</param>
public sealed record OfflineStatus(
    bool Ready, string? Refusal, string? Series, string? NextNumber, string? Cashier, Guid? CashierUserId, Guid? DeviceId, DateTimeOffset? PackCreatedAtUtc,
    int Items, OfflineLimits? Limits, int Pending, decimal PendingAmount, DateTimeOffset? OldestPendingUtc);

/// <summary>
/// The counter agent's offline billing (D-039): keeps the newest pack the POS gave it, prices and issues invoices from
/// the counter's offline series with the server's own code, and keeps them (encrypted) until the POS has delivered them
/// to the server. One request at a time, so numbers are never given twice.
/// </summary>
public sealed class OfflineEngine : IDisposable
{
    private const string PackFile = "pack.bin";
    private const string StateFile = "state.bin";
    private const string BillPrefix = "bill-";
    private readonly OfflineStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal OfflineEngine(OfflineStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public static OfflineEngine Open(string directory, TimeProvider clock) => new(new OfflineStore(directory), clock);

    public void Dispose() => _gate.Dispose();

    private sealed record State(string Prefix, long NextSequence);

    public Task<OfflineStatus> StatusAsync(CancellationToken cancellationToken) => Locked(() => Status(), cancellationToken);

    /// <summary>Keeps a newer pack. Refused while bills of another device or cashier's shift are waiting: they must reach the server first.</summary>
    public Task<OfflineStatus> SetPackAsync(OfflinePack pack, CancellationToken cancellationToken) => Locked(() =>
    {
        ArgumentNullException.ThrowIfNull(pack);
        var current = _store.Load<OfflinePack>(PackFile);
        var pending = Pending();
        if (pending.Count > 0 && pending.Any(b => b.DeviceId != pack.DeviceId || b.ShiftId != pack.ShiftId))
        {
            throw new OfflineException("Bills issued offline in another shift are still waiting for the server. Send them first.");
        }

        if (current is null || pack.CreatedAtUtc > current.CreatedAtUtc)
        {
            var state = _store.Load<State>(StateFile);
            var local = state is not null && state.Prefix == pack.NumberPrefix ? state.NextSequence : 1;
            var waiting = pending.Where(b => b.NumberPrefix == pack.NumberPrefix).Select(b => b.Sequence + 1).DefaultIfEmpty(1).Max();
            _store.Save(PackFile, pack);
            _store.Save(StateFile, new State(pack.NumberPrefix, Math.Max(pack.NextSequence, Math.Max(local, waiting))));
        }

        return Status();
    }, cancellationToken);

    public Task<IReadOnlyList<OfflineItemMatch>> FindAsync(string? search, CancellationToken cancellationToken) => Locked(() =>
    {
        var pack = _store.Load<OfflinePack>(PackFile);
        var term = (search ?? string.Empty).Trim();
        if (pack is null || term.Length == 0)
        {
            return (IReadOnlyList<OfflineItemMatch>)[];
        }

        var byCode = pack.Items.Where(i => i.Barcodes.Contains(term, StringComparer.Ordinal)).ToList();
        var found = byCode.Count > 0 ? byCode : pack.Items.Where(i => i.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        return found.Select(i => new OfflineItemMatch(i.VariantUnitId, i.Name, i.UnitCode, i.Mrps, byCode.Count > 0 ? term : i.Barcodes.Count > 0 ? i.Barcodes[0] : null)).ToList();
    }, cancellationToken);

    public Task<OfflineDraft> PriceAsync(OfflineCart cart, CancellationToken cancellationToken) => Locked(() => Domain(() => OfflineBilling.Price(Pack(), cart, _clock.GetUtcNow())), cancellationToken);

    /// <summary>Issues the bill with the next number. The same id again returns the bill already issued (a retry after a lost answer).</summary>
    public Task<OfflineBill> IssueAsync(IssueOfflineRequest request, CancellationToken cancellationToken) => Locked(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var pending = Pending();
        if (pending.FirstOrDefault(b => b.Id == request.Id) is { } already)
        {
            return already;
        }

        var pack = Pack();
        var now = _clock.GetUtcNow();
        var draft = Domain(() => OfflineBilling.Price(pack, request.Cart, now));
        if (draft.Result.GrandTotal != request.ExpectedGrandTotal)
        {
            throw new OfflineException($"The bill total is Rs. {draft.Result.GrandTotal:0.00}, not Rs. {request.ExpectedGrandTotal:0.00}. Check the bill and collect again.");
        }

        if (OfflineBilling.Refusal(pack, pending, draft.Result.GrandTotal, now) is { } refusal)
        {
            throw new OfflineException(refusal);
        }

        var state = _store.Load<State>(StateFile) ?? new State(pack.NumberPrefix, pack.NextSequence);
        var sequence = state.Prefix == pack.NumberPrefix ? state.NextSequence : pack.NextSequence;
        var bill = Domain(() => OfflineBilling.Issue(pack, draft, request.Payments ?? [], request.Id, sequence, now));

        // The bill is on disk before its number moves on: after a crash the number is either used by this bill or still free.
        _store.Save(BillFile(bill), bill);
        _store.Save(StateFile, new State(pack.NumberPrefix, sequence + 1));
        return bill;
    }, cancellationToken);

    public Task<IReadOnlyList<OfflineBill>> PendingAsync(CancellationToken cancellationToken) => Locked(() => (IReadOnlyList<OfflineBill>)Pending(), cancellationToken);

    /// <summary>The server's answers: what it has (posted, quarantined, already received or refused) leaves the agent; what it did not process stays.</summary>
    public Task<OfflineStatus> AcknowledgeAsync(OfflineAckRequest request, CancellationToken cancellationToken) => Locked(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var pending = Pending().ToDictionary(b => b.Id);
        foreach (var result in request.Results ?? [])
        {
            if (result.Status != "NOT_PROCESSED" && pending.TryGetValue(result.Id, out var bill))
            {
                _store.Delete(BillFile(bill));
            }
        }

        return Status();
    }, cancellationToken);

    private OfflineStatus Status()
    {
        var pack = _store.Load<OfflinePack>(PackFile);
        var pending = Pending();
        var state = _store.Load<State>(StateFile);
        var refusal = pack is null ? "This counter has no offline price list yet: it is loaded while the server is reachable." : OfflineBilling.Refusal(pack, pending, 0, _clock.GetUtcNow());
        var next = pack is null ? null : Counter.InvoiceNumber(pack.NumberPrefix, state is not null && state.Prefix == pack.NumberPrefix ? state.NextSequence : pack.NextSequence);
        return new OfflineStatus(refusal is null, refusal, pack?.NumberPrefix, next, pack?.CashierName, pack?.CashierUserId, pack?.DeviceId, pack?.CreatedAtUtc,
            pack?.Items.Count ?? 0, pack?.Limits, pending.Count, pending.Sum(b => b.GrandTotal), pending.Count == 0 ? null : pending.Min(b => b.IssuedAtUtc));
    }

    private OfflinePack Pack() => _store.Load<OfflinePack>(PackFile)
        ?? throw new OfflineException("This counter has no offline price list yet: it is loaded while the server is reachable.");

    private List<OfflineBill> Pending() => _store.Names(BillPrefix).Select(n => _store.Load<OfflineBill>(n)!).OrderBy(b => b.NumberPrefix, StringComparer.Ordinal)
        .ThenBy(b => b.Sequence).ToList();

    private static string BillFile(OfflineBill bill) => $"{BillPrefix}{bill.NumberPrefix.Replace('/', '_')}-{bill.Sequence:D12}.bin";

    private static T Domain<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (DomainException e)
        {
            throw new OfflineException(e.Message);
        }
    }

    private async Task<T> Locked<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
