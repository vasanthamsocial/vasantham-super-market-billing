using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Identity;
using SupermarketBilling.Infrastructure.Persistence;
using SupermarketBilling.Infrastructure.Security;

namespace SupermarketBilling.Infrastructure.Organisation;

/// <summary>
/// Businesses and stores. Anything outside the caller's businesses is reported as "not found" rather than
/// "forbidden", so other businesses' existence is not revealed.
/// </summary>
public sealed class OrganisationService(
    SupermarketBillingDbContext db,
    IAccessControl access,
    ICurrentUser currentUser,
    AuditRecorder audit,
    IOptions<SecurityOptions> options,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<BusinessDto>> ListBusinessesAsync(CancellationToken cancellationToken)
    {
        var visible = await access.BusinessesWithPermissionAsync(Permissions.StoresView, cancellationToken).ConfigureAwait(false);
        var businesses = await db.Businesses.AsNoTracking().Where(b => visible.Contains(b.Id)).OrderBy(b => b.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return businesses.Select(ToDto).ToList();
    }

    public async Task<BusinessDto> GetBusinessAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var visible = await access.BusinessesWithPermissionAsync(Permissions.StoresView, cancellationToken).ConfigureAwait(false);
        var business = visible.Contains(businessId)
            ? await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId, cancellationToken).ConfigureAwait(false)
            : null;
        return ToDto(business ?? throw AppException.NotFound("Business"));
    }

    /// <summary>Adds another legal business (licence permitting). The creator becomes its owner.</summary>
    public async Task<BusinessDto> CreateBusinessAsync(CreateBusinessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = await access.BusinessesWithPermissionAsync(Permissions.BusinessesCreate, cancellationToken).ConfigureAwait(false);
        if (allowed.Count == 0)
        {
            throw AppException.Forbidden("Only an owner can add a business.");
        }

        var count = await db.Businesses.CountAsync(cancellationToken).ConfigureAwait(false);
        if (count >= options.Value.MaxBusinesses)
        {
            throw AppException.Conflict("license.max_businesses", $"This installation is licensed for {options.Value.MaxBusinesses} business(es). Contact your supplier to add more.");
        }

        var now = clock.GetUtcNow();
        var business = Business.Create(request.Code, request.LegalName, request.TradeName, request.StateCode, request.Gstin, request.Address, now);
        db.Businesses.Add(business);
        var grant = RoleAssignment.Grant(currentUser.UserId, Roles.Owner, business.Id, null, currentUser.UserId, null, now);
        db.RoleAssignments.Add(grant);
        Catalog.CatalogService.SeedDefaultUnits(db, business.Id, now);
        db.TaxRegistrations.Add(Domain.Tax.TaxRegistration.Initial(
            business.Id, Identity.SetupService.TaxModeFor(request), business.Gstin, Catalog.BusinessCalendar.Today(clock), currentUser.UserId, now));
        db.InventorySettings.Add(Domain.Inventory.InventorySettings.Create(business.Id, Domain.Inventory.ValuationMethods.Fifo));
        db.PurchaseSettings.Add(Domain.Purchases.PurchaseSettings.Default(business.Id));
        audit.Record("business.created", "business", business.Id, business.Id, details: new { business.Code, business.LegalName, business.Gstin });
        audit.Record("role.granted", "role_assignment", grant.Id, business.Id, details: new { role = Roles.Owner, via = "business_created" });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(business);
    }

    public async Task<BusinessDto> UpdateBusinessAsync(Guid businessId, UpdateBusinessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.BusinessesManage, businessId, null, cancellationToken).ConfigureAwait(false);
        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Business");
        db.Entry(business).Property(b => b.RowVersion).OriginalValue = request.RowVersion;

        var before = ToDto(business);
        business.Update(request.LegalName, request.TradeName, request.StateCode, request.Gstin, request.Address, request.RequireMfaForPrivilegedUsers);
        business.SetPriceApprovalPolicy(request.RequirePriceApproval);
        audit.Record("business.updated", "business", business.Id, business.Id, details: new { before, after = ToDto(business) });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(business);
    }

    public async Task<IReadOnlyList<StoreDto>> ListStoresAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var businessWide = await access.HasPermissionAsync(Permissions.StoresView, businessId, null, cancellationToken).ConfigureAwait(false);
        var stores = await db.Stores.AsNoTracking().Where(s => s.BusinessId == businessId).OrderBy(s => s.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!businessWide)
        {
            // Store-limited staff see only their own stores.
            var visible = new List<Store>();
            foreach (var store in stores)
            {
                if (await access.HasPermissionAsync(Permissions.StoresView, businessId, store.Id, cancellationToken).ConfigureAwait(false))
                {
                    visible.Add(store);
                }
            }

            stores = visible;
            if (stores.Count == 0)
            {
                throw AppException.NotFound("Business");
            }
        }

        return stores.Select(ToDto).ToList();
    }

    public async Task<StoreDto> CreateStoreAsync(Guid businessId, CreateStoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.StoresManage, businessId, null, cancellationToken).ConfigureAwait(false);
        var store = Store.Create(businessId, request.Code, request.Name, request.StateCode, request.Gstin, request.Address, clock.GetUtcNow());
        db.Stores.Add(store);
        audit.Record("store.created", "store", store.Id, businessId, store.Id, details: new { store.Code, store.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(store);
    }

    public async Task<StoreDto> UpdateStoreAsync(Guid businessId, Guid storeId, UpdateStoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.StoresManage, businessId, storeId, cancellationToken).ConfigureAwait(false);
        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == storeId && s.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Store");
        db.Entry(store).Property(s => s.RowVersion).OriginalValue = request.RowVersion;

        var before = ToDto(store);
        store.Update(request.Name, request.StateCode, request.Gstin, request.Address, request.IsActive);
        audit.Record("store.updated", "store", store.Id, businessId, store.Id, details: new { before, after = ToDto(store) });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(store);
    }

    /// <summary>
    /// Throws NotFound when the caller has no access to the business at all, Forbidden when they can see it but
    /// lack the specific permission.
    /// </summary>
    internal async Task RequireAsync(string permission, Guid businessId, Guid? storeId, CancellationToken cancellationToken)
    {
        if (await access.HasPermissionAsync(permission, businessId, storeId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var visible = await access.BusinessesWithPermissionAsync(Permissions.StoresView, cancellationToken).ConfigureAwait(false);
        throw visible.Contains(businessId) ? AppException.Forbidden() : AppException.NotFound("Business");
    }

    internal static BusinessDto ToDto(Business b) =>
        new(b.Id, b.Code, b.LegalName, b.TradeName, b.StateCode, b.Gstin, b.Address, b.IsActive, b.RequireMfaForPrivilegedUsers, b.RowVersion, b.RequirePriceApproval);

    internal static StoreDto ToDto(Store s) =>
        new(s.Id, s.BusinessId, s.Code, s.Name, s.StateCode, s.Gstin, s.Address, s.TimeZone, s.IsActive, s.RowVersion);
}
