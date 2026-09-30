using SupermarketBilling.Domain.Approvals;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Identity;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.UnitTests.Identity;

public sealed class IdentityDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Owner_has_every_permission_and_only_privileged_roles_can_manage_users()
    {
        Assert.True(Permissions.All.IsSubsetOf(Roles.Get(Roles.Owner).Permissions));
        foreach (var role in Roles.All.Where(r => !r.IsPrivileged))
        {
            Assert.DoesNotContain(Permissions.UsersManage, role.Permissions);
            Assert.DoesNotContain(Permissions.RolesAssign, role.Permissions);
            Assert.DoesNotContain(Permissions.ApprovalsDecide, role.Permissions);
        }
    }

    [Fact]
    public void All_ten_roles_from_the_specification_exist()
    {
        Assert.Equal(
            ["accountant", "auditor", "cashier", "collection_manager", "collection_person", "inventory_operator", "manager", "owner", "purchase_operator", "support_admin"],
            Roles.Codes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Business_wide_roles_cannot_be_limited_to_a_store()
    {
        var error = Assert.Throws<DomainException>(() =>
            RoleAssignment.Grant(Guid.NewGuid(), Roles.Owner, Guid.NewGuid(), Guid.NewGuid(), null, null, Now));
        Assert.Equal("role.business_wide_only", error.Code);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("-leadinghyphen")]
    public void Invalid_usernames_are_rejected(string username)
    {
        Assert.Throws<DomainException>(() => User.NormalizeUsername(username));
    }

    [Fact]
    public void Usernames_are_normalised_to_lower_case()
    {
        Assert.Equal("ravi.k", User.NormalizeUsername("  Ravi.K "));
    }

    [Theory]
    [InlineData("short", "ravi")]
    [InlineData("ravi-password-1", "ravi")]
    [InlineData("aaaaaaaaaaaa", "ravi")]
    public void Weak_passwords_fail_the_policy(string password, string username)
    {
        Assert.NotNull(User.CheckPasswordPolicy(password, username));
    }

    [Fact]
    public void Lockout_starts_at_the_limit_and_ends_after_the_period()
    {
        var user = User.Create("cashier1", "Cashier", "hash", Now, mustChangePassword: false);

        for (var i = 0; i < 4; i++)
        {
            Assert.False(user.RecordFailedLogin(Now, maxAttempts: 5, TimeSpan.FromMinutes(15)));
        }

        Assert.True(user.RecordFailedLogin(Now, 5, TimeSpan.FromMinutes(15)));
        Assert.True(user.IsLockedOut(Now.AddMinutes(14)));
        Assert.False(user.IsLockedOut(Now.AddMinutes(15)));
    }

    [Fact]
    public void Nobody_can_decide_their_own_approval_request()
    {
        var requester = Guid.NewGuid();
        var request = ApprovalRequest.Create(Guid.NewGuid(), "role.grant", "Grant Manager", "{}", null, requester, Now, TimeSpan.FromDays(7));

        Assert.Equal("approval.self_decision", Assert.Throws<DomainException>(() => request.Approve(requester, Now, null)).Code);
        Assert.Equal("approval.self_decision", Assert.Throws<DomainException>(() => request.Reject(requester, Now, "no")).Code);

        request.Approve(Guid.NewGuid(), Now, null);
        Assert.Equal(ApprovalStatus.Approved, request.Status);
        Assert.Equal("approval.not_pending", Assert.Throws<DomainException>(() => request.Approve(Guid.NewGuid(), Now, null)).Code);
    }

    [Fact]
    public void Expired_approval_requests_cannot_be_approved()
    {
        var request = ApprovalRequest.Create(Guid.NewGuid(), "role.grant", "Grant", "{}", null, Guid.NewGuid(), Now, TimeSpan.FromDays(7));

        Assert.Equal("approval.expired", Assert.Throws<DomainException>(() => request.Approve(Guid.NewGuid(), Now.AddDays(7), null)).Code);
    }

    [Fact]
    public void Sessions_expire_on_idle_absolute_lifetime_or_revocation()
    {
        var session = Session.Start(Guid.NewGuid(), new byte[32], new byte[32], Now, TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), null, null);

        Assert.True(session.IsValid(Now.AddMinutes(29)));
        Assert.False(session.IsValid(Now.AddMinutes(30)));
        session.Revoke(Now, "logout");
        Assert.False(session.IsValid(Now));
    }

    [Theory]
    [InlineData("27AAPFU0939F1ZV")]
    [InlineData("29AAGCB7383J1Z4")]
    public void Real_gstins_validate(string gstin)
    {
        Assert.True(Gstin.IsValid(gstin));
    }

    [Theory]
    [InlineData("27AAPFU0939F1ZW")] // wrong check character
    [InlineData("27AAPFU0939F1Z")] // too short
    [InlineData("27aapfu0939f1zv")] // lower case (normalised before validation)
    [InlineData("ZZAAPFU0939F1ZV")] // state code not numeric
    public void Malformed_gstins_are_rejected(string gstin)
    {
        Assert.False(Gstin.IsValid(gstin));
    }

    [Fact]
    public void Gstin_state_must_match_the_business_state()
    {
        Assert.Equal("gstin.state_mismatch", Assert.Throws<DomainException>(() => Gstin.Validate("27AAPFU0939F1ZV", "33")).Code);
        Assert.Equal("27AAPFU0939F1ZV", Gstin.Validate(" 27aapfu0939f1zv ", "27"));
    }

    [Fact]
    public void Business_and_store_codes_are_normalised_and_validated()
    {
        var business = Business.Create(" smkt ", "Legal", null, "33", null, null, Now);
        Assert.Equal("SMKT", business.Code);
        Assert.Equal("Legal", business.TradeName);
        Assert.Throws<DomainException>(() => Store.Create(business.Id, "has space", "Store", "33", null, null, Now));
        Assert.Throws<DomainException>(() => Business.Create("SMKT", "Legal", null, "00", null, null, Now));
    }
}
