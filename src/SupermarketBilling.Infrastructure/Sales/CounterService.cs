using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Sales;

/// <summary>The counter a POS request is made from: resolved from the device cookie, never from the request body.</summary>
public sealed record PosDevice(CounterDevice Device, Counter Counter, Store Store, Business Business);

/// <summary>Billing counters and the browsers trusted to bill on them.</summary>
public sealed class CounterService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<CounterDto>> ListAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var businesses = await access.BusinessesWithPermissionAsync(Permissions.CountersManage, cancellationToken).ConfigureAwait(false);
        if (!businesses.Contains(businessId))
        {
            await organisation.RequireAsync(Permissions.CountersManage, businessId, null, cancellationToken).ConfigureAwait(false);
        }

        var counters = await db.Counters.AsNoTracking().Where(c => c.BusinessId == businessId).OrderBy(c => c.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var visible = new List<Counter>();
        foreach (var counter in counters)
        {
            if (await access.HasPermissionAsync(Permissions.CountersManage, businessId, counter.StoreId, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(counter);
            }
        }

        return await ToDtosAsync(visible, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CounterDto> CreateAsync(Guid businessId, CreateCounterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.CountersManage, businessId, request.StoreId, cancellationToken).ConfigureAwait(false);
        if (!await db.Stores.AnyAsync(s => s.Id == request.StoreId && s.BusinessId == businessId && s.IsActive, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Store");
        }

        var counter = Counter.Create(businessId, request.StoreId, request.Code, request.Name, clock.GetUtcNow());
        if (await db.Counters.AnyAsync(c => c.BusinessId == businessId && c.Code == counter.Code, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("counter.code_taken", $"Counter code {counter.Code} is already used in this business. Invoice numbers must stay unique.");
        }

        db.Counters.Add(counter);
        audit.Record("counter.created", "counter", counter.Id, businessId, counter.StoreId, details: new { counter.Code, counter.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await ToDtosAsync([counter], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<CounterDto> UpdateAsync(Guid businessId, Guid counterId, UpdateCounterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var counter = await ManagedCounterAsync(businessId, counterId, cancellationToken).ConfigureAwait(false);
        db.Entry(counter).Property(c => c.RowVersion).OriginalValue = request.RowVersion;
        var before = new { counter.Name, counter.IsActive };
        counter.Rename(request.Name);
        counter.SetActive(request.IsActive);
        audit.Record("counter.updated", "counter", counter.Id, businessId, counter.StoreId, details: new { before, after = new { counter.Name, counter.IsActive } });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await ToDtosAsync([counter], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<CounterDeviceDto>> DevicesAsync(Guid businessId, Guid counterId, string? currentDeviceToken, CancellationToken cancellationToken)
    {
        await ManagedCounterAsync(businessId, counterId, cancellationToken).ConfigureAwait(false);
        var currentHash = currentDeviceToken is null ? null : SecretTokens.Hash(currentDeviceToken);
        var rows = await (
                from d in db.CounterDevices.AsNoTracking()
                join u in db.Users.AsNoTracking() on d.EnrolledByUserId equals u.Id
                where d.CounterId == counterId
                orderby d.EnrolledAtUtc descending
                select new { Device = d, EnrolledBy = u.DisplayName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => ToDto(r.Device, r.EnrolledBy, currentHash)).ToList();
    }

    /// <summary>
    /// Trusts this browser to bill on the counter. Any device this browser was enrolled as before is revoked, so one
    /// browser is one device. The token is returned once, for the API to put in an HttpOnly cookie.
    /// </summary>
    public async Task<EnrolDeviceResult> EnrolAsync(Guid businessId, Guid counterId, EnrolDeviceRequest request, string? previousToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var counter = await ManagedCounterAsync(businessId, counterId, cancellationToken).ConfigureAwait(false);
        if (!counter.IsActive)
        {
            throw AppException.Conflict("counter.inactive", "This counter is switched off. Switch it on before enrolling a device.");
        }

        var now = clock.GetUtcNow();
        if (previousToken is not null)
        {
            var previousHash = SecretTokens.Hash(previousToken);
            var previous = await db.CounterDevices.FirstOrDefaultAsync(d => d.TokenHash == previousHash && d.RevokedAtUtc == null, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                previous.Revoke(currentUser.UserId, now);
                audit.Record("counter.device_revoked", "counter_device", previous.Id, businessId, details: new { previous.Name, reason = "re-enrolled" });
            }
        }

        var token = SecretTokens.NewToken();
        var device = CounterDevice.Enrol(businessId, counter.Id, request.Name, SecretTokens.Hash(token), currentUser.UserId, now);
        db.CounterDevices.Add(device);
        audit.Record("counter.device_enrolled", "counter_device", device.Id, businessId, counter.StoreId, details: new { counter = counter.Code, device.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        var enrolledBy = await db.Users.AsNoTracking().Where(u => u.Id == currentUser.UserId).Select(u => u.DisplayName).FirstAsync(cancellationToken).ConfigureAwait(false);
        return new EnrolDeviceResult(ToDto(device, enrolledBy, device.TokenHash), token);
    }

    public async Task RevokeDeviceAsync(Guid businessId, Guid counterId, Guid deviceId, CancellationToken cancellationToken)
    {
        var counter = await ManagedCounterAsync(businessId, counterId, cancellationToken).ConfigureAwait(false);
        var device = await db.CounterDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.CounterId == counterId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Device");
        device.Revoke(currentUser.UserId, clock.GetUtcNow());
        audit.Record("counter.device_revoked", "counter_device", device.Id, businessId, counter.StoreId, details: new { counter = counter.Code, device.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The enrolled device behind a POS request, and the signed-in user's right to bill on it. Both are required:
    /// a device alone (no cashier) or a cashier on an unenrolled browser cannot bill.
    /// </summary>
    public async Task<PosDevice> RequireDeviceAsync(string? deviceToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(deviceToken) || deviceToken.Length > 100)
        {
            throw NotEnrolled();
        }

        var hash = SecretTokens.Hash(deviceToken);
        var row = await (
                from d in db.CounterDevices
                join c in db.Counters on d.CounterId equals c.Id
                join s in db.Stores on c.StoreId equals s.Id
                join b in db.Businesses on c.BusinessId equals b.Id
                where d.TokenHash == hash
                select new { Device = d, Counter = c, Store = s, Business = b })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null || !row.Device.IsActive)
        {
            throw NotEnrolled();
        }

        if (!row.Counter.IsActive || !row.Store.IsActive || !row.Business.IsActive)
        {
            throw AppException.Conflict("counter.inactive", $"Counter {row.Counter.Code} is switched off. Ask a manager.");
        }

        if (!await access.HasPermissionAsync(Permissions.PosBill, row.Counter.BusinessId, row.Counter.StoreId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Forbidden($"You are not allowed to bill in {row.Store.Name}.");
        }

        return new PosDevice(row.Device, row.Counter, row.Store, row.Business);
    }

    private static AppException NotEnrolled() =>
        AppException.Forbidden("This browser is not enrolled as a billing counter. A manager must enrol it under Counters first.");

    private async Task<Counter> ManagedCounterAsync(Guid businessId, Guid counterId, CancellationToken cancellationToken)
    {
        var counter = await db.Counters.FirstOrDefaultAsync(c => c.Id == counterId && c.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Counter");
        await organisation.RequireAsync(Permissions.CountersManage, businessId, counter.StoreId, cancellationToken).ConfigureAwait(false);
        return counter;
    }

    private async Task<List<CounterDto>> ToDtosAsync(List<Counter> counters, CancellationToken cancellationToken)
    {
        var ids = counters.Select(c => c.Id).ToList();
        var devices = await db.CounterDevices.AsNoTracking().Where(d => ids.Contains(d.CounterId) && d.RevokedAtUtc == null)
            .GroupBy(d => d.CounterId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var index = counters.Count == 0 ? 0
            : (await BillingService.RegistrationInForceAsync(db, counters[0].BusinessId, Catalog.BusinessCalendar.Today(clock), cancellationToken).ConfigureAwait(false)).Index;
        var series = counters.Select(c => Counter.InvoiceSeries(c.InvoicePrefix(index))).ToList();
        var sequences = await db.DocumentSequences.AsNoTracking().Where(s => series.Contains(s.Series))
            .Select(s => new { s.StoreId, s.Series, s.NextNumber }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return counters.Select(c =>
            {
                var prefix = c.InvoicePrefix(index);
                var next = sequences.FirstOrDefault(s => s.StoreId == c.StoreId && s.Series == Counter.InvoiceSeries(prefix))?.NextNumber ?? 1;
                return new CounterDto(c.Id, c.StoreId, c.Code, c.Name, c.IsActive, devices.FirstOrDefault(d => d.Key == c.Id)?.Count ?? 0,
                    Counter.InvoiceNumber(prefix, next), c.RowVersion);
            })
            .ToList();
    }

    private static CounterDeviceDto ToDto(CounterDevice d, string enrolledBy, byte[]? currentHash) =>
        new(d.Id, d.Name, enrolledBy, d.EnrolledAtUtc, d.LastSeenAtUtc, d.RevokedAtUtc,
            currentHash is not null && d.IsActive && SecretTokens.FixedTimeEquals(d.TokenHash, currentHash), d.OfflineMaxBills, d.OfflineMaxAmount, d.OfflineMaxHours);
}
