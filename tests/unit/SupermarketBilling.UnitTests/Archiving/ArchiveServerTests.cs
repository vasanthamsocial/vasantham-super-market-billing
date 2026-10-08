using SupermarketBilling.Domain.Archiving;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.UnitTests.Archiving;

public sealed class ArchiveServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 2, 5, 0, 0, TimeSpan.Zero);
    private static readonly Guid Business = Guid.NewGuid();
    private static readonly Guid Store = Guid.NewGuid();

    [Fact]
    public void A_grant_covers_only_its_business_store_financial_year_and_reports()
    {
        var grant = ArchiveGrant.Grant(Guid.NewGuid(), ArchiveRoles.ReportUser, Business, Store, 2026, ["sales-summary", "gst-by-rate"], null, Now);
        Assert.True(grant.Covers(ArchivePermissions.Reports, Business, Store, new DateOnly(2026, 4, 1), "sales-summary"));
        Assert.True(grant.Covers(ArchivePermissions.Reports, Business, Store, new DateOnly(2027, 3, 31), "gst-by-rate"));
        Assert.False(grant.Covers(ArchivePermissions.Reports, Business, Store, new DateOnly(2026, 3, 31), "sales-summary")); // 2025-26
        Assert.False(grant.Covers(ArchivePermissions.Reports, Business, Guid.NewGuid(), null, "sales-summary"));
        Assert.False(grant.Covers(ArchivePermissions.Reports, Guid.NewGuid(), Store, null, null));
        Assert.False(grant.Covers(ArchivePermissions.Reports, Business, Store, null, "stock-valuation"));
        Assert.False(grant.Covers(ArchivePermissions.Import, Business, Store));

        var everything = ArchiveGrant.Grant(Guid.NewGuid(), ArchiveRoles.Owner, null, null, null, null, null, Now);
        Assert.True(everything.Covers(ArchivePermissions.ApproveOwner, Guid.NewGuid()));
    }

    [Fact]
    public void Grants_are_well_formed()
    {
        Assert.Equal("archive_grant.store_needs_business",
            Assert.Throws<DomainException>(() => ArchiveGrant.Grant(Guid.NewGuid(), ArchiveRoles.ReportUser, null, Store, null, null, null, Now)).Code);
        Assert.Equal("archive_grant.year_invalid",
            Assert.Throws<DomainException>(() => ArchiveGrant.Grant(Guid.NewGuid(), ArchiveRoles.ReportUser, null, null, 26, null, null, Now)).Code);
        Assert.Equal("archive_grant.reports_invalid",
            Assert.Throws<DomainException>(() => ArchiveGrant.Grant(Guid.NewGuid(), ArchiveRoles.ReportUser, null, null, null, ["Sales Summary"], null, Now)).Code);
        Assert.Equal("archive_role.unknown", Assert.Throws<DomainException>(() => ArchiveRoles.Get("owner")).Code);
        Assert.Equal("2026-27", FinancialYears.Label(2026));
        Assert.Equal((2025, 2026), (FinancialYears.Of(new DateOnly(2026, 3, 31)), FinancialYears.Of(new DateOnly(2026, 4, 1))));
    }

    [Fact]
    public void An_imported_month_is_approved_by_the_accountant_then_by_someone_else_as_owner()
    {
        var import = ArchiveImport.Record(Guid.NewGuid(), Business, "SMKT", "Test", new DateOnly(2026, 10, 1), Guid.NewGuid(), new string('a', 64), 100, "{}", "{}", Guid.NewGuid(), Now);
        var accountant = Guid.NewGuid();
        Assert.Equal("archive_import.not_waiting_for_owner", Assert.Throws<DomainException>(() => import.ApproveAsOwner(Guid.NewGuid(), null, Now)).Code);
        import.ApproveAsAccountant(accountant, "Agrees with GSTR-1", Now);
        Assert.Equal("archive_import.same_approver", Assert.Throws<DomainException>(() => import.ApproveAsOwner(accountant, null, Now)).Code);
        import.ApproveAsOwner(Guid.NewGuid(), null, Now);
        Assert.Equal(ArchiveImportStatus.Approved, import.Status);
        Assert.Equal("archive_import.not_waiting_for_accountant", Assert.Throws<DomainException>(() => import.ApproveAsAccountant(accountant, null, Now)).Code);
    }

    [Fact]
    public void Masters_are_refreshed_only_by_a_newer_month()
    {
        var master = ArchiveMaster.Create(Business, "stores", Store.ToString(), "{\"name\":\"Old\"}", Guid.NewGuid(), new DateOnly(2026, 10, 1));
        master.Refresh("{\"name\":\"Older\"}", Guid.NewGuid(), new DateOnly(2026, 9, 1));
        Assert.Equal("{\"name\":\"Old\"}", master.Data);
        master.Refresh("{\"name\":\"New\"}", Guid.NewGuid(), new DateOnly(2026, 11, 1));
        Assert.Equal("{\"name\":\"New\"}", master.Data);
    }
}
