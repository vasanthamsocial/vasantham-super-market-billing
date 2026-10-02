using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Infrastructure.Accounts;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Catalog;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Purchases;

/// <summary>Suppliers (with their balances from the supplier ledger) and the business's purchasing settings.</summary>
public sealed class SupplierService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
    PartyAccountService accounts,
    AuditRecorder audit,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<SupplierDto>> ListAsync(Guid businessId, string? search, CancellationToken cancellationToken)
    {
        await RequireAnywhereAsync(Permissions.PurchasesView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.Suppliers.AsNoTracking().Where(s => s.BusinessId == businessId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = "%" + search.Trim() + "%";
            query = query.Where(s => EF.Functions.ILike(s.Name, term) || EF.Functions.ILike(s.Code, term) || (s.Gstin != null && EF.Functions.ILike(s.Gstin, term)));
        }

        var suppliers = await query.OrderBy(s => s.Name).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var balances = await accounts.BalancesAsync(PartyTypes.Supplier, suppliers.Select(s => s.Id).ToList(), cancellationToken).ConfigureAwait(false);
        return suppliers.Select(s => ToDto(s, balances.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<SupplierDto> GetAsync(Guid businessId, Guid supplierId, CancellationToken cancellationToken)
    {
        await RequireAnywhereAsync(Permissions.PurchasesView, businessId, cancellationToken).ConfigureAwait(false);
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId && s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Supplier");
        var balances = await accounts.BalancesAsync(PartyTypes.Supplier, [supplierId], cancellationToken).ConfigureAwait(false);
        return ToDto(supplier, balances.GetValueOrDefault(supplierId));
    }

    public async Task<SupplierDto> CreateAsync(Guid businessId, CreateSupplierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAnywhereAsync(Permissions.SuppliersManage, businessId, cancellationToken).ConfigureAwait(false);
        if (request.OpeningBalance is not null)
        {
            await organisation.RequireAsync(Permissions.LedgersAdjust, businessId, null, cancellationToken).ConfigureAwait(false);
        }

        Supplier supplier;
        try
        {
            supplier = Supplier.Create(businessId, request.Code, Details(request.Name, request.TradeName, request.Gstin, request.StateCode, request.Address,
                request.ContactPerson, request.Phone, request.Email, request.WhatsAppNumber, request.SmsNumber, request.WhatsAppConsent, request.SmsConsent,
                request.CreditPeriodDays), clock.GetUtcNow());
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        if (await db.Suppliers.AnyAsync(s => s.BusinessId == businessId && s.Code == supplier.Code, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("supplier.code_taken", $"Supplier code {supplier.Code} is already used.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Suppliers.Add(supplier);
        audit.Record("supplier.created", "supplier", supplier.Id, businessId, details: new { supplier.Code, supplier.Name, supplier.Gstin, supplier.CreditPeriodDays });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        if (request.OpeningBalance is { } opening && opening != 0)
        {
            await accounts.PostOpeningAsync(PartyTypes.Supplier, businessId, supplier.Id,
                new OpeningBalanceRequest(opening, request.OpeningBalanceDate ?? BusinessCalendar.Today(clock)), cancellationToken).ConfigureAwait(false);
            await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(businessId, supplier.Id, cancellationToken).ConfigureAwait(false);
    }

    internal static PartyDetails Details(
        string legalName, string? tradeName, string? gstin, string stateCode, string? address, string? contactPerson, string? phone, string? email,
        string? whatsApp, string? sms, bool whatsAppConsent, bool smsConsent, int creditPeriodDays) =>
        new(legalName, tradeName, gstin, stateCode, address, contactPerson, phone, email, whatsApp, sms, whatsAppConsent, smsConsent, creditPeriodDays);

    public async Task<SupplierDto> UpdateAsync(Guid businessId, Guid supplierId, UpdateSupplierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAnywhereAsync(Permissions.SuppliersManage, businessId, cancellationToken).ConfigureAwait(false);
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId && s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Supplier");
        db.Entry(supplier).Property(s => s.RowVersion).OriginalValue = request.RowVersion;
        var before = new { supplier.Name, supplier.Gstin, supplier.StateCode, supplier.IsActive, supplier.CreditPeriodDays, supplier.WhatsAppConsent, supplier.SmsConsent };
        try
        {
            supplier.Update(Details(request.Name, request.TradeName, request.Gstin, request.StateCode, request.Address, request.ContactPerson, request.Phone,
                request.Email, request.WhatsAppNumber, request.SmsNumber, request.WhatsAppConsent, request.SmsConsent, request.CreditPeriodDays), clock.GetUtcNow());
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        supplier.SetActive(request.IsActive);
        audit.Record("supplier.updated", "supplier", supplier.Id, businessId, details: new
        {
            before,
            after = new { supplier.Name, supplier.Gstin, supplier.StateCode, supplier.IsActive, supplier.CreditPeriodDays, supplier.WhatsAppConsent, supplier.SmsConsent },
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(businessId, supplier.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurchaseSettingsDto> SettingsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAnywhereAsync(Permissions.PurchasesView, businessId, cancellationToken).ConfigureAwait(false);
        var s = await LoadSettingsAsync(businessId, cancellationToken).ConfigureAwait(false);
        return new PurchaseSettingsDto(s.CostReasonThresholdPercent, s.CostApprovalThresholdPercent, s.AllowLossLeader, s.RowVersion);
    }

    /// <summary>Only those who approve purchases may change the thresholds that send purchases to them.</summary>
    public async Task<PurchaseSettingsDto> UpdateSettingsAsync(Guid businessId, PurchaseSettingsDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.PurchasesApprove, businessId, null, cancellationToken).ConfigureAwait(false);
        var settings = await db.PurchaseSettings.FirstOrDefaultAsync(s => s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Purchase settings");
        db.Entry(settings).Property(s => s.RowVersion).OriginalValue = request.RowVersion;
        var before = new { settings.CostReasonThresholdPercent, settings.CostApprovalThresholdPercent, settings.AllowLossLeader };
        try
        {
            settings.Change(request.CostReasonThresholdPercent, request.CostApprovalThresholdPercent, request.AllowLossLeader);
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        audit.Record("purchase_settings.changed", "purchase_settings", businessId, businessId, details: new { before, after = request });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new PurchaseSettingsDto(settings.CostReasonThresholdPercent, settings.CostApprovalThresholdPercent, settings.AllowLossLeader, settings.RowVersion);
    }

    internal async Task<PurchaseSettings> LoadSettingsAsync(Guid businessId, CancellationToken cancellationToken) =>
        await db.PurchaseSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
        ?? PurchaseSettings.Default(businessId);

    internal static SupplierDto ToDto(Supplier s, (decimal Balance, decimal Overdue) account) => new(
        s.Id, s.Code, s.Name, s.Gstin, s.StateCode, s.Address, s.Phone, s.IsActive, s.RowVersion, s.TradeName, s.ContactPerson, s.Email, s.WhatsAppNumber,
        s.SmsNumber, s.WhatsAppConsent, s.SmsConsent, s.CreditPeriodDays, account.Balance, account.Overdue);

    private async Task RequireAnywhereAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        var businesses = await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false);
        if (!businesses.Contains(businessId))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }
}
