using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Inventory;

namespace SupermarketBilling.UnitTests.Inventory;

public sealed class StockOriginTests
{
    [Theory]
    [InlineData("GST_TAX_INVOICE", "GST")]
    [InlineData("IMPORT", "GST")]
    [InlineData("REVERSE_CHARGE", "GST")]
    [InlineData("BILL_OF_SUPPLY", "NON_GST")]
    [InlineData("UNREGISTERED", "NON_GST")]
    [InlineData("PENDING_DOCUMENT", "OTHER")]
    [InlineData("OTHER", "OTHER")]
    public void A_purchase_gives_its_goods_an_origin_from_the_supplier_document(string classification, string origin) =>
        Assert.Equal(origin, StockOrigins.ForPurchase(classification));

    [Fact]
    public void Goods_from_lots_of_one_origin_keep_it_and_mixed_lots_become_other()
    {
        Assert.Equal("GST", StockOrigins.Of(["GST", "GST"]));
        Assert.Equal("OTHER", StockOrigins.Of(["GST", "NON_GST"]));
        Assert.Equal("NON_GST", StockOrigins.Of(["NON_GST"]));
    }

    [Fact]
    public void A_lot_has_a_known_origin_and_other_by_default()
    {
        var now = new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal("OTHER", CostLayer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, 1m, 10m, now).Origin);
        Assert.Equal("GST", CostLayer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, 1m, 10m, now, StockOrigins.Gst).Origin);
        Assert.Equal("stock.origin_invalid", Assert.Throws<DomainException>(() => CostLayer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, 1m, 10m, now, "IMPORTED")).Code);
    }
}
