using SupermarketBilling.Domain.Catalog;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.UnitTests.Catalog;

public sealed class CatalogAndTaxTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static Product NewProduct(string supply = SupplyTypes.Taxable, decimal gst = 5, decimal cess = 0, string hsn = "1101") =>
        Product.Create(Guid.NewGuid(), "ATTA-5", "Whole Wheat Atta", null, null, null, Guid.NewGuid(), hsn, supply, gst, cess, false, false, false, false, Now);

    [Theory]
    [InlineData("8901058851427")] // EAN-13
    [InlineData("96385074")] // EAN-8
    [InlineData("036000291452")] // UPC-A
    [InlineData("10012345678902")] // GTIN-14
    public void Real_gs1_barcodes_are_accepted(string code)
    {
        var barcode = VariantBarcode.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), code, Now);

        Assert.Equal(BarcodeTypes.Gs1, barcode.Type);
    }

    [Fact]
    public void Mis_scanned_barcodes_fail_the_check_digit()
    {
        var error = Assert.Throws<DomainException>(() => VariantBarcode.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "8901058851428", Now));

        Assert.Equal("barcode.check_digit", error.Code);
    }

    [Fact]
    public void Internal_codes_are_accepted_but_must_contain_a_letter()
    {
        Assert.Equal(BarcodeTypes.Internal, VariantBarcode.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "loose-rice".Replace("-", "", StringComparison.Ordinal), Now).Type);
        Assert.Throws<DomainException>(() => VariantBarcode.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "12345", Now));
    }

    [Theory]
    [InlineData(SupplyTypes.Taxable, 0, 0, "product.rate_required")]
    [InlineData(SupplyTypes.Exempt, 5, 0, "product.rate_not_allowed")]
    [InlineData(SupplyTypes.NilRated, 0, 1, "product.rate_not_allowed")]
    [InlineData(SupplyTypes.Taxable, 101, 0, "product.rate_invalid")]
    [InlineData(SupplyTypes.Taxable, 5.0001, 0, "product.rate_invalid")]
    public void Tax_rates_must_match_the_supply_type(string supply, double gst, double cess, string expected)
    {
        var error = Assert.Throws<DomainException>(() => NewProduct(supply, (decimal)gst, (decimal)cess));

        Assert.Equal(expected, error.Code);
    }

    [Theory]
    [InlineData("110")]
    [InlineData("11011")]
    [InlineData("ABCD")]
    public void Hsn_must_be_4_6_or_8_digits(string hsn)
    {
        Assert.Equal("product.hsn_invalid", Assert.Throws<DomainException>(() => NewProduct(hsn: hsn)).Code);
    }

    [Fact]
    public void Tracking_expiry_implies_tracking_batches()
    {
        var product = Product.Create(Guid.NewGuid(), "MILK", "Milk", null, null, null, Guid.NewGuid(), "0401", SupplyTypes.Exempt, 0, 0, false, false, true, false, Now);

        Assert.True(product.TracksBatches);
    }

    [Fact]
    public void Pack_units_convert_exactly_to_base_units()
    {
        var box = VariantUnit.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 12m, isBase: false, Now);
        var bag = VariantUnit.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0.5m, isBase: false, Now);

        Assert.Equal(36m, box.ToBase(3));
        Assert.Equal(0.75m, bag.ToBase(1.5m));
        Assert.Throws<DomainException>(() => VariantUnit.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2m, isBase: true, Now));
        Assert.Throws<DomainException>(() => VariantUnit.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0m, isBase: false, Now));
    }

    [Fact]
    public void Units_limit_quantity_precision()
    {
        var pieces = Unit.Create(Guid.NewGuid(), "pcs", "Pieces", 0, Now);
        var kg = Unit.Create(Guid.NewGuid(), "KG", "Kilogram", 3, Now);

        Assert.Throws<DomainException>(() => pieces.ValidateQuantity(1.5m));
        kg.ValidateQuantity(1.255m);
        Assert.Throws<DomainException>(() => kg.ValidateQuantity(1.2555m));
    }

    [Fact]
    public void Gst_modes_need_a_valid_gstin_and_unregistered_mode_drops_it()
    {
        Assert.Throws<DomainException>(() => TaxRegistration.Initial(Guid.NewGuid(), TaxRegistrationModes.GstRegular, null, Today, Guid.NewGuid(), Now));
        var unregistered = TaxRegistration.Initial(Guid.NewGuid(), TaxRegistrationModes.NotGstRegistered, "27AAPFU0939F1ZV", Today, Guid.NewGuid(), Now);
        Assert.Null(unregistered.Gstin);
    }

    [Fact]
    public void Tax_mode_changes_cannot_be_backdated_or_overlap_history()
    {
        var current = TaxRegistration.Initial(Guid.NewGuid(), TaxRegistrationModes.GstComposition, "27AAPFU0939F1ZV", new DateOnly(2026, 4, 1), Guid.NewGuid(), Now);

        Assert.Equal("tax_mode.backdated", Assert.Throws<DomainException>(() =>
            TaxRegistration.ValidateChange(current, TaxRegistrationModes.GstRegular, Today.AddDays(-1), Today, "Turnover crossed the composition limit")).Code);
        Assert.Equal("tax_mode.reason_required", Assert.Throws<DomainException>(() =>
            TaxRegistration.ValidateChange(current, TaxRegistrationModes.GstRegular, Today, Today, "short")).Code);
        TaxRegistration.ValidateChange(current, TaxRegistrationModes.GstRegular, Today, Today, "Turnover crossed the composition limit");
    }

    [Fact]
    public void The_entry_in_force_is_the_latest_that_has_started()
    {
        var business = Guid.NewGuid();
        var first = TaxRegistration.Initial(business, TaxRegistrationModes.GstComposition, "27AAPFU0939F1ZV", new DateOnly(2026, 4, 1), Guid.NewGuid(), Now);
        var second = TaxRegistration.Change(first, TaxRegistrationModes.GstRegular, "27AAPFU0939F1ZV", new DateOnly(2026, 11, 1), Today,
            "Turnover crossed the composition limit", "REG-06", Guid.NewGuid(), Guid.NewGuid(), Now);
        var history = new[] { first, second };

        Assert.Null(TaxRegistration.InForce(history, new DateOnly(2026, 3, 31)));
        Assert.Same(first, TaxRegistration.InForce(history, new DateOnly(2026, 10, 31)));
        Assert.Same(second, TaxRegistration.InForce(history, new DateOnly(2026, 11, 1)));
    }
}
