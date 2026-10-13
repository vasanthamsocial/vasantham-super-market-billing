using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Archiving;
using SupermarketBilling.Domain.Archiving;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Archiving;

/// <summary>
/// Retention on the store server (spec section 22, D-044), first part: the gates. A month may be removed only when
/// retention is switched on, the month is older than the current and the previous financial year, it is locked and
/// packaged, the archive's signed receipt says it holds that exact package approved by the accountant and the owner,
/// earlier months went first, and nothing kept refers to it. Only the owner registers the archive's key and receipts.
/// For now this shows what removal would do (a dry run); nothing is removed.
/// </summary>
public sealed class RetentionService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IOptions<RetentionOptions> options,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    /// <summary>
    /// Records that removal would take from the store server (each is in the archive package). Stock movements are
    /// replaced by one carried-forward total per item, store and cost layer, so stock and its value stay exact.
    /// </summary>
    private static readonly HashSet<string> Removed = new(StringComparer.Ordinal)
    {
        "sales_invoices", "sales_invoice_lines", "sales_invoice_payments", "credit_note_redemptions", "sales_returns", "sales_return_lines",
        "sales_return_refunds", "grns", "grn_lines", "grn_expenses", "grn_allocations", "purchase_returns", "purchase_return_lines", "stock_documents",
        "stock_ledger", "offline_bills", "audit_events",
    };

    private const string DryRunNote =
        "This is what removing the month would do. Removal itself is not available yet: nothing is removed. Customer and supplier accounts, "
        + "receipts, payments, cheques, shifts and collection rounds stay on this server (they carry balances that are still in use).";

    private bool Enabled => options.Value.Enabled;

    public async Task<RetentionDto> StatusAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.MonthsView, businessId, null, cancellationToken).ConfigureAwait(false);
        var keepFrom = Retention.KeepFrom(BusinessCalendar.Today(clock));
        var recipient = await db.ArchiveRecipients.AsNoTracking().FirstOrDefaultAsync(r => r.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        var months = new List<RetentionMonthDto>();
        if (await MonthCloseService.FirstMonthAsync(db, businessId, cancellationToken).ConfigureAwait(false) is { } first)
        {
            var locked = await db.MonthLocks.AsNoTracking().Where(l => l.BusinessId == businessId && l.Month < keepFrom).Select(l => l.Month)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var packaged = await db.MonthPackages.AsNoTracking().Where(p => p.BusinessId == businessId && p.Month < keepFrom).Select(p => p.Month).Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var receipts = await db.MonthRemovalReceipts.AsNoTracking().Where(r => r.BusinessId == businessId).ToDictionaryAsync(r => r.Month, cancellationToken)
                .ConfigureAwait(false);
            for (var month = first; month < keepFrom; month = month.AddMonths(1))
            {
                var receipt = receipts.GetValueOrDefault(month);
                months.Add(new RetentionMonthDto(Months.Format(month), locked.Contains(month), packaged.Contains(month), receipt?.RegisteredAtUtc,
                    receipt?.ArchiveApprovedAtUtc));
            }
        }

        return new RetentionDto(Enabled, Months.Format(keepFrom), recipient is not null, recipient?.ReceiptKeyId, recipient?.ReceiptKeySetAtUtc, months);
    }

    /// <summary>Registers the archive server's receipt key (from its Store servers screen). The owner only.</summary>
    public async Task<RetentionDto> SetReceiptKeyAsync(Guid businessId, SetReceiptKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireOwnerAsync(businessId, cancellationToken).ConfigureAwait(false);
        var recipient = await db.ArchiveRecipients.FirstOrDefaultAsync(r => r.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.Conflict("retention.archive_missing", "Register the archive server's key on the Month close screen first.");
        string pem, keyId;
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
            throw AppException.Validation("archive.key_invalid", "Paste the archive's receipt key exactly as its screen shows it (-----BEGIN PUBLIC KEY----- ...).");
        }

        if (keyId == recipient.KeyId)
        {
            throw AppException.Validation("retention.wrong_key", "That is the archive's encryption key. Paste its receipt key (the second key on its Store servers screen).");
        }

        recipient.SetReceiptKey(pem, keyId, currentUser.UserId, clock.GetUtcNow());
        audit.Record("retention.receipt_key_set", "archive_recipient", recipient.Id, businessId, details: new { keyId });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await StatusAsync(businessId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers the archive's receipt for a month: only if signed with the registered receipt key, for this business,
    /// and for a package this server made of that (locked) month, unchanged. The owner only.
    /// </summary>
    public async Task<RetentionPlanDto> RegisterReceiptAsync(Guid businessId, byte[] file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await RequireOwnerAsync(businessId, cancellationToken).ConfigureAwait(false);
        var recipient = await db.ArchiveRecipients.AsNoTracking().FirstOrDefaultAsync(r => r.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        if (recipient?.ReceiptKeyPem is null)
        {
            throw AppException.Conflict("retention.receipt_key_missing", "Register the archive's receipt key first.");
        }

        var payload = Verify(file, recipient.ReceiptKeyPem);
        if (payload.BusinessId != businessId)
        {
            throw AppException.Validation("retention.receipt_other_business", $"This receipt is for another business ({payload.BusinessCode}).");
        }

        var month = Valid(() => Months.Parse(payload.Month));
        var package = await db.MonthPackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == payload.PackageId && p.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppException.Validation("retention.package_unknown", "The receipt is for a package this server did not make.");
        if (package.Month != month || package.FileSha256 != payload.FileSha256)
        {
            throw AppException.Validation("retention.receipt_mismatch", "The receipt does not match the package this server made for that month.");
        }

        var existing = await db.MonthRemovalReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.BusinessId == businessId && r.Month == month, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.PackageId != payload.PackageId)
        {
            throw AppException.Conflict("retention.receipt_exists", $"A receipt for {payload.Month} (another package) is already registered.");
        }

        if (existing is null)
        {
            var receipt = MonthRemovalReceipt.Register(payload.ReceiptId, businessId, month, payload.PackageId, payload.FileSha256, recipient.ReceiptKeyId!,
                Encoding.UTF8.GetString(file), payload.OwnerApprovedAtUtc, currentUser.UserId, clock.GetUtcNow());
            db.MonthRemovalReceipts.Add(receipt);
            audit.Record("retention.receipt_registered", "month_removal_receipt", receipt.Id, businessId,
                details: new { month = payload.Month, package = payload.PackageId, payload.FileSha256, approved = payload.OwnerApprovedAtUtc });
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        }

        return await PlanAsync(businessId, payload.Month, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What removing the month would do: each gate, what stops it, and what would be removed or kept (a dry run).</summary>
    public async Task<RetentionPlanDto> PlanAsync(Guid businessId, string month, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.MonthsView, businessId, null, cancellationToken).ConfigureAwait(false);
        RequireEnabled();
        var first = Valid(() => Months.Parse(month));
        var keepFrom = Retention.KeepFrom(BusinessCalendar.Today(clock));
        var checks = new List<RetentionCheckDto>
        {
            new("enabled", "Retention is switched on for this server", Enabled, null),
            new("old_enough", $"Older than the months always kept (from {Months.Format(keepFrom)})", first < keepFrom, null),
        };
        var locked = await db.MonthLocks.AsNoTracking().AnyAsync(l => l.BusinessId == businessId && l.Month == first, cancellationToken).ConfigureAwait(false);
        checks.Add(new("locked", "The month is locked", locked, null));
        var packages = await db.MonthPackages.AsNoTracking().Where(p => p.BusinessId == businessId && p.Month == first).ToListAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(new("packaged", "An archive package was made for it", packages.Count > 0, null));
        checks.Add(await ReceiptCheckAsync(businessId, first, packages, cancellationToken).ConfigureAwait(false));
        var earliest = await MonthCloseService.FirstMonthAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        checks.Add(new("earlier_removed", "Earlier months were removed first", earliest is null || earliest >= first,
            earliest is { } e && e < first ? $"{Months.Format(e)} still holds records." : null));

        var removed = new List<RetentionCountDto>();
        var kept = new List<RetentionCountDto>();
        foreach (var dataset in MonthCloseService.Datasets().Where(d => !ArchiveDatasets.IsMaster(d.Name)))
        {
            var count = await ScalarAsync<long>($"SELECT count(*) AS \"Value\" FROM ({dataset.Sql}) x", businessId, first, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                (Removed.Contains(dataset.Name) ? removed : kept).Add(new RetentionCountDto(dataset.Name, count));
            }
        }

        var blockers = new List<RetentionBlockerDto>();
        foreach (var (code, description, from, example) in Blockers)
        {
            var count = await ScalarAsync<long>($"SELECT count(*) AS \"Value\" {from}", businessId, first, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                var sql = "SELECT " + example + " AS \"Value\" " + from + " ORDER BY 1 LIMIT 5"; // fixed text above, never input
                var examples = await db.Database.SqlQueryRaw<string>(sql, MonthCloseService.Parameters(businessId, first)).ToListAsync(cancellationToken).ConfigureAwait(false);
                blockers.Add(new RetentionBlockerDto(code, description, count, examples));
            }
        }

        return new RetentionPlanDto(Months.Format(first), checks.All(c => c.Passed) && blockers.Count == 0, checks, blockers, removed, kept, DryRunNote);
    }

    /// <summary>
    /// What stops a month from being removed: records that stay (later months) and refer to it, or that the archive
    /// package does not hold. Parameters: @b, @first and @next (the month), as for the package.
    /// </summary>
    private static readonly (string Code, string Description, string From, string Example)[] Blockers =
    [
        ("later_returns", "Credit notes of later months return bills of this month",
            "FROM sales_returns r JOIN sales_invoices i ON i.id = r.original_invoice_id WHERE i.business_id = @b AND i.business_date >= @first AND i.business_date < @next AND r.business_date >= @next",
            "r.number || ' (' || r.business_date || ') returns ' || i.number"),
        ("later_store_credit", "Store credit from this month's credit notes was used on later bills",
            "FROM credit_note_redemptions c JOIN sales_returns r ON r.id = c.return_id JOIN sales_invoices i ON i.id = c.invoice_id WHERE r.business_id = @b AND r.business_date >= @first AND r.business_date < @next AND i.business_date >= @next",
            "r.number || ' used on ' || i.number"),
        ("later_purchase_returns", "Debit notes of later months return goods received this month",
            "FROM purchase_returns p JOIN grns g ON g.id = p.grn_id WHERE g.business_id = @b AND g.business_date >= @first AND g.business_date < @next AND p.business_date >= @next",
            "p.number || ' (' || p.business_date || ') against ' || g.number"),
        ("deliveries", "Bills of this month have packing or delivery records, which the archive package does not hold yet",
            "FROM packing_challans c JOIN sales_invoices i ON i.id = c.invoice_id WHERE i.business_id = @b AND i.business_date >= @first AND i.business_date < @next",
            "c.number || ' for ' || i.number"),
    ];

    private async Task<RetentionCheckDto> ReceiptCheckAsync(Guid businessId, DateOnly month, List<MonthPackage> packages, CancellationToken cancellationToken)
    {
        const string Description = "The archive's receipt says it holds this month's package, approved";
        var receipt = await db.MonthRemovalReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.BusinessId == businessId && r.Month == month, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            return new("archive_receipt", Description, false, "No receipt registered yet: download it from the archive (an approved month) and register it here.");
        }

        // Verified again with the key registered now: a replaced key no longer vouches for old receipts.
        var recipient = await db.ArchiveRecipients.AsNoTracking().FirstOrDefaultAsync(r => r.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = Verify(Encoding.UTF8.GetBytes(receipt.Receipt), recipient?.ReceiptKeyPem
                ?? throw new ArchivePackageException("No archive receipt key is registered."));
            var package = packages.FirstOrDefault(p => p.Id == payload.PackageId);
            return package is not null && package.FileSha256 == payload.FileSha256
                ? new("archive_receipt", Description, true, $"Approved in the archive on {payload.OwnerApprovedAtUtc:yyyy-MM-dd}.")
                : new("archive_receipt", Description, false, "The receipt no longer matches this month's package.");
        }
        catch (AppException e)
        {
            return new("archive_receipt", Description, false, e.Message);
        }
        catch (ArchivePackageException e)
        {
            return new("archive_receipt", Description, false, e.Message);
        }
    }

    private static ArchiveReceiptPayload Verify(byte[] file, string receiptKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(receiptKeyPem);
        try
        {
            return ArchiveReceipt.Read(file, key);
        }
        catch (ArchivePackageException e)
        {
            throw AppException.Validation("retention.receipt_invalid", e.Message);
        }
    }

    private async Task<T> ScalarAsync<T>(string sql, Guid businessId, DateOnly first, CancellationToken cancellationToken) =>
        (await db.Database.SqlQueryRaw<T>(sql, MonthCloseService.Parameters(businessId, first)).ToListAsync(cancellationToken).ConfigureAwait(false)).Single();

    private async Task RequireOwnerAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.MonthsRemove, businessId, null, cancellationToken).ConfigureAwait(false);
        RequireEnabled();
    }

    private void RequireEnabled()
    {
        if (!Enabled)
        {
            throw AppException.Conflict("retention.disabled", "Retention is switched off on this server (Retention:Enabled). Archived months stay here.");
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
