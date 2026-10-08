using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
/// Monthly close (spec section 22, D-040): checks that a month is complete and reconciles, locks it (nothing dated in it
/// can be posted, changed or removed afterwards: database triggers), and makes its archive package: every record of the
/// month and the master data they refer to, with counts, totals and checksums, encrypted for the business's archive
/// server and signed by this installation.
/// </summary>
public sealed class MonthCloseService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    ArchiveSigningKey signingKey,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Months

    /// <summary>Every month from the first one with records to the current one, with whether it is locked and packaged.</summary>
    public async Task<IReadOnlyList<MonthStatusDto>> MonthsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.MonthsView, businessId, null, cancellationToken).ConfigureAwait(false);
        var today = BusinessCalendar.Today(clock);
        var current = Months.First(today);
        var first = await FirstMonthAsync(businessId, cancellationToken).ConfigureAwait(false) ?? current;
        var locks = await (
                from l in db.MonthLocks.AsNoTracking()
                join u in db.Users.AsNoTracking() on l.LockedByUserId equals u.Id
                where l.BusinessId == businessId
                select new { l.Month, l.LockedAtUtc, u.DisplayName })
            .ToDictionaryAsync(l => l.Month, cancellationToken).ConfigureAwait(false);
        var packages = (await db.MonthPackages.AsNoTracking().Where(p => p.BusinessId == businessId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(p => p.Month);
        var months = new List<MonthStatusDto>();
        for (var month = current; month >= first; month = month.AddMonths(-1))
        {
            var locked = locks.GetValueOrDefault(month);
            var made = packages[month].OrderByDescending(p => p.CreatedAtUtc).ToList();
            months.Add(new MonthStatusDto(Months.Format(month), locked is not null ? "LOCKED" : month == current ? "IN_PROGRESS" : "OPEN", locked?.LockedAtUtc,
                locked?.DisplayName, made.Count, made.FirstOrDefault()?.CreatedAtUtc, made.FirstOrDefault()?.FileSha256));
        }

        return months;
    }

    /// <summary>Whether the month can be locked: each check, with how many records stop it.</summary>
    public async Task<MonthChecksDto> ChecksAsync(Guid businessId, string month, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.MonthsView, businessId, null, cancellationToken).ConfigureAwait(false);
        var first = Valid(() => Months.Parse(month));
        return await RunChecksAsync(businessId, first, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Locks the month if every check passes (checked again under a lock, so nothing slips in between).</summary>
    public async Task<MonthStatusDto> LockAsync(Guid businessId, string month, LockMonthRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.MonthsClose, businessId, null, cancellationToken).ConfigureAwait(false);
        var first = Valid(() => Months.Parse(month));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"month-close|" + businessId}))", cancellationToken).ConfigureAwait(false);
        if (await db.MonthLocks.AnyAsync(l => l.BusinessId == businessId && l.Month == first, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("month.locked", $"{Months.Format(first)} is already locked.");
        }

        var checks = await RunChecksAsync(businessId, first, cancellationToken).ConfigureAwait(false);
        if (!checks.Ready)
        {
            var failed = checks.Checks.Where(c => !c.Passed).Select(c => c.Title);
            throw AppException.Conflict("month.not_ready", $"{Months.Format(first)} cannot be locked yet: {string.Join("; ", failed)}.");
        }

        var now = clock.GetUtcNow();
        var monthLock = Valid(() => MonthLock.Lock(businessId, first, BusinessCalendar.Today(clock), JsonSerializer.Serialize(checks.Checks, Json), request.Note,
            currentUser.UserId, now));
        db.MonthLocks.Add(monthLock);
        audit.Record("month.locked", "month_lock", monthLock.Id, businessId, details: new { month = Months.Format(first), request.Note });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await MonthsAsync(businessId, cancellationToken).ConfigureAwait(false)).First(m => m.Month == Months.Format(first));
    }

    private async Task<MonthChecksDto> RunChecksAsync(Guid businessId, DateOnly first, CancellationToken cancellationToken)
    {
        var next = first.AddMonths(1);
        var from = BusinessCalendar.StartOf(first);
        var to = BusinessCalendar.StartOf(next);
        var today = BusinessCalendar.Today(clock);
        var checks = new List<MonthCheckDto>();
        void Add(string key, string title, long problems, string detail) => checks.Add(new MonthCheckDto(key, title, problems == 0, problems, detail));

        Add("month_over", "The month is over", next <= today ? 0 : 1, next <= today ? "Ended." : $"It ends on {next.AddDays(-1):yyyy-MM-dd}.");

        // Months are closed in order, so a locked month never has an open one before it.
        var firstMonth = await FirstMonthAsync(businessId, cancellationToken).ConfigureAwait(false) ?? first;
        var locked = await db.MonthLocks.AsNoTracking().Where(l => l.BusinessId == businessId && l.Month < first).Select(l => l.Month)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var unlocked = new List<string>();
        for (var m = firstMonth; m < first; m = m.AddMonths(1))
        {
            if (!locked.Contains(m))
            {
                unlocked.Add(Months.Format(m));
            }
        }

        Add("earlier_locked", "Earlier months are locked", unlocked.Count, unlocked.Count == 0 ? "All earlier months are locked." : $"Lock first: {string.Join(", ", unlocked)}.");

        Add("shifts_closed", "All shifts are closed", await CountAsync(
            $"SELECT count(*) AS \"Value\" FROM shifts WHERE business_id = {businessId} AND business_date >= {first} AND business_date < {next} AND status <> 'CLOSED'",
            cancellationToken).ConfigureAwait(false), "Shifts of the month still open.");
        Add("cash_reviewed", "Cash differences are reviewed", await CountAsync(
            $"SELECT count(*) AS \"Value\" FROM shifts WHERE business_id = {businessId} AND business_date >= {first} AND business_date < {next} AND difference <> 0 AND reviewed_at_utc IS NULL",
            cancellationToken).ConfigureAwait(false), "Closed shifts whose drawer difference a manager has not reviewed.");
        Add("rounds_counted", "Collection rounds are counted", await CountAsync(
            $"SELECT count(*) AS \"Value\" FROM collector_sessions WHERE business_id = {businessId} AND business_date >= {first} AND business_date < {next} AND status <> 'CONFIRMED'",
            cancellationToken).ConfigureAwait(false), "Collectors' rounds not yet handed over and counted.");
        Add("offline_cleared", "Offline queues are cleared", await CountAsync(
            $"""
            SELECT (SELECT count(*) FROM offline_bills WHERE business_id = {businessId} AND issued_at_utc >= {from} AND issued_at_utc < {to}
                      AND (status = 'QUARANTINED' OR (status = 'POSTED' AND review IS NOT NULL AND reviewed_at_utc IS NULL)))
                 + (SELECT count(*) FROM offline_submissions WHERE business_id = {businessId} AND recorded_at_utc >= {from} AND recorded_at_utc < {to}
                      AND status = 'QUARANTINED') AS "Value"
            """, cancellationToken).ConfigureAwait(false), "Offline bills or collections waiting for a manager's decision or review.");
        Add("sales_reconcile", "Sales reconcile (lines, taxes, payments)", await CountAsync(
            $"""
            SELECT count(*) AS "Value" FROM sales_invoices i
             WHERE i.business_id = {businessId} AND i.business_date >= {first} AND i.business_date < {next}
               AND ((SELECT coalesce(sum(l.total), 0) FROM sales_invoice_lines l WHERE l.invoice_id = i.id) + i.round_off <> i.grand_total
                 OR (SELECT coalesce(sum(l.cgst), 0) FROM sales_invoice_lines l WHERE l.invoice_id = i.id) <> i.cgst_total
                 OR (SELECT coalesce(sum(l.sgst), 0) FROM sales_invoice_lines l WHERE l.invoice_id = i.id) <> i.sgst_total
                 OR (SELECT coalesce(sum(l.igst), 0) FROM sales_invoice_lines l WHERE l.invoice_id = i.id) <> i.igst_total
                 OR (SELECT coalesce(sum(p.amount), 0) FROM sales_invoice_payments p WHERE p.invoice_id = i.id) <> i.paid_total)
            """, cancellationToken).ConfigureAwait(false), "Invoices whose lines, taxes or payments do not add up to the bill.");
        Add("returns_reconcile", "Returns reconcile", await CountAsync(
            $"""
            SELECT count(*) AS "Value" FROM sales_returns r
             WHERE r.business_id = {businessId} AND r.business_date >= {first} AND r.business_date < {next}
               AND (SELECT coalesce(sum(l.total), 0) FROM sales_return_lines l WHERE l.return_id = r.id) + r.round_off <> r.grand_total
            """, cancellationToken).ConfigureAwait(false), "Credit notes whose lines do not add up.");
        Add("stock_reconcile", "Stock balances equal the stock ledger", await CountAsync(
            $"""
            SELECT count(*) AS "Value" FROM stock_balances b
             WHERE b.business_id = {businessId}
               AND b.quantity <> (SELECT coalesce(sum(l.quantity), 0) FROM stock_ledger l WHERE l.store_id = b.store_id AND l.variant_id = b.variant_id)
            """, cancellationToken).ConfigureAwait(false), "Items whose stock on hand differs from their movements.");

        return new MonthChecksDto(Months.Format(first), checks.All(c => c.Passed), checks);
    }

    // Packages

    /// <summary>The archive package of a locked month: made, recorded (with its checksum), and returned as a file.</summary>
    public async Task<(byte[] Content, string FileName)> PackageAsync(Guid businessId, string month, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.MonthsClose, businessId, null, cancellationToken).ConfigureAwait(false);
        var first = Valid(() => Months.Parse(month));
        if (!await db.MonthLocks.AnyAsync(l => l.BusinessId == businessId && l.Month == first, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("month.not_locked", $"Lock {Months.Format(first)} before making its archive package.");
        }

        var recipient = await db.ArchiveRecipients.AsNoTracking().FirstOrDefaultAsync(r => r.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.Conflict("archive.no_recipient", "Register the archive server's public key first (Archive keys).");
        var business = await db.Businesses.AsNoTracking().FirstAsync(b => b.Id == businessId, cancellationToken).ConfigureAwait(false);
        var installation = await db.Installation.AsNoTracking().Select(i => i.InstallationId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var datasets = new List<ArchiveDatasetInput>();
        foreach (var dataset in Datasets())
        {
            var rows = await db.Database.SqlQueryRaw<string>(dataset.Sql, Parameters(businessId, first)).ToListAsync(cancellationToken).ConfigureAwait(false);
            datasets.Add(new ArchiveDatasetInput(dataset.Name, rows, dataset.Totals));
        }

        var now = clock.GetUtcNow();
        var packageId = Guid.CreateVersion7(now);
        using var signer = signingKey.Create();
        using var archive = ECDiffieHellman.Create();
        archive.ImportFromPem(recipient.PublicKeyPem);
        var file = ArchivePackage.Write(new ArchiveManifest(ArchivePackage.Format, packageId, businessId, business.Code, business.LegalName, Months.Format(first), now,
            installation, []), datasets, signer, archive, out var manifest);

        var record = MonthPackage.Record(packageId, businessId, first, ArchivePackage.FileSha256(file), file.LongLength, ArchivePackage.KeyId(signer.ExportSubjectPublicKeyInfo()),
            recipient.KeyId, JsonSerializer.Serialize(manifest, Json), currentUser.UserId, now);
        db.MonthPackages.Add(record);
        audit.Record("month.packaged", "month_package", record.Id, businessId, details: new
        {
            month = Months.Format(first),
            record.FileSha256,
            record.FileSize,
            records = manifest.Datasets.Sum(d => d.Records),
            recipient = recipient.KeyId,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (file, $"{business.Code}-{Months.Format(first)}-{packageId.ToString("N")[..8]}.sbarc");
    }

    public async Task<IReadOnlyList<MonthPackageDto>> PackagesAsync(Guid businessId, string month, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.MonthsView, businessId, null, cancellationToken).ConfigureAwait(false);
        var first = Valid(() => Months.Parse(month));
        return await (
                from p in db.MonthPackages.AsNoTracking()
                join u in db.Users.AsNoTracking() on p.CreatedByUserId equals u.Id
                where p.BusinessId == businessId && p.Month == first
                orderby p.CreatedAtUtc descending
                select new MonthPackageDto(p.Id, month, p.FileSha256, p.FileSize, p.RecipientKeyId, u.DisplayName, p.CreatedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    // Keys

    public async Task<ArchiveKeysDto> KeysAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await organisation.RequireAsync(Permissions.MonthsView, businessId, null, cancellationToken).ConfigureAwait(false);
        var recipient = await db.ArchiveRecipients.AsNoTracking().FirstOrDefaultAsync(r => r.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        return new ArchiveKeysDto(signingKey.KeyId(), signingKey.PublicKeyPem(), recipient?.KeyId, recipient?.SetAtUtc, recipient?.RowVersion);
    }

    /// <summary>Registers (or replaces) the archive server's public key, copied from the archive's own screen.</summary>
    public async Task<ArchiveKeysDto> SetRecipientAsync(Guid businessId, SetArchiveRecipientRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.MonthsClose, businessId, null, cancellationToken).ConfigureAwait(false);
        string pem;
        string keyId;
        try
        {
            using var key = ECDiffieHellman.Create();
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
            throw AppException.Validation("archive.key_invalid", "Paste the archive server's public key exactly as its screen shows it (-----BEGIN PUBLIC KEY----- ...).");
        }

        var now = clock.GetUtcNow();
        var existing = await db.ArchiveRecipients.FirstOrDefaultAsync(r => r.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.ArchiveRecipients.Add(ArchiveRecipient.Register(businessId, pem, keyId, currentUser.UserId, now));
        }
        else
        {
            if (request.RowVersion != existing.RowVersion)
            {
                throw AppException.Conflict("concurrency.conflict", "The archive key was changed by someone else. Reload and try again.");
            }

            existing.Replace(pem, keyId, currentUser.UserId, now);
        }

        audit.Record("archive.recipient_set", "archive_recipient", existing?.Id, businessId, details: new { keyId, replaced = existing is not null });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await KeysAsync(businessId, cancellationToken).ConfigureAwait(false);
    }

    // Helpers

    private async Task<DateOnly?> FirstMonthAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var first = (await db.Database.SqlQuery<DateOnly?>(
                $"""
                SELECT min(d) AS "Value" FROM (
                    SELECT min(business_date) AS d FROM sales_invoices WHERE business_id = {businessId}
                    UNION ALL SELECT min(business_date) FROM grns WHERE business_id = {businessId}
                    UNION ALL SELECT min(business_date) FROM stock_ledger WHERE business_id = {businessId}
                    UNION ALL SELECT min(entry_date) FROM debtor_ledger WHERE business_id = {businessId}
                    UNION ALL SELECT min(entry_date) FROM supplier_ledger WHERE business_id = {businessId}
                    UNION ALL SELECT min(business_date) FROM shifts WHERE business_id = {businessId}) x
                """).ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        return first is { } day ? Months.First(day) : null;
    }

    private async Task<long> CountAsync(FormattableString sql, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<long>(sql).ToListAsync(cancellationToken).ConfigureAwait(false)).Single();

    private static object[] Parameters(Guid businessId, DateOnly first) =>
    [
        new NpgsqlParameter("b", businessId),
        new NpgsqlParameter("first", first),
        new NpgsqlParameter("next", first.AddMonths(1)),
        new NpgsqlParameter("from", BusinessCalendar.StartOf(first)),
        new NpgsqlParameter("to", BusinessCalendar.StartOf(first.AddMonths(1))),
    ];

    private sealed record Dataset(string Name, string Sql, IReadOnlyList<string> Totals);

    /// <summary>
    /// What a month's package holds: the master data its records refer to (as they are now; users without passwords or
    /// keys) and every record dated in the month, each row exactly as stored (JSON).
    /// </summary>
    private static IEnumerable<Dataset> Datasets()
    {
        static Dataset Rows(string name, string from, string order = "t.id", params string[] totals) =>
            new(name, $"SELECT row_to_json(t)::text AS \"Value\" FROM {from} ORDER BY {order}", totals);

        const string Month = "t.business_id = @b AND t.business_date >= @first AND t.business_date < @next";
        const string Entries = "t.business_id = @b AND t.entry_date >= @first AND t.entry_date < @next";
        const string During = "t.business_id = @b AND {0} >= @from AND {0} < @to";

        // Master data.
        yield return Rows("businesses", "businesses t WHERE t.id = @b");
        foreach (var table in ArchiveDatasets.MasterTables)
        {
            yield return Rows(table, $"{table} t WHERE t.business_id = @b");
        }

        yield return Rows("users",
            "(SELECT u.id, u.username, u.display_name, u.is_active FROM users u WHERE EXISTS (SELECT 1 FROM role_assignments r WHERE r.user_id = u.id AND r.business_id = @b)) t");

        // The month's records.
        yield return Rows("sales_invoices", $"sales_invoices t WHERE {Month}", "t.id", "grand_total", "taxable_total", "cgst_total", "sgst_total", "igst_total", "cess_total", "paid_total");
        yield return Rows("sales_invoice_lines", $"sales_invoice_lines t WHERE t.invoice_id IN (SELECT t.id FROM sales_invoices t WHERE {Month})", "t.id", "total", "taxable", "quantity");
        yield return Rows("sales_invoice_payments", $"sales_invoice_payments t WHERE t.invoice_id IN (SELECT t.id FROM sales_invoices t WHERE {Month})", "t.id", "amount");
        yield return Rows("credit_note_redemptions", $"credit_note_redemptions t WHERE t.invoice_id IN (SELECT t.id FROM sales_invoices t WHERE {Month})", "t.id", "amount");
        yield return Rows("sales_returns", $"sales_returns t WHERE {Month}", "t.id", "grand_total", "taxable_total", "store_credit");
        yield return Rows("sales_return_lines", $"sales_return_lines t WHERE t.return_id IN (SELECT t.id FROM sales_returns t WHERE {Month})", "t.id", "total", "quantity");
        yield return Rows("sales_return_refunds", $"sales_return_refunds t WHERE t.return_id IN (SELECT t.id FROM sales_returns t WHERE {Month})", "t.id", "amount");
        yield return Rows("grns", $"grns t WHERE {Month}", "t.id", "invoice_total", "taxable_total", "landed_total");
        yield return Rows("grn_lines", $"grn_lines t WHERE t.grn_id IN (SELECT t.id FROM grns t WHERE {Month})", "t.id", "total", "base_quantity");
        yield return Rows("grn_expenses", $"grn_expenses t WHERE t.grn_id IN (SELECT t.id FROM grns t WHERE {Month})", "t.id", "amount");
        yield return Rows("grn_allocations",
            $"grn_allocations t WHERE t.expense_id IN (SELECT e.id FROM grn_expenses e WHERE e.grn_id IN (SELECT t.id FROM grns t WHERE {Month}))", "t.id", "amount");
        yield return Rows("purchase_returns", $"purchase_returns t WHERE {Month}", "t.id", "total");
        yield return Rows("purchase_return_lines", $"purchase_return_lines t WHERE t.purchase_return_id IN (SELECT t.id FROM purchase_returns t WHERE {Month})", "t.id", "total");
        yield return Rows("stock_documents", $"stock_documents t WHERE {Month}");
        yield return Rows("stock_ledger", $"stock_ledger t WHERE {Month}", "t.sequence", "quantity", "value");
        yield return Rows("debtor_ledger", $"debtor_ledger t WHERE {Entries}", "t.id", "amount");
        yield return Rows("supplier_ledger", $"supplier_ledger t WHERE {Entries}", "t.id", "amount");
        yield return Rows("debtor_settlements", "debtor_settlements t WHERE " + string.Format(System.Globalization.CultureInfo.InvariantCulture, During, "t.created_at_utc"), "t.id", "amount");
        yield return Rows("supplier_settlements", "supplier_settlements t WHERE " + string.Format(System.Globalization.CultureInfo.InvariantCulture, During, "t.created_at_utc"), "t.id", "amount");
        yield return Rows("debtor_receipts", "debtor_receipts t WHERE t.business_id = @b AND t.receipt_date >= @first AND t.receipt_date < @next", "t.id", "amount");
        yield return Rows("receipt_reversals", "receipt_reversals t WHERE " + string.Format(System.Globalization.CultureInfo.InvariantCulture, During, "t.reversed_at_utc"));
        yield return Rows("supplier_payments", "supplier_payments t WHERE t.business_id = @b AND t.payment_date >= @first AND t.payment_date < @next", "t.id", "amount");
        yield return Rows("shifts", $"shifts t WHERE {Month}", "t.id", "expected_cash", "counted_cash", "difference");
        yield return Rows("shift_counts", $"shift_counts t WHERE t.shift_id IN (SELECT t.id FROM shifts t WHERE {Month})");
        yield return Rows("cash_movements", $"cash_movements t WHERE t.shift_id IN (SELECT t.id FROM shifts t WHERE {Month})", "t.id", "amount");
        yield return Rows("collector_sessions", $"collector_sessions t WHERE {Month}", "t.id", "expected_cash", "counted_cash");
        yield return Rows("collector_session_counts", $"collector_session_counts t WHERE t.session_id IN (SELECT t.id FROM collector_sessions t WHERE {Month})");
        yield return Rows("cheque_events", "cheque_events t WHERE t.business_id = @b AND t.event_date >= @first AND t.event_date < @next");
        yield return Rows("cheques", "cheques t WHERE t.id IN (SELECT e.cheque_id FROM cheque_events e WHERE e.business_id = @b AND e.event_date >= @first AND e.event_date < @next)",
            "t.id", "amount");
        yield return Rows("offline_bills", "offline_bills t WHERE " + string.Format(System.Globalization.CultureInfo.InvariantCulture, During, "t.issued_at_utc"), "t.id", "grand_total");
        yield return Rows("offline_submissions", "offline_submissions t WHERE " + string.Format(System.Globalization.CultureInfo.InvariantCulture, During, "t.recorded_at_utc"), "t.id", "amount");
        yield return Rows("audit_events", "audit_events t WHERE " + string.Format(System.Globalization.CultureInfo.InvariantCulture, During, "t.occurred_at_utc"), "t.sequence");
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
