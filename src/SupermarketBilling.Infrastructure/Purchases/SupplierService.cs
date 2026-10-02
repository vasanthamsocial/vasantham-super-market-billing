using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Purchases;

/// <summary>Suppliers and the business's purchasing settings.</summary>
public sealed class SupplierService(
    SupermarketBillingDbContext db,
    OrganisationService organisation,
    IAccessControl access,
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

        return await query.OrderBy(s => s.Name).Take(500).Select(s => ToDto(s)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupplierDto> CreateAsync(Guid businessId, CreateSupplierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAnywhereAsync(Permissions.SuppliersManage, businessId, cancellationToken).ConfigureAwait(false);
        var supplier = Supplier.Create(businessId, request.Code, request.Name, request.Gstin, request.StateCode, request.Address, request.Phone, clock.GetUtcNow());
        if (await db.Suppliers.AnyAsync(s => s.BusinessId == businessId && s.Code == supplier.Code, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.Conflict("supplier.code_taken", $"Supplier code {supplier.Code} is already used.");
        }

        db.Suppliers.Add(supplier);
        audit.Record("supplier.created", "supplier", supplier.Id, businessId, details: new { supplier.Code, supplier.Name, supplier.Gstin });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(supplier);
    }

    public async Task<SupplierDto> UpdateAsync(Guid businessId, Guid supplierId, UpdateSupplierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAnywhereAsync(Permissions.SuppliersManage, businessId, cancellationToken).ConfigureAwait(false);
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId && s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Supplier");
        db.Entry(supplier).Property(s => s.RowVersion).OriginalValue = request.RowVersion;
        var before = new { supplier.Name, supplier.Gstin, supplier.StateCode, supplier.IsActive };
        supplier.Update(request.Name, request.Gstin, request.StateCode, request.Address, request.Phone);
        supplier.SetActive(request.IsActive);
        audit.Record("supplier.updated", "supplier", supplier.Id, businessId, details: new { before, after = new { supplier.Name, supplier.Gstin, supplier.StateCode, supplier.IsActive } });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(supplier);
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

    internal static SupplierDto ToDto(Supplier s) => new(s.Id, s.Code, s.Name, s.Gstin, s.StateCode, s.Address, s.Phone, s.IsActive, s.RowVersion);

    private async Task RequireAnywhereAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        var businesses = await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false);
        if (!businesses.Contains(businessId))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }
}
