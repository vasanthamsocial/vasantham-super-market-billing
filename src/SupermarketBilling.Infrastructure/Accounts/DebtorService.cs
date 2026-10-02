using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Purchases;

namespace SupermarketBilling.Infrastructure.Accounts;

/// <summary>Debtors: customers who may buy on credit. Their balances come from the debtor ledger.</summary>
public sealed class DebtorService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    PartyAccountService accounts,
    AuditRecorder audit,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<DebtorDto>> ListAsync(Guid businessId, string? search, string? status, CancellationToken cancellationToken)
    {
        await accounts.RequireViewAsync(PartyTypes.Debtor, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.Debtors.AsNoTracking().Where(d => d.BusinessId == businessId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = "%" + search.Trim() + "%";
            query = query.Where(d => EF.Functions.ILike(d.LegalName, term) || (d.TradeName != null && EF.Functions.ILike(d.TradeName, term)) ||
                                     EF.Functions.ILike(d.Code, term) || (d.Gstin != null && EF.Functions.ILike(d.Gstin, term)) ||
                                     (d.Phone != null && EF.Functions.ILike(d.Phone, term)) || (d.WhatsAppNumber != null && EF.Functions.ILike(d.WhatsAppNumber, term)));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(d => d.Status == status);
        }

        var debtors = await query.OrderBy(d => d.LegalName).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var balances = await accounts.BalancesAsync(PartyTypes.Debtor, debtors.Select(d => d.Id).ToList(), cancellationToken).ConfigureAwait(false);
        return debtors.Select(d => ToDto(d, balances.GetValueOrDefault(d.Id))).ToList();
    }

    public async Task<DebtorDto> GetAsync(Guid businessId, Guid debtorId, CancellationToken cancellationToken)
    {
        await accounts.RequireViewAsync(PartyTypes.Debtor, businessId, cancellationToken).ConfigureAwait(false);
        var debtor = await db.Debtors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == debtorId && d.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Debtor");
        var balances = await accounts.BalancesAsync(PartyTypes.Debtor, [debtorId], cancellationToken).ConfigureAwait(false);
        return ToDto(debtor, balances.GetValueOrDefault(debtorId));
    }

    public async Task<DebtorDto> CreateAsync(Guid businessId, CreateDebtorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireManageAsync(businessId, cancellationToken).ConfigureAwait(false);
        if (request.OpeningBalance is not null)
        {
            await organisation.RequireAsync(Permissions.LedgersAdjust, businessId, null, cancellationToken).ConfigureAwait(false);
        }

        await EnsureGroupAsync(businessId, request.CustomerGroupId, cancellationToken).ConfigureAwait(false);
        Debtor debtor;
        try
        {
            debtor = Debtor.Create(businessId, request.Code, SupplierService.Details(request.LegalName, request.TradeName, request.Gstin, request.StateCode,
                    request.Address, request.ContactPerson, request.Phone, request.Email, request.WhatsAppNumber, request.SmsNumber, request.WhatsAppConsent,
                    request.SmsConsent, request.CreditPeriodDays),
                request.CreditLimit, request.CustomerGroupId, clock.GetUtcNow());
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        if (await db.Debtors.AnyAsync(d => d.BusinessId == businessId && d.Code == debtor.Code, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("debtor.code_taken", $"Debtor code {debtor.Code} is already used.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Debtors.Add(debtor);
        audit.Record("debtor.created", "debtor", debtor.Id, businessId, details: new
        {
            debtor.Code, debtor.LegalName, debtor.Gstin, debtor.CreditLimit, debtor.CreditPeriodDays, debtor.WhatsAppConsent, debtor.SmsConsent,
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        if (request.OpeningBalance is { } opening && opening != 0)
        {
            await accounts.PostOpeningAsync(PartyTypes.Debtor, businessId, debtor.Id,
                new OpeningBalanceRequest(opening, request.OpeningBalanceDate ?? BusinessCalendar.Today(clock)), cancellationToken).ConfigureAwait(false);
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(businessId, debtor.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DebtorDto> UpdateAsync(Guid businessId, Guid debtorId, UpdateDebtorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireManageAsync(businessId, cancellationToken).ConfigureAwait(false);
        await EnsureGroupAsync(businessId, request.CustomerGroupId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Locked like a posting, so the account cannot be closed while an entry is being added.
        var debtor = (await db.Debtors.FromSql($"SELECT *, xmin FROM debtors WHERE id = {debtorId} FOR UPDATE").ToListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(d => d.BusinessId == businessId) ?? throw AppException.NotFound("Debtor");
        db.Entry(debtor).Property(d => d.RowVersion).OriginalValue = request.RowVersion;
        var before = new { debtor.LegalName, debtor.Gstin, debtor.CreditLimit, debtor.CreditPeriodDays, debtor.Status, debtor.WhatsAppConsent, debtor.SmsConsent };
        var balance = await db.DebtorLedger.Where(e => e.PartyId == debtorId).SumAsync(e => e.Amount, cancellationToken).ConfigureAwait(false);
        try
        {
            debtor.Update(SupplierService.Details(request.LegalName, request.TradeName, request.Gstin, request.StateCode, request.Address, request.ContactPerson,
                    request.Phone, request.Email, request.WhatsAppNumber, request.SmsNumber, request.WhatsAppConsent, request.SmsConsent, request.CreditPeriodDays),
                request.CreditLimit, request.CustomerGroupId, clock.GetUtcNow());
            debtor.SetStatus(request.Status, balance);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        audit.Record("debtor.updated", "debtor", debtor.Id, businessId, details: new
        {
            before,
            after = new { debtor.LegalName, debtor.Gstin, debtor.CreditLimit, debtor.CreditPeriodDays, debtor.Status, debtor.WhatsAppConsent, debtor.SmsConsent },
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(businessId, debtorId, cancellationToken).ConfigureAwait(false);
    }

    internal static DebtorDto ToDto(Debtor d, (decimal Balance, decimal Overdue) account) => new(
        d.Id, d.Code, d.LegalName, d.TradeName, d.DisplayName, d.Gstin, d.StateCode, d.Address, d.ContactPerson, d.Phone, d.Email, d.WhatsAppNumber, d.SmsNumber,
        d.WhatsAppConsent, d.SmsConsent, d.ConsentChangedAtUtc, d.CreditPeriodDays, d.CreditLimit, d.CustomerGroupId, d.Status, account.Balance, account.Overdue,
        d.RowVersion);

    private async Task RequireManageAsync(Guid businessId, CancellationToken cancellationToken)
    {
        if (!(await access.BusinessesWithPermissionAsync(Permissions.DebtorsManage, cancellationToken).ConfigureAwait(false)).Contains(businessId))
        {
            await organisation.RequireAsync(Permissions.DebtorsManage, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureGroupAsync(Guid businessId, Guid? groupId, CancellationToken cancellationToken)
    {
        if (groupId is { } g && !await db.CustomerGroups.AnyAsync(x => x.Id == g && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Customer group");
        }
    }
}
