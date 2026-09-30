using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Infrastructure.Identity;

namespace SupermarketBilling.UnitTests.Identity;

public sealed class GrantPolicyTests
{
    private static readonly Guid BusinessA = Guid.NewGuid();
    private static readonly Guid BusinessB = Guid.NewGuid();
    private static readonly Guid Store1 = Guid.NewGuid();
    private static readonly Guid Store2 = Guid.NewGuid();

    private static ActiveGrant Grant(string role, Guid business, Guid? store = null) => new(Guid.NewGuid(), role, business, store);

    [Fact]
    public void Business_level_checks_need_a_business_wide_grant()
    {
        var storeManager = new[] { Grant(Roles.Manager, BusinessA, Store1) };

        Assert.False(AccessControl.Covers(storeManager, Permissions.UsersManage, BusinessA, null));
        Assert.True(AccessControl.Covers(storeManager, Permissions.UsersManage, BusinessA, Store1));
        Assert.False(AccessControl.Covers(storeManager, Permissions.UsersManage, BusinessA, Store2));
        Assert.False(AccessControl.Covers(storeManager, Permissions.UsersManage, BusinessB, Store1));
    }

    [Fact]
    public void Managers_can_grant_their_own_roles_but_not_owner_support_or_accountant()
    {
        var manager = new[] { Grant(Roles.Manager, BusinessA) };

        Assert.True(GrantPolicy.CanGrant(manager, Permissions.RolesAssign, Roles.Get(Roles.Cashier), BusinessA, Store1));
        Assert.True(GrantPolicy.CanGrant(manager, Permissions.RolesAssign, Roles.Get(Roles.Auditor), BusinessA, null));

        // Accountants prepare tax-registration changes, which managers cannot; only an owner appoints them.
        Assert.False(GrantPolicy.CanGrant(manager, Permissions.RolesAssign, Roles.Get(Roles.Accountant), BusinessA, null));
        Assert.False(GrantPolicy.CanGrant(manager, Permissions.RolesAssign, Roles.Get(Roles.Owner), BusinessA, null));
        Assert.False(GrantPolicy.CanGrant(manager, Permissions.RolesAssign, Roles.Get(Roles.SupportAdmin), BusinessA, null));
        Assert.False(GrantPolicy.CanGrant(manager, Permissions.RolesAssign, Roles.Get(Roles.Cashier), BusinessB, null));
    }

    [Fact]
    public void Managing_a_user_requires_authority_over_every_role_they_hold()
    {
        var manager = new[] { Grant(Roles.Manager, BusinessA) };

        Assert.True(GrantPolicy.CanManage(manager, [Grant(Roles.Cashier, BusinessA, Store1)], Permissions.UsersManage));
        Assert.False(GrantPolicy.CanManage(manager, [Grant(Roles.Owner, BusinessA)], Permissions.UsersManage));

        // A user who also works for another business cannot be disabled by a manager of only one of them.
        Assert.False(GrantPolicy.CanManage(manager, [Grant(Roles.Cashier, BusinessA, Store1), Grant(Roles.Cashier, BusinessB)], Permissions.UsersManage));
    }

    [Fact]
    public void Auditor_is_read_only()
    {
        var auditor = new[] { Grant(Roles.Auditor, BusinessA) };

        Assert.True(AccessControl.Covers(auditor, Permissions.AuditView, BusinessA, null));
        Assert.False(AccessControl.Covers(auditor, Permissions.UsersManage, BusinessA, null));
        Assert.False(AccessControl.Covers(auditor, Permissions.ApprovalsDecide, BusinessA, null));
        Assert.False(GrantPolicy.CanGrant(auditor, Permissions.RolesAssign, Roles.Get(Roles.Cashier), BusinessA, Store1));
    }
}
