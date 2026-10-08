using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Archiving;
using SupermarketBilling.Domain.Archiving;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Tenancy;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;
using SupermarketBilling.Infrastructure.Tenancy;

namespace SupermarketBilling.Infrastructure.Archiving;

/// <summary>
/// The archive server (spec section 22, D-042): its owner administrator and named users with archive roles limited by
/// business, store, financial year and report; the store servers it trusts; importing their monthly packages (only
/// from a trusted server, verified by signature, checksums, counts and totals, recomputed again from what was stored,
/// idempotently); and the accountant's then the owner's approval of each imported month.
/// </summary>
public sealed class ArchiveServerService(
    SupermarketBillingDbContext db,
    TenantContext tenant,
    SetupCodeStore setupCode,
    PasswordHashing passwords,
    AuthService auth,
    ArchiveRecipientKey recipientKey,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> security,
    TimeProvider clock)
{
    private const long SetupLockKey = 7_311_2026_0014;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Setup

    /// <summary>Creates the archive's company and its owner administrator, once, with the setup code from the server.</summary>
    public async Task SetupAsync(ArchiveSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({SetupLockKey})", cancellationToken).ConfigureAwait(false);
        var installation = await db.Installation.FirstAsync(i => i.Id == Installation.SingletonId, cancellationToken).ConfigureAwait(false);
        if (installation.TenantId is not null)
        {
            throw AppException.Conflict("setup.already_completed", "Initial setup has already been completed.");
        }

        if (!setupCode.Matches(request.SetupCode))
        {
            throw new AppException(ErrorKind.Forbidden, "setup.code_invalid", "The setup code is not correct. It is in the setup-code file on the archive server.");
        }

        var username = User.NormalizeUsername(request.OwnerUsername);
        AuthService.EnsurePasswordPolicy(request.OwnerPassword, username);
        var company = Tenant.Create(request.CompanyCode, request.CompanyName, now);
        await tenant.SetAsync(company.Id, db, cancellationToken).ConfigureAwait(false);
        db.Tenants.Add(company);
        installation.AssignTenant(company.Id);
        var owner = User.Create(username, request.OwnerDisplayName, passwords.Hash(request.OwnerPassword), now, mustChangePassword: false);
        db.Users.Add(owner);
        var grant = ArchiveGrant.Grant(owner.Id, ArchiveRoles.Owner, null, null, null, null, null, now);
        db.ArchiveGrants.Add(grant);
        audit.Record("archive.setup_completed", "tenant", company.Id, details: new { company = company.Code, owner = owner.Username }, actorUserId: owner.Id);
        audit.Record("archive.role_granted", "archive_grant", grant.Id, details: new { user = owner.Username, role = ArchiveRoles.Owner, via = "initial_setup" }, actorUserId: owner.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        setupCode.Delete();
    }

    // Who may do what

    private async Task<List<ArchiveGrant>> GrantsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.ArchiveGrants.AsNoTracking().Where(g => g.UserId == userId && g.RevokedAtUtc == null).ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<List<ArchiveGrant>> MyGrantsAsync(CancellationToken cancellationToken) => await GrantsAsync(currentUser.UserId, cancellationToken).ConfigureAwait(false);

    /// <summary>The permission for a business (null: a permission that is not about any one business, needing an unlimited grant).</summary>
    private async Task RequireAsync(string permission, Guid? businessId, CancellationToken cancellationToken)
    {
        var grants = await MyGrantsAsync(cancellationToken).ConfigureAwait(false);
        var allowed = businessId is { } b ? grants.Any(g => g.Covers(permission, b)) : grants.Any(g => g.BusinessId is null && g.Covers(permission, null));
        if (!allowed)
        {
            throw AppException.Forbidden();
        }
    }

    public async Task<ArchiveMeDto> MeAsync(CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == currentUser.UserId, cancellationToken).ConfigureAwait(false);
        var grants = await MyGrantsAsync(cancellationToken).ConfigureAwait(false);
        return new ArchiveMeDto(user.Id, user.Username, user.DisplayName, await GrantDtosAsync(grants, cancellationToken).ConfigureAwait(false),
            grants.SelectMany(g => ArchiveRoles.Get(g.RoleCode).Permissions).Distinct().Order(StringComparer.Ordinal).ToList());
    }

    public static IReadOnlyList<ArchiveRoleDto> Roles() =>
        ArchiveRoles.All.Select(r => new ArchiveRoleDto(r.Code, r.Name, r.Permissions.Order(StringComparer.Ordinal).ToList())).ToList();

    // Users

    public async Task<IReadOnlyList<ArchiveUserDto>> UsersAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(ArchivePermissions.UsersView, null, cancellationToken).ConfigureAwait(false);
        var ids = await db.ArchiveGrants.AsNoTracking().Select(g => g.UserId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).OrderBy(u => u.Username).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ArchiveUserDto>();
        foreach (var user in users)
        {
            result.Add(await UserDtoAsync(user, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<ArchiveUserDto> CreateUserAsync(CreateArchiveUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Grant);
        await RequireAsync(ArchivePermissions.UsersManage, null, cancellationToken).ConfigureAwait(false);
        var username = User.NormalizeUsername(request.Username);
        AuthService.EnsurePasswordPolicy(request.TemporaryPassword, username);
        var now = clock.GetUtcNow();
        var user = User.Create(username, request.DisplayName, passwords.Hash(request.TemporaryPassword), now, mustChangePassword: true);
        db.Users.Add(user);
        var grant = await NewGrantAsync(user.Id, request.Grant, now, cancellationToken).ConfigureAwait(false);
        audit.Record("archive.user_created", "user", user.Id, details: new { user.Username, user.DisplayName });
        audit.Record("archive.role_granted", "archive_grant", grant.Id, details: new { user = user.Username, role = grant.RoleCode, grant.BusinessId, grant.StoreId, grant.FinancialYear, grant.Reports });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await UserDtoAsync(user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArchiveUserDto> GrantAsync(Guid userId, ArchiveGrantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(ArchivePermissions.UsersManage, null, cancellationToken).ConfigureAwait(false);
        if (userId == currentUser.UserId)
        {
            throw AppException.Forbidden("You cannot change your own roles.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("User");
        var grant = await NewGrantAsync(user.Id, request, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        audit.Record("archive.role_granted", "archive_grant", grant.Id, details: new { user = user.Username, role = grant.RoleCode, grant.BusinessId, grant.StoreId, grant.FinancialYear, grant.Reports });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await UserDtoAsync(user, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArchiveGrant> NewGrantAsync(Guid userId, ArchiveGrantRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (request.BusinessId is { } business && !await db.ArchiveImports.AnyAsync(i => i.BusinessId == business, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Archived business");
        }

        if (request.StoreId is { } store && !await db.ArchiveMasters.AnyAsync(m => m.BusinessId == request.BusinessId && m.Dataset == "stores" && m.RecordId == store.ToString(),
                cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Archived store");
        }

        var grant = Valid(() => ArchiveGrant.Grant(userId, request.RoleCode, request.BusinessId, request.StoreId, request.FinancialYear, request.Reports, currentUser.UserId, now));
        db.ArchiveGrants.Add(grant);
        return grant;
    }

    public async Task RevokeAsync(Guid userId, Guid grantId, CancellationToken cancellationToken)
    {
        await RequireAsync(ArchivePermissions.UsersManage, null, cancellationToken).ConfigureAwait(false);
        if (userId == currentUser.UserId)
        {
            throw AppException.Forbidden("You cannot change your own roles.");
        }

        var grant = await db.ArchiveGrants.FirstOrDefaultAsync(g => g.Id == grantId && g.UserId == userId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Archive role");
        if (grant.RoleCode == ArchiveRoles.Owner)
        {
            await EnsureAnotherOwnerAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        Valid(() => { grant.Revoke(currentUser.UserId, clock.GetUtcNow()); return grant; });
        audit.Record("archive.role_revoked", "archive_grant", grant.Id, details: new { userId, role = grant.RoleCode });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArchiveUserDto> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken)
    {
        await RequireAsync(ArchivePermissions.UsersManage, null, cancellationToken).ConfigureAwait(false);
        if (userId == currentUser.UserId)
        {
            throw AppException.Forbidden("You cannot disable your own account.");
        }

        var user = await ArchiveUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (!isActive)
        {
            if (await db.ArchiveGrants.AnyAsync(g => g.UserId == userId && g.RoleCode == ArchiveRoles.Owner && g.RevokedAtUtc == null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureAnotherOwnerAsync(userId, cancellationToken).ConfigureAwait(false);
            }

            await auth.RevokeSessionsAsync(userId, now, "user_disabled", null, cancellationToken).ConfigureAwait(false);
        }

        user.SetActive(isActive);
        audit.Record(isActive ? "archive.user_enabled" : "archive.user_disabled", "user", user.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await UserDtoAsync(user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArchiveUserDto> UnlockAsync(Guid userId, CancellationToken cancellationToken)
    {
        await RequireAsync(ArchivePermissions.UsersUnlock, null, cancellationToken).ConfigureAwait(false);
        var user = await ArchiveUserAsync(userId, cancellationToken).ConfigureAwait(false);
        user.Unlock();
        audit.Record("archive.user_unlocked", "user", user.Id);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await UserDtoAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A one-time reset code, shown once to the administrator; only its hash is kept.</summary>
    public async Task<PasswordResetIssuedResponse> IssuePasswordResetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await RequireAsync(ArchivePermissions.UsersManage, null, cancellationToken).ConfigureAwait(false);
        if (userId == currentUser.UserId)
        {
            throw AppException.Validation("reset.self", "Use 'Change password' for your own account.");
        }

        var user = await ArchiveUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        foreach (var previous in await db.PasswordResetTokens.Where(t => t.UserId == userId && t.UsedAtUtc == null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            previous.MarkUsed(now);
        }

        var code = SecretTokens.NewHumanCode();
        var token = PasswordResetToken.Issue(userId, SecretTokens.HashHumanCode(code), currentUser.UserId, now, TimeSpan.FromMinutes(security.Value.PasswordResetMinutes));
        db.PasswordResetTokens.Add(token);
        audit.Record("archive.password_reset_issued", "user", user.Id, details: new { expires = token.ExpiresAtUtc });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new PasswordResetIssuedResponse(code, token.ExpiresAtUtc);
    }

    private async Task<User> ArchiveUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.ArchiveGrants.AnyAsync(g => g.UserId == userId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("User");
        }

        return await db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAnotherOwnerAsync(Guid excluding, CancellationToken cancellationToken)
    {
        var others = await db.ArchiveGrants.Where(g => g.RoleCode == ArchiveRoles.Owner && g.RevokedAtUtc == null && g.BusinessId == null && g.UserId != excluding)
            .Join(db.Users.Where(u => u.IsActive), g => g.UserId, u => u.Id, (g, _) => g)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (!others)
        {
            throw AppException.Conflict("owner.last", "This is the archive's last owner administrator. Add another first.");
        }
    }

    private async Task<ArchiveUserDto> UserDtoAsync(User user, CancellationToken cancellationToken) =>
        new(user.Id, user.Username, user.DisplayName, user.IsActive, user.IsLockedOut(clock.GetUtcNow()), user.MfaEnabled, user.MustChangePassword, user.LastLoginAtUtc,
            await GrantDtosAsync(await GrantsAsync(user.Id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));

    private async Task<IReadOnlyList<ArchiveGrantDto>> GrantDtosAsync(IReadOnlyList<ArchiveGrant> grants, CancellationToken cancellationToken)
    {
        var businessIds = grants.Where(g => g.BusinessId != null).Select(g => g.BusinessId!.Value).Distinct().ToList();
        var names = await db.ArchiveImports.AsNoTracking().Where(i => businessIds.Contains(i.BusinessId)).GroupBy(i => i.BusinessId)
            .Select(g => new { g.Key, Name = g.OrderByDescending(i => i.Month).Select(i => i.BusinessName).First() })
            .ToDictionaryAsync(x => x.Key, x => x.Name, cancellationToken).ConfigureAwait(false);
        var storeIds = grants.Where(g => g.StoreId != null).Select(g => g.StoreId!.Value.ToString()).ToList();
        var stores = (await db.ArchiveMasters.AsNoTracking().Where(m => m.Dataset == "stores" && storeIds.Contains(m.RecordId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(m => m.RecordId, m => JsonDocument.Parse(m.Data).RootElement.GetProperty("name").GetString());
        return grants.OrderBy(g => g.RoleCode, StringComparer.Ordinal).Select(g => new ArchiveGrantDto(
            g.Id, g.RoleCode, ArchiveRoles.Get(g.RoleCode).Name, g.BusinessId, g.BusinessId is { } b ? names.GetValueOrDefault(b) : null, g.StoreId,
            g.StoreId is { } s ? stores.GetValueOrDefault(s.ToString()) : null, g.FinancialYear, g.FinancialYear is { } y ? FinancialYears.Label(y) : null,
            g.Reports?.Split(','))).ToList();
    }

    // Keys and trusted store servers

    public async Task<ArchiveServerKeysDto> KeysAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(ArchivePermissions.SourcesView, null, cancellationToken).ConfigureAwait(false);
        return new ArchiveServerKeysDto(recipientKey.KeyId(), recipientKey.PublicKeyPem());
    }

    public async Task<IReadOnlyList<ArchiveSourceDto>> SourcesAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(ArchivePermissions.SourcesView, null, cancellationToken).ConfigureAwait(false);
        return await (
                from s in db.ArchiveSources.AsNoTracking()
                join u in db.Users.AsNoTracking() on s.RegisteredByUserId equals u.Id
                orderby s.RegisteredAtUtc
                select new ArchiveSourceDto(s.Id, s.Name, s.KeyId, u.DisplayName, s.RegisteredAtUtc, s.RevokedAtUtc == null,
                    db.ArchiveImports.Count(i => i.SourceId == s.Id)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Trusts a store server's packages: its signing public key, copied from its Month close screen.</summary>
    public async Task<ArchiveSourceDto> RegisterSourceAsync(RegisterArchiveSourceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(ArchivePermissions.SourcesManage, null, cancellationToken).ConfigureAwait(false);
        string pem;
        string keyId;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(request.PublicKeyPem ?? string.Empty);
            if (key.KeySize != 256)
            {
                throw new CryptographicException("not a P-256 key");
            }

            pem = key.ExportSubjectPublicKeyInfoPem();
            keyId = ArchivePackage.KeyId(key.ExportSubjectPublicKeyInfo());
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            throw AppException.Validation("archive.key_invalid", "Paste the store server's public key exactly as its Month close screen shows it (-----BEGIN PUBLIC KEY----- ...).");
        }

        if (await db.ArchiveSources.AnyAsync(s => s.KeyId == keyId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("archive_source.exists", "This store server is already registered.");
        }

        var source = Valid(() => ArchiveSource.Register(request.Name, keyId, pem, currentUser.UserId, clock.GetUtcNow()));
        db.ArchiveSources.Add(source);
        audit.Record("archive.source_registered", "archive_source", source.Id, details: new { source.Name, keyId });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await SourcesAsync(cancellationToken).ConfigureAwait(false)).First(s => s.Id == source.Id);
    }

    public async Task RevokeSourceAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await RequireAsync(ArchivePermissions.SourcesManage, null, cancellationToken).ConfigureAwait(false);
        var source = await db.ArchiveSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Store server");
        source.Revoke(currentUser.UserId, clock.GetUtcNow());
        audit.Record("archive.source_revoked", "archive_source", source.Id, details: new { source.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    // Imports

    /// <summary>
    /// Imports a package (spec steps 8 and 9): only from a trusted store server, after verifying its signature, every
    /// checksum, count and total; then the stored records are counted and totalled again. Importing the same package or
    /// an identical month again adds nothing; a different package for a month already archived is refused.
    /// </summary>
    public async Task<ArchiveImportDto> ImportAsync(byte[] file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArchiveHeader header;
        try
        {
            header = ArchivePackage.ReadHeader(file);
        }
        catch (ArchivePackageException e)
        {
            throw AppException.Validation("archive.package_invalid", e.Message);
        }

        await RequireAsync(ArchivePermissions.Import, header.BusinessId, cancellationToken).ConfigureAwait(false);
        var source = await db.ArchiveSources.AsNoTracking().FirstOrDefaultAsync(s => s.KeyId == header.SignerKeyId && s.RevokedAtUtc == null, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.Conflict("archive.source_unknown",
                $"This package comes from a store server this archive does not trust (key {header.SignerKeyId}). Register the store server first.");

        ArchiveContents contents;
        try
        {
            using var signer = ECDsa.Create();
            signer.ImportFromPem(source.PublicKeyPem);
            using var archive = recipientKey.Create();
            contents = ArchivePackage.Open(file, archive, id => id == source.KeyId ? signer : null);
        }
        catch (ArchivePackageException e)
        {
            throw AppException.Validation("archive.package_invalid", e.Message);
        }

        var manifest = contents.Manifest;
        var month = Months.Parse(manifest.Month);
        var fileSha = ArchivePackage.FileSha256(file);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"archive-import|" + manifest.BusinessId + "|" + manifest.Month}))", cancellationToken)
            .ConfigureAwait(false);

        var same = await db.ArchiveImports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == manifest.PackageId, cancellationToken).ConfigureAwait(false);
        if (same is not null)
        {
            return same.FileSha256 == fileSha
                ? await DtoAsync(same, alreadyImported: true, cancellationToken).ConfigureAwait(false)
                : throw AppException.Conflict("archive.package_changed", "A package with this id was imported before with different contents.");
        }

        var existing = await db.ArchiveImports.AsNoTracking().FirstOrDefaultAsync(i => i.BusinessId == manifest.BusinessId && i.Month == month, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            // A locked month never changes, so a later package of it must hold the same records (masters may be newer).
            var archived = JsonSerializer.Deserialize<ArchiveManifest>(existing.Manifest, Json)!;
            var differs = manifest.Datasets.Where(d => !ArchiveDatasets.IsMaster(d.Name))
                .Any(d => archived.Datasets.FirstOrDefault(a => a.Name == d.Name)?.Sha256 != d.Sha256);
            return differs
                ? throw AppException.Conflict("archive.month_differs",
                    $"{manifest.BusinessCode} {manifest.Month} is already archived with different records. A locked month cannot change; check which package is right.")
                : await DtoAsync(existing, alreadyImported: true, cancellationToken).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        var import = ArchiveImport.Record(manifest.PackageId, manifest.BusinessId, manifest.BusinessCode, manifest.BusinessName, month, source.Id, fileSha, file.LongLength,
            JsonSerializer.Serialize(manifest, Json), "{}", currentUser.UserId, now);
        db.ArchiveImports.Add(import);
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (dataset, rows) in contents.Datasets)
        {
            if (ArchiveDatasets.IsMaster(dataset))
            {
                var current = await db.ArchiveMasters.Where(m => m.BusinessId == manifest.BusinessId && m.Dataset == dataset)
                    .ToDictionaryAsync(m => m.RecordId, cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    var id = RecordId(dataset, row);
                    if (current.TryGetValue(id, out var master))
                    {
                        master.Refresh(row.GetRawText(), import.Id, month);
                    }
                    else
                    {
                        db.ArchiveMasters.Add(ArchiveMaster.Create(manifest.BusinessId, dataset, id, row.GetRawText(), import.Id, month));
                    }
                }
            }
            else
            {
                foreach (var row in rows)
                {
                    db.ArchiveRecords.Add(ArchiveRecord.Create(import.Id, manifest.BusinessId, month, dataset, RecordId(dataset, row), row.GetRawText(), now));
                }
            }

            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }

        // Verified again from what the archive now holds: every month dataset's count and totals.
        var verification = await VerifyStoredAsync(import.Id, manifest, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"UPDATE archive_imports SET verification = {verification}::jsonb WHERE id = {import.Id}", cancellationToken).ConfigureAwait(false);
        audit.Record("archive.imported", "archive_import", import.Id, manifest.BusinessId, details: new
        {
            business = manifest.BusinessCode,
            month = manifest.Month,
            fileSha,
            source = source.Name,
            records = manifest.Datasets.Sum(d => d.Records),
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await DtoAsync(await db.ArchiveImports.AsNoTracking().FirstAsync(i => i.Id == import.Id, cancellationToken).ConfigureAwait(false), false, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string RecordId(string dataset, JsonElement row)
    {
        if (row.TryGetProperty("id", out var id))
        {
            return id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
        }

        throw AppException.Validation("archive.record_without_id", $"A record of {dataset} has no id.");
    }

    private async Task<string> VerifyStoredAsync(Guid importId, ArchiveManifest manifest, CancellationToken cancellationToken)
    {
        var checks = new List<object>();
        foreach (var dataset in manifest.Datasets.Where(d => !ArchiveDatasets.IsMaster(d.Name)))
        {
            var name = dataset.Name;
            var count = (await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM archive_records WHERE import_id = {importId} AND dataset = {name}")
                .ToListAsync(cancellationToken).ConfigureAwait(false)).Single();
            if (count != dataset.Records)
            {
                throw new InvalidOperationException($"Stored {count} records of {name}, the package had {dataset.Records}.");
            }

            var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var (column, expected) in dataset.Totals)
            {
                var stored = (await db.Database.SqlQuery<decimal>(
                        $"SELECT coalesce(sum((data ->> {column})::numeric), 0) AS \"Value\" FROM archive_records WHERE import_id = {importId} AND dataset = {name}")
                    .ToListAsync(cancellationToken).ConfigureAwait(false)).Single();
                if (stored != expected)
                {
                    throw new InvalidOperationException($"Stored {name} {column} adds up to {stored}, the package had {expected}.");
                }

                totals[column] = stored;
            }

            checks.Add(new { dataset = name, records = count, totals });
        }

        return JsonSerializer.Serialize(new { verifiedAtUtc = clock.GetUtcNow(), checks }, Json);
    }

    public async Task<IReadOnlyList<ArchiveImportDto>> ImportsAsync(CancellationToken cancellationToken)
    {
        var grants = await MyGrantsAsync(cancellationToken).ConfigureAwait(false);
        string[] viewing = [ArchivePermissions.Import, ArchivePermissions.ApproveAccounts, ArchivePermissions.ApproveOwner, ArchivePermissions.Reports];
        if (!grants.Any(g => viewing.Any(p => ArchiveRoles.Get(g.RoleCode).Permissions.Contains(p))))
        {
            throw AppException.Forbidden();
        }

        var imports = await db.ArchiveImports.AsNoTracking().OrderByDescending(i => i.Month).ThenBy(i => i.BusinessCode).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ArchiveImportDto>();
        foreach (var import in imports.Where(i => grants.Any(g => viewing.Any(p => g.Covers(p, i.BusinessId)))))
        {
            result.Add(await DtoAsync(import, false, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>The accountant's approval of an imported month, then the owner's (two different people).</summary>
    public async Task<ArchiveImportDto> ApproveAsync(Guid importId, bool asOwner, ArchiveApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var import = await db.ArchiveImports.FirstOrDefaultAsync(i => i.Id == importId, cancellationToken).ConfigureAwait(false) ?? throw AppException.NotFound("Import");
        await RequireAsync(asOwner ? ArchivePermissions.ApproveOwner : ArchivePermissions.ApproveAccounts, import.BusinessId, cancellationToken).ConfigureAwait(false);
        if (import.RowVersion != request.RowVersion)
        {
            throw AppException.Conflict("concurrency", "This import was changed by someone else. Reload it and try again.");
        }

        var now = clock.GetUtcNow();
        Valid(() =>
        {
            if (asOwner)
            {
                import.ApproveAsOwner(currentUser.UserId, request.Note, now);
            }
            else
            {
                import.ApproveAsAccountant(currentUser.UserId, request.Note, now);
            }

            return import;
        });
        audit.Record(asOwner ? "archive.owner_approved" : "archive.accountant_approved", "archive_import", import.Id, import.BusinessId,
            details: new { business = import.BusinessCode, month = Months.Format(import.Month), request.Note });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await DtoAsync(import, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Archived businesses (with stores and months), for choosing grant scopes.</summary>
    public async Task<IReadOnlyList<ArchiveBusinessDto>> BusinessesAsync(CancellationToken cancellationToken)
    {
        var grants = await MyGrantsAsync(cancellationToken).ConfigureAwait(false);
        if (grants.Count == 0)
        {
            throw AppException.Forbidden();
        }

        var imports = await db.ArchiveImports.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var stores = await db.ArchiveMasters.AsNoTracking().Where(m => m.Dataset == "stores").ToListAsync(cancellationToken).ConfigureAwait(false);
        return imports.GroupBy(i => i.BusinessId)
            .Where(g => grants.Any(gr => gr.BusinessId is null || gr.BusinessId == g.Key))
            .Select(g =>
            {
                var latest = g.OrderByDescending(i => i.Month).First();
                return new ArchiveBusinessDto(g.Key, latest.BusinessCode, latest.BusinessName,
                    stores.Where(s => s.BusinessId == g.Key).Select(s => JsonDocument.Parse(s.Data).RootElement)
                        .Select(s => new ArchiveStoreDto(s.GetProperty("id").GetGuid(), s.GetProperty("code").GetString()!, s.GetProperty("name").GetString()!))
                        .OrderBy(s => s.Code, StringComparer.Ordinal).ToList(),
                    g.Select(i => Months.Format(i.Month)).Order(StringComparer.Ordinal).ToList());
            })
            .OrderBy(b => b.Code, StringComparer.Ordinal).ToList();
    }

    private async Task<ArchiveImportDto> DtoAsync(ArchiveImport i, bool alreadyImported, CancellationToken cancellationToken)
    {
        var ids = new[] { (Guid?)i.ImportedByUserId, i.AccountantApprovedByUserId, i.OwnerApprovedByUserId }.Where(x => x != null).Select(x => x!.Value).ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
        var source = await db.ArchiveSources.AsNoTracking().Where(s => s.Id == i.SourceId).Select(s => s.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(i.Manifest, Json)!;
        return new ArchiveImportDto(i.Id, i.BusinessId, i.BusinessCode, i.BusinessName, Months.Format(i.Month), source, i.FileSha256, i.FileSize,
            manifest.Datasets.Where(d => !ArchiveDatasets.IsMaster(d.Name)).Sum(d => d.Records), names.GetValueOrDefault(i.ImportedByUserId, "?"), i.ImportedAtUtc,
            i.Status, i.AccountantApprovedByUserId is { } a ? names.GetValueOrDefault(a) : null, i.AccountantApprovedAtUtc, i.AccountantNote,
            i.OwnerApprovedByUserId is { } o ? names.GetValueOrDefault(o) : null, i.OwnerApprovedAtUtc, i.OwnerNote, i.RowVersion,
            manifest.Datasets.Select(d => new ArchiveImportDatasetDto(d.Name, d.Records, d.Totals, ArchiveDatasets.IsMaster(d.Name))).ToList(), alreadyImported);
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
