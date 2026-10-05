using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Accounts;

/// <summary>
/// Offline collections (spec section 16). A manager enrols a collector's phone (device identity, an offline amount
/// limit, a maximum offline time). Collections recorded on it without signal reach the server in the phone's order: each
/// is posted exactly as an online field collection would be (same rules, same round, idempotent on the phone's id), or,
/// when it cannot be (too old, over the limit, the round already handed over, not the collector's party...), kept in
/// quarantine for a manager to decide. Receipts, and messages to the party, exist only once the server has accepted.
/// </summary>
public sealed class OfflineCollectionService(
    SupermarketBillingDbContext db,
    FieldCollectionService field,
    DebtorReceiptService receipts,
    OrganisationService organisation,
    IAccessControl access,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const string IdempotencyPrefix = "offline-";
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    // Devices

    public async Task<IReadOnlyList<CollectionDeviceDto>> DevicesAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var devices = await db.CollectionDevices.AsNoTracking().Where(d => d.BusinessId == businessId).OrderByDescending(d => d.EnrolledAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(devices.SelectMany(d => new[] { d.CollectorUserId, d.EnrolledByUserId }), cancellationToken).ConfigureAwait(false);
        return devices.Select(d => Dto(d, names)).ToList();
    }

    /// <summary>Enrols the browser this is called from (the collector's phone) for the collector. The token goes only into an HttpOnly cookie.</summary>
    public async Task<(CollectionDeviceDto Device, string Token)> EnrolAsync(Guid businessId, EnrolCollectionDeviceRequest request, string? previousToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var collectors = Roles.All.Where(r => r.Permissions.Contains(Permissions.CollectionsCollect)).Select(r => r.Code).ToList();
        if (!await db.RoleAssignments.AnyAsync(a => a.UserId == request.CollectorUserId && a.BusinessId == businessId && a.RevokedAtUtc == null && collectors.Contains(a.RoleCode),
                cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Validation("device.not_a_collector", "Enrol the phone for someone who collects in this business.");
        }

        var now = clock.GetUtcNow();
        if (previousToken is not null)
        {
            var hash = SecretTokens.Hash(previousToken);
            var previous = await db.CollectionDevices.FirstOrDefaultAsync(d => d.TokenHash == hash && d.RevokedAtUtc == null, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                previous.Revoke(currentUser.UserId, now);
                audit.Record("collection.device_revoked", "collection_device", previous.Id, businessId, details: new { previous.Name, reason = "re-enrolled" });
            }
        }

        var token = SecretTokens.NewToken();
        var device = Valid(() => CollectionDevice.Enrol(businessId, request.CollectorUserId, request.Name, SecretTokens.Hash(token), request.OfflineLimit, request.MaxOfflineHours,
            currentUser.UserId, now));
        db.CollectionDevices.Add(device);
        audit.Record("collection.device_enrolled", "collection_device", device.Id, businessId,
            details: new { device.Name, collector = request.CollectorUserId, device.OfflineLimit, device.MaxOfflineHours });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync([device.CollectorUserId, device.EnrolledByUserId], cancellationToken).ConfigureAwait(false);
        return (Dto(device, names), token);
    }

    public async Task<CollectionDeviceDto> SetLimitsAsync(Guid businessId, Guid deviceId, CollectionDeviceLimitsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var device = await db.CollectionDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Phone");
        db.Entry(device).Property(d => d.RowVersion).OriginalValue = request.RowVersion;
        Valid(() => { device.SetLimits(request.OfflineLimit, request.MaxOfflineHours); return device; });
        audit.Record("collection.device_limits", "collection_device", device.Id, businessId, details: new { device.OfflineLimit, device.MaxOfflineHours });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return Dto(device, await NamesAsync([device.CollectorUserId, device.EnrolledByUserId], cancellationToken).ConfigureAwait(false));
    }

    public async Task RevokeAsync(Guid businessId, Guid deviceId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var device = await db.CollectionDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Phone");
        device.Revoke(currentUser.UserId, clock.GetUtcNow());
        audit.Record("collection.device_revoked", "collection_device", device.Id, businessId, details: new { device.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The phone this browser is, when it is enrolled for the signed-in collector (what the app keeps to work offline).</summary>
    public async Task<MyCollectionDeviceDto?> MineAsync(string? token, CancellationToken cancellationToken)
    {
        var device = await DeviceAsync(token, cancellationToken).ConfigureAwait(false);
        return device is null || device.CollectorUserId != currentUser.UserId
            ? null
            : new MyCollectionDeviceDto(device.Id, device.BusinessId, device.CollectorUserId, device.Name, device.OfflineLimit, device.MaxOfflineHours, device.LastSequence);
    }

    // Synchronisation

    /// <summary>
    /// Receives the phone's collections in order. One synchronisation per phone at a time (a lock held for the whole
    /// request); each collection is posted or quarantined, and the phone's last sequence moves on with it. A resend
    /// returns the first result; a gap stops processing so the phone sends what is missing first.
    /// </summary>
    public async Task<OfflineSyncResponse> SyncAsync(string? token, OfflineSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Items);
        var device = await DeviceAsync(token, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.Forbidden("This phone is not enrolled for offline collection.");
        if (device.CollectorUserId != currentUser.UserId)
        {
            throw AppException.Forbidden("This phone is enrolled for another collector.");
        }

        if (request.Items.Count > 500)
        {
            throw AppException.Validation("offline.too_many", "Send at most 500 collections at a time.");
        }

        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock(hashtext({"offline-device|" + device.Id}))", cancellationToken).ConfigureAwait(false);
            try
            {
                return await SyncLockedAsync(device.Id, request.Items.OrderBy(i => i.Sequence).ToList(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtext({"offline-device|" + device.Id}))", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<OfflineSyncResponse> SyncLockedAsync(Guid deviceId, List<OfflineItemRequest> items, CancellationToken cancellationToken)
    {
        var results = new List<OfflineItemResult>();
        var held = 0m;
        var stopped = false;
        foreach (var item in items)
        {
            db.ChangeTracker.Clear();
            var device = await db.CollectionDevices.FirstAsync(d => d.Id == deviceId, cancellationToken).ConfigureAwait(false);
            var hash = Hash(item);
            var known = await db.OfflineSubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == item.Id, cancellationToken).ConfigureAwait(false);
            if (known is not null)
            {
                results.Add(known.PayloadHash == hash
                    ? await ResultAsync(known, "DUPLICATE", cancellationToken).ConfigureAwait(false)
                    : new OfflineItemResult(item.Id, item.Sequence, "REJECTED", "Sent before with different details.", null, null));
                continue;
            }

            if (stopped || item.Sequence > device.LastSequence + 1)
            {
                stopped = true;
                results.Add(new OfflineItemResult(item.Id, item.Sequence, "NOT_PROCESSED", $"Collection {device.LastSequence + 1} must come first.", null, null));
                continue;
            }

            if (item.Sequence <= device.LastSequence)
            {
                results.Add(new OfflineItemResult(item.Id, item.Sequence, "REJECTED", "This sequence number was already used by another collection.", null, null));
                continue;
            }

            var offlineItem = new OfflineSubmission.Item(item.Id, item.Sequence, item.DebtorId, item.Method, item.Amount, Trim(item.Reference, 40), Trim(item.Note, 200),
                Trim(item.BankName, 60), item.ChequeDate, item.RecordedAtUtc);
            var now = clock.GetUtcNow();
            var reason = OfflineRules.Check(device, offlineItem, held, now);
            DebtorReceiptDto? receipt = null;
            if (reason is null && !await db.Debtors.AnyAsync(d => d.Id == item.DebtorId && d.BusinessId == device.BusinessId, cancellationToken).ConfigureAwait(false))
            {
                reason = "The party was not found.";
            }

            if (reason is null)
            {
                try
                {
                    // Exactly as an online field collection, in the collector's open round; the phone's id makes it idempotent.
                    receipt = await field.CollectAsync(device.BusinessId, new FieldCollectionRequest(item.DebtorId, item.Method, item.Amount, offlineItem.Reference,
                        offlineItem.BankName, item.ChequeDate, offlineItem.Note, null, IdempotencyPrefix + item.Id.ToString("N")), cancellationToken).ConfigureAwait(false);
                }
                catch (AppException e) when (e.Kind is ErrorKind.Validation or ErrorKind.Conflict or ErrorKind.Forbidden or ErrorKind.NotFound)
                {
                    reason = e.Message;
                }

                db.ChangeTracker.Clear();
                device = await db.CollectionDevices.FirstAsync(d => d.Id == deviceId, cancellationToken).ConfigureAwait(false);
            }

            held += item.Amount;
            var submission = OfflineSubmission.Receive(device.BusinessId, device, offlineItem, hash, receipt?.Id, reason is null ? null : Trim(reason, 300), now);
            device.Received(item.Sequence, now);
            db.OfflineSubmissions.Add(submission);
            audit.Record(reason is null ? "collection.offline_accepted" : "collection.offline_quarantined", "offline_submission", submission.Id, device.BusinessId,
                details: new { device = device.Name, item.Sequence, item.Amount, receipt = receipt?.Number, reason });
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            results.Add(new OfflineItemResult(item.Id, item.Sequence, submission.Status, submission.Reason, receipt?.Number, receipt?.BalanceAfter));
        }

        var last = await db.CollectionDevices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.LastSequence).FirstAsync(cancellationToken).ConfigureAwait(false);
        return new OfflineSyncResponse(results, last);
    }

    // Quarantine

    public async Task<IReadOnlyList<OfflineSubmissionDto>> SubmissionsAsync(Guid businessId, string? status, Guid? collectorUserId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false))
        {
            // A collector sees their own.
            await RequireAsync(Permissions.CollectionsCollect, businessId, cancellationToken).ConfigureAwait(false);
            collectorUserId = currentUser.UserId;
        }

        var query = db.OfflineSubmissions.AsNoTracking().Where(s => s.BusinessId == businessId);
        query = string.IsNullOrWhiteSpace(status) ? query : query.Where(s => s.Status == status);
        query = collectorUserId is { } c ? query.Where(s => s.CollectorUserId == c) : query;
        var list = await query.OrderByDescending(s => s.ReceivedAtUtc).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await DtosAsync(list, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A manager decides a quarantined collection: posted as an office receipt at a store (the money was handed in), or not
    /// (for example it went back to the party). Never by the collector.
    /// </summary>
    public async Task<OfflineSubmissionDto> ResolveAsync(Guid businessId, Guid submissionId, ResolveOfflineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CollectionsManage, businessId, cancellationToken).ConfigureAwait(false);
        var submission = await db.OfflineSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId && s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Offline collection");
        if (submission.RowVersion != request.RowVersion)
        {
            throw AppException.Conflict("concurrency.conflict", "This record was changed by someone else. Reload it and try again.");
        }

        // Checked before any receipt is posted.
        Valid(() => { new ResolutionCheck(submission).Run(request.Note, currentUser.UserId); return submission; });
        Guid? receiptId = null;
        if (request.Accept)
        {
            var store = request.StoreId ?? throw AppException.Validation("offline.store_required", "Choose the store the money was handed in at.");
            var receipt = await receipts.CreateAsync(businessId, new DebtorReceiptRequest(submission.DebtorId, submission.Method, submission.Amount, store, submission.Reference,
                $"Offline collection {submission.Sequence} quarantined: {submission.Reason}", null, IdempotencyPrefix + submission.Id.ToString("N"), submission.BankName,
                submission.ChequeDate), cancellationToken).ConfigureAwait(false);
            receiptId = receipt.Id;
            db.ChangeTracker.Clear();
            submission = await db.OfflineSubmissions.FirstAsync(s => s.Id == submissionId, cancellationToken).ConfigureAwait(false);
        }

        Valid(() => { submission.Resolve(receiptId, request.Note, currentUser.UserId, clock.GetUtcNow()); return submission; });
        audit.Record("collection.offline_resolved", "offline_submission", submission.Id, businessId, details: new { request.Accept, request.Note, receiptId });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await DtosAsync([submission], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>Runs the resolution rules on a copy, so nothing is posted when they fail.</summary>
    private sealed class ResolutionCheck(OfflineSubmission submission)
    {
        public void Run(string note, Guid userId)
        {
            if (submission.Status != OfflineStatus.Quarantined)
            {
                throw new DomainException("offline.not_quarantined", "Only a quarantined collection is resolved, once.");
            }

            if (userId == submission.CollectorUserId)
            {
                throw new DomainException("offline.self_resolve", "Someone other than the collector decides their quarantined collections.");
            }

            if (string.IsNullOrWhiteSpace(note) || note.Trim().Length > 300)
            {
                throw new DomainException("offline.note_required", "Say what was decided and why.");
            }
        }
    }

    // Helpers

    private async Task<CollectionDevice?> DeviceAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = SecretTokens.Hash(token);
        return await db.CollectionDevices.AsNoTracking().FirstOrDefaultAsync(d => d.TokenHash == hash && d.RevokedAtUtc == null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OfflineItemResult> ResultAsync(OfflineSubmission s, string status, CancellationToken cancellationToken)
    {
        var receipt = s.ReceiptId is { } id
            ? await db.DebtorReceipts.AsNoTracking().Where(r => r.Id == id).Select(r => r.Number).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return new OfflineItemResult(s.Id, s.Sequence, status, s.Reason, receipt, null);
    }

    private async Task<List<OfflineSubmissionDto>> DtosAsync(List<OfflineSubmission> list, CancellationToken cancellationToken)
    {
        var deviceIds = list.Select(s => s.DeviceId).Distinct().ToList();
        var devices = await db.CollectionDevices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken).ConfigureAwait(false);
        var debtorIds = list.Select(s => s.DebtorId).Distinct().ToList();
        var debtors = (await db.Debtors.AsNoTracking().Where(d => debtorIds.Contains(d.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => $"{d.Code} - {d.DisplayName}");
        var receiptIds = list.Where(s => s.ReceiptId != null).Select(s => s.ReceiptId!.Value).ToList();
        var numbers = await db.DebtorReceipts.AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(list.Select(s => s.CollectorUserId).Concat(list.Where(s => s.ResolvedByUserId != null).Select(s => s.ResolvedByUserId!.Value)), cancellationToken)
            .ConfigureAwait(false);
        return list.Select(s => new OfflineSubmissionDto(s.Id, s.DeviceId, devices.GetValueOrDefault(s.DeviceId, "?"), s.Sequence, s.CollectorUserId,
            names.GetValueOrDefault(s.CollectorUserId, "?"), s.DebtorId, debtors.GetValueOrDefault(s.DebtorId, "?"), s.Method, s.Amount, s.Reference, s.Note, s.RecordedAtUtc,
            s.ReceivedAtUtc, s.Status, s.Reason, s.ReceiptId is { } r ? numbers.GetValueOrDefault(r) : null, s.ResolvedByUserId is { } u ? names.GetValueOrDefault(u) : null,
            s.ResolvedAtUtc, s.ResolutionNote, s.RowVersion)).ToList();
    }

    private static CollectionDeviceDto Dto(CollectionDevice d, Dictionary<Guid, string> names) =>
        new(d.Id, d.CollectorUserId, names.GetValueOrDefault(d.CollectorUserId, "?"), d.Name, d.OfflineLimit, d.MaxOfflineHours, d.LastSequence,
            names.GetValueOrDefault(d.EnrolledByUserId, "?"), d.EnrolledAtUtc, d.LastSyncedAtUtc, d.IsActive, d.RowVersion);

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    private static string Hash(OfflineItemRequest item) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, HashJson))));

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var v && v.Length > max ? v[..max] : value.Trim();

    private async Task<bool> CanAsync(string permission, Guid businessId, CancellationToken cancellationToken) =>
        (await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false)).Contains(businessId);

    private async Task RequireAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(permission, businessId, cancellationToken).ConfigureAwait(false))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static T Valid<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }
    }
}
