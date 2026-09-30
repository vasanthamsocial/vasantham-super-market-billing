using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Organisation;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Catalog;

/// <summary>
/// The business's catalogue: units, categories, brands, customer groups, products, variants, packs, barcodes and
/// MRPs. The catalogue is shared by all the business's stores, so staff limited to one store can still use it.
/// </summary>
public sealed class CatalogService(
    SupermarketBillingDbContext db,
    IAccessControl access,
    OrganisationService organisation,
    AuditRecorder audit,
    TimeProvider clock)
{
    private const int MaxPageSize = 200;

    public static void SeedDefaultUnits(SupermarketBillingDbContext db, Guid businessId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        foreach (var (code, name, decimals) in Domain.Catalog.Unit.Defaults)
        {
            db.Units.Add(Domain.Catalog.Unit.Create(businessId, code, name, decimals, now));
        }
    }

    // ---- Masters ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<UnitDto>> ListUnitsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        return await db.Units.AsNoTracking().Where(u => u.BusinessId == businessId).OrderBy(u => u.Code)
            .Select(u => new UnitDto(u.Id, u.Code, u.Name, u.DecimalPlaces, u.IsActive)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UnitDto> CreateUnitAsync(Guid businessId, CreateUnitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var unit = Domain.Catalog.Unit.Create(businessId, request.Code, request.Name, request.DecimalPlaces, clock.GetUtcNow());
        db.Units.Add(unit);
        audit.Record("catalog.unit_created", "unit", unit.Id, businessId, details: new { unit.Code, unit.Name, unit.DecimalPlaces });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new UnitDto(unit.Id, unit.Code, unit.Name, unit.DecimalPlaces, unit.IsActive);
    }

    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        return await db.Categories.AsNoTracking().Where(c => c.BusinessId == businessId).OrderBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.ParentId, c.Name, c.IsActive)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CategoryDto> CreateCategoryAsync(Guid businessId, CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        if (request.ParentId is { } parent && !await db.Categories.AnyAsync(c => c.Id == parent && c.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Parent category");
        }

        var category = Category.Create(businessId, request.ParentId, request.Name, clock.GetUtcNow());
        db.Categories.Add(category);
        audit.Record("catalog.category_created", "category", category.Id, businessId, details: new { category.Name, category.ParentId });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new CategoryDto(category.Id, category.ParentId, category.Name, category.IsActive);
    }

    public async Task<IReadOnlyList<BrandDto>> ListBrandsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        return await db.Brands.AsNoTracking().Where(b => b.BusinessId == businessId).OrderBy(b => b.Name)
            .Select(b => new BrandDto(b.Id, b.Name, b.IsActive)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrandDto> CreateBrandAsync(Guid businessId, CreateBrandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var brand = Brand.Create(businessId, request.Name, clock.GetUtcNow());
        db.Brands.Add(brand);
        audit.Record("catalog.brand_created", "brand", brand.Id, businessId, details: new { brand.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new BrandDto(brand.Id, brand.Name, brand.IsActive);
    }

    public async Task<IReadOnlyList<CustomerGroupDto>> ListCustomerGroupsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        return await db.CustomerGroups.AsNoTracking().Where(g => g.BusinessId == businessId).OrderBy(g => g.Code)
            .Select(g => new CustomerGroupDto(g.Id, g.Code, g.Name, g.IsActive)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CustomerGroupDto> CreateCustomerGroupAsync(Guid businessId, CreateCustomerGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await organisation.RequireAsync(Permissions.PricesManage, businessId, null, cancellationToken).ConfigureAwait(false);
        var group = CustomerGroup.Create(businessId, request.Code, request.Name, clock.GetUtcNow());
        db.CustomerGroups.Add(group);
        audit.Record("catalog.customer_group_created", "customer_group", group.Id, businessId, details: new { group.Code, group.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new CustomerGroupDto(group.Id, group.Code, group.Name, group.IsActive);
    }

    // ---- Products -----------------------------------------------------------------------------------------

    /// <summary>Search by code, name or barcode. Results are paged (at most 200).</summary>
    public async Task<IReadOnlyList<ProductSummaryDto>> SearchProductsAsync(
        Guid businessId, string? search, bool includeInactive, int skip, int take, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        var query = db.Products.AsNoTracking().Where(p => p.BusinessId == businessId);
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var barcode = term.ToUpperInvariant();
            var pattern = $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            var byBarcode = db.VariantBarcodes.Where(b => b.BusinessId == businessId && b.IsActive && b.Code == barcode)
                .Join(db.ProductVariants, b => b.VariantId, v => v.Id, (b, v) => v.ProductId);
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Code, pattern) || byBarcode.Contains(p.Id));
        }

        return await (
                from p in query
                from c in db.Categories.Where(c => c.Id == p.CategoryId).DefaultIfEmpty()
                from b in db.Brands.Where(b => b.Id == p.BrandId).DefaultIfEmpty()
                orderby p.Name
                select new ProductSummaryDto(
                    p.Id, p.Code, p.Name, c == null ? null : c.Name, b == null ? null : b.Name, p.HsnSac, p.SupplyType, p.GstRatePercent,
                    db.ProductVariants.Count(v => v.ProductId == p.Id), p.IsActive))
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProductDetailDto> GetProductAsync(Guid businessId, Guid productId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && p.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Product");
        return await ToDetailAsync(product, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProductDetailDto> CreateProductAsync(Guid businessId, CreateProductRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        await EnsureReferencesAsync(businessId, request.BaseUnitId, request.CategoryId, request.BrandId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        var product = Product.Create(businessId, request.Code, request.Name, request.PrintName, request.CategoryId, request.BrandId, request.BaseUnitId,
            request.HsnSac, request.SupplyType, request.GstRatePercent, request.CessRatePercent, request.IsWeighed, request.TracksBatches,
            request.TracksExpiry, request.TracksSerials, now);
        db.Products.Add(product);

        var v = request.Variant;
        var variant = ProductVariant.Create(businessId, product.Id, string.IsNullOrWhiteSpace(v?.Code) ? product.Code : v.Code,
            string.IsNullOrWhiteSpace(v?.Name) ? product.Name : v.Name, now);
        db.ProductVariants.Add(variant);
        var baseUnit = VariantUnit.Create(businessId, variant.Id, product.BaseUnitId, 1m, isBase: true, now);
        db.VariantUnits.Add(baseUnit);
        AddOptionalBarcodeAndMrp(businessId, variant.Id, baseUnit.Id, v?.Barcode, v?.Mrp, now);

        audit.Record("catalog.product_created", "product", product.Id, businessId,
            details: new { product.Code, product.Name, product.HsnSac, product.SupplyType, product.GstRatePercent, product.CessRatePercent, variant = variant.Code });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ToDetailAsync(product, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProductDetailDto> UpdateProductAsync(Guid businessId, Guid productId, UpdateProductRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Product");
        await EnsureReferencesAsync(businessId, product.BaseUnitId, request.CategoryId, request.BrandId, cancellationToken).ConfigureAwait(false);
        db.Entry(product).Property(p => p.RowVersion).OriginalValue = request.RowVersion;

        var before = new { product.Name, product.HsnSac, product.SupplyType, product.GstRatePercent, product.CessRatePercent, product.IsActive };
        product.Update(request.Name, request.PrintName, request.CategoryId, request.BrandId, request.HsnSac, request.SupplyType, request.GstRatePercent,
            request.CessRatePercent, request.IsWeighed, request.TracksBatches, request.TracksExpiry, request.TracksSerials, request.IsActive);
        audit.Record("catalog.product_updated", "product", product.Id, businessId, details: new
        {
            before,
            after = new { product.Name, product.HsnSac, product.SupplyType, product.GstRatePercent, product.CessRatePercent, product.IsActive },
        });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ToDetailAsync(product, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProductDetailDto> AddVariantAsync(Guid businessId, Guid productId, CreateVariantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Product");
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            throw AppException.Validation("variant.code_name_required", "A new variant needs its own code and name.");
        }

        var now = clock.GetUtcNow();
        var variant = ProductVariant.Create(businessId, productId, request.Code, request.Name, now);
        db.ProductVariants.Add(variant);
        var baseUnit = VariantUnit.Create(businessId, variant.Id, product.BaseUnitId, 1m, isBase: true, now);
        db.VariantUnits.Add(baseUnit);
        AddOptionalBarcodeAndMrp(businessId, variant.Id, baseUnit.Id, request.Barcode, request.Mrp, now);
        audit.Record("catalog.variant_created", "product_variant", variant.Id, businessId, details: new { product = product.Code, variant.Code, variant.Name });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return await ToDetailAsync(product, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VariantUnitDto> AddVariantUnitAsync(Guid businessId, Guid variantId, AddVariantUnitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var variant = await VariantAsync(businessId, variantId, cancellationToken).ConfigureAwait(false);
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UnitId && u.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Unit");
        var pack = VariantUnit.Create(businessId, variant.Id, unit.Id, request.FactorToBase, isBase: false, clock.GetUtcNow());
        db.VariantUnits.Add(pack);
        audit.Record("catalog.pack_added", "variant_unit", pack.Id, businessId, details: new { variant = variant.Code, unit = unit.Code, pack.FactorToBase });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new VariantUnitDto(pack.Id, unit.Id, unit.Code, pack.FactorToBase, pack.IsBase);
    }

    public async Task<BarcodeDto> AddBarcodeAsync(Guid businessId, Guid variantId, AddBarcodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var variant = await VariantAsync(businessId, variantId, cancellationToken).ConfigureAwait(false);
        await EnsurePackAsync(variant.Id, request.VariantUnitId, cancellationToken).ConfigureAwait(false);
        var barcode = VariantBarcode.Create(businessId, variant.Id, request.VariantUnitId, request.Code, clock.GetUtcNow());
        await EnsureBarcodeFreeAsync(businessId, barcode.Code, cancellationToken).ConfigureAwait(false);
        db.VariantBarcodes.Add(barcode);
        audit.Record("catalog.barcode_added", "variant_barcode", barcode.Id, businessId, details: new { variant = variant.Code, barcode.Code });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new BarcodeDto(barcode.Id, barcode.VariantUnitId, barcode.Code, barcode.Type, barcode.IsActive);
    }

    public async Task DeactivateBarcodeAsync(Guid businessId, Guid barcodeId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var barcode = await db.VariantBarcodes.FirstOrDefaultAsync(b => b.Id == barcodeId && b.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Barcode");
        barcode.Deactivate();
        audit.Record("catalog.barcode_deactivated", "variant_barcode", barcode.Id, businessId, details: new { barcode.Code });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MrpDto> AddMrpAsync(Guid businessId, Guid variantId, AddMrpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var variant = await VariantAsync(businessId, variantId, cancellationToken).ConfigureAwait(false);
        await EnsurePackAsync(variant.Id, request.VariantUnitId, cancellationToken).ConfigureAwait(false);
        var mrp = VariantMrp.Create(businessId, variant.Id, request.VariantUnitId, request.Mrp, request.EffectiveFrom ?? BusinessCalendar.Today(clock), clock.GetUtcNow());
        db.VariantMrps.Add(mrp);
        audit.Record("catalog.mrp_added", "variant_mrp", mrp.Id, businessId, details: new { variant = variant.Code, mrp.Mrp, mrp.EffectiveFrom });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return new MrpDto(mrp.Id, mrp.VariantUnitId, mrp.Mrp, mrp.EffectiveFrom, mrp.IsActive);
    }

    public async Task DeactivateMrpAsync(Guid businessId, Guid mrpId, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogManage, businessId, cancellationToken).ConfigureAwait(false);
        var mrp = await db.VariantMrps.FirstOrDefaultAsync(m => m.Id == mrpId && m.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("MRP");
        mrp.Deactivate();
        audit.Record("catalog.mrp_deactivated", "variant_mrp", mrp.Id, businessId, details: new { mrp.Mrp });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a scanned barcode is: used by billing and goods receipt.</summary>
    public async Task<BarcodeLookupDto> LookupBarcodeAsync(Guid businessId, string code, CancellationToken cancellationToken)
    {
        await RequireAsync(Permissions.CatalogView, businessId, cancellationToken).ConfigureAwait(false);
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        var row = await (
                from b in db.VariantBarcodes.AsNoTracking()
                join v in db.ProductVariants.AsNoTracking() on b.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                join vu in db.VariantUnits.AsNoTracking() on b.VariantUnitId equals vu.Id
                join u in db.Units.AsNoTracking() on vu.UnitId equals u.Id
                where b.BusinessId == businessId && b.Code == normalized && b.IsActive && v.IsActive && p.IsActive
                select new { b, v, p, vu, u })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("Barcode");

        var mrps = await db.VariantMrps.AsNoTracking()
            .Where(m => m.VariantUnitId == row.vu.Id && m.IsActive)
            .OrderByDescending(m => m.EffectiveFrom).Select(m => m.Mrp)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new BarcodeLookupDto(row.p.Id, row.p.Name, row.p.PrintName, row.v.Id, row.v.Name, row.vu.Id, row.u.Code, row.vu.FactorToBase,
            row.b.Code, row.p.SupplyType, row.p.GstRatePercent, row.p.CessRatePercent, row.p.IsWeighed, mrps);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>The catalogue is business-wide: holding the permission for any store of the business is enough.</summary>
    internal async Task RequireAsync(string permission, Guid businessId, CancellationToken cancellationToken)
    {
        var businesses = await access.BusinessesWithPermissionAsync(permission, cancellationToken).ConfigureAwait(false);
        if (!businesses.Contains(businessId))
        {
            await organisation.RequireAsync(permission, businessId, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private void AddOptionalBarcodeAndMrp(Guid businessId, Guid variantId, Guid variantUnitId, string? barcode, decimal? mrp, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            db.VariantBarcodes.Add(VariantBarcode.Create(businessId, variantId, variantUnitId, barcode, now));
        }

        if (mrp is { } value)
        {
            db.VariantMrps.Add(VariantMrp.Create(businessId, variantId, variantUnitId, value, BusinessCalendar.Today(clock), now));
        }
    }

    private async Task EnsureReferencesAsync(Guid businessId, Guid baseUnitId, Guid? categoryId, Guid? brandId, CancellationToken cancellationToken)
    {
        if (!await db.Units.AnyAsync(u => u.Id == baseUnitId && u.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Unit");
        }

        if (categoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Category");
        }

        if (brandId is { } b && !await db.Brands.AnyAsync(x => x.Id == b && x.BusinessId == businessId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Brand");
        }
    }

    private async Task EnsureBarcodeFreeAsync(Guid businessId, string code, CancellationToken cancellationToken)
    {
        var owner = await db.VariantBarcodes.AsNoTracking().Where(b => b.BusinessId == businessId && b.Code == code && b.IsActive)
            .Join(db.ProductVariants, b => b.VariantId, v => v.Id, (b, v) => v.Name)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (owner is not null)
        {
            throw AppException.Conflict("barcode.in_use", $"Barcode {code} is already used by '{owner}'. Deactivate it there first if the manufacturer reused it.");
        }
    }

    private async Task<ProductVariant> VariantAsync(Guid businessId, Guid variantId, CancellationToken cancellationToken) =>
        await db.ProductVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == variantId && v.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.NotFound("Variant");

    private async Task EnsurePackAsync(Guid variantId, Guid variantUnitId, CancellationToken cancellationToken)
    {
        if (!await db.VariantUnits.AnyAsync(u => u.Id == variantUnitId && u.VariantId == variantId, cancellationToken).ConfigureAwait(false))
        {
            throw AppException.NotFound("Pack unit");
        }
    }

    private async Task<ProductDetailDto> ToDetailAsync(Product product, CancellationToken cancellationToken)
    {
        var variants = await db.ProductVariants.AsNoTracking().Where(v => v.ProductId == product.Id).OrderBy(v => v.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var variantIds = variants.Select(v => v.Id).ToList();
        var packs = await (
                from vu in db.VariantUnits.AsNoTracking()
                join u in db.Units.AsNoTracking() on vu.UnitId equals u.Id
                where variantIds.Contains(vu.VariantId)
                orderby vu.FactorToBase
                select new { vu.VariantId, Dto = new VariantUnitDto(vu.Id, u.Id, u.Code, vu.FactorToBase, vu.IsBase) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var barcodes = await db.VariantBarcodes.AsNoTracking().Where(b => variantIds.Contains(b.VariantId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var mrps = await db.VariantMrps.AsNoTracking().Where(m => variantIds.Contains(m.VariantId)).OrderByDescending(m => m.EffectiveFrom)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var baseUnitCode = await db.Units.AsNoTracking().Where(u => u.Id == product.BaseUnitId).Select(u => u.Code).FirstAsync(cancellationToken).ConfigureAwait(false);

        return new ProductDetailDto(
            product.Id, product.Code, product.Name, product.PrintName, product.CategoryId, product.BrandId, product.BaseUnitId, baseUnitCode,
            product.HsnSac, product.SupplyType, product.GstRatePercent, product.CessRatePercent, product.IsWeighed, product.TracksBatches,
            product.TracksExpiry, product.TracksSerials, product.IsActive, product.RowVersion,
            variants.Select(v => new VariantDto(
                v.Id, v.Code, v.Name, v.IsActive,
                packs.Where(p => p.VariantId == v.Id).Select(p => p.Dto).ToList(),
                barcodes.Where(b => b.VariantId == v.Id).Select(b => new BarcodeDto(b.Id, b.VariantUnitId, b.Code, b.Type, b.IsActive)).ToList(),
                mrps.Where(m => m.VariantId == v.Id).Select(m => new MrpDto(m.Id, m.VariantUnitId, m.Mrp, m.EffectiveFrom, m.IsActive)).ToList()))
                .ToList());
    }
}
