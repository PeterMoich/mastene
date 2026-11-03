using Shouldly;
using TenantManager.Domain.Common;
using TenantManager.Domain.Tenants;

namespace TenantManager.Domain.Tests;

public class TenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    private static Tenant NewTenant() =>
        new(Guid.NewGuid(), "Sipho", "Dlamini", " Sipho.Dlamini@Example.com ", "082 555 0101");

    [Fact]
    public void Email_is_trimmed_and_lower_cased()
    {
        NewTenant().Email.ShouldBe("sipho.dlamini@example.com");
    }

    [Theory]
    [InlineData("082 555 0101", "+27825550101")]
    [InlineData("+27 82 555 0101", "+27825550101")]
    [InlineData("27825550101", "+27825550101")]
    [InlineData("(082) 555-0101", "+27825550101")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    public void Phone_numbers_are_stored_in_E164(string input, string expected)
    {
        Tenant.NormalisePhone(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("phone")]
    [InlineData("")]
    public void Invalid_phone_numbers_are_rejected(string input)
    {
        Should.Throw<DomainRuleException>(() => Tenant.NormalisePhone(input)).Code.ShouldBe("phone.invalid");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Sipho <sipho@example.com>")]
    [InlineData("sipho@localhost")]
    public void Invalid_emails_are_rejected(string input)
    {
        Should.Throw<DomainRuleException>(() => Tenant.NormaliseEmail(input)).Code.ShouldBe("email.invalid");
    }

    // BR19: a tenant cannot be deleted while they have an active lease or outstanding balance.
    [Fact]
    public void BR19_Tenant_with_active_lease_cannot_be_deleted()
    {
        var tenant = NewTenant();
        var ex = Should.Throw<DomainRuleException>(() =>
            tenant.Delete(new TenantObligations(HasActiveLease: true, OutstandingBalance: 0), Guid.NewGuid(), Now));

        ex.Code.ShouldBe("tenant.has_active_lease");
        ex.Kind.ShouldBe(RuleKind.Conflict);
        tenant.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public void BR19_Tenant_with_outstanding_balance_cannot_be_deleted()
    {
        var tenant = NewTenant();
        Should.Throw<DomainRuleException>(() =>
                tenant.Delete(new TenantObligations(HasActiveLease: false, OutstandingBalance: 0.01m), Guid.NewGuid(), Now))
            .Code.ShouldBe("tenant.has_outstanding_balance");
        tenant.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public void BR19_Tenant_without_obligations_is_soft_deleted()
    {
        var tenant = NewTenant();
        var by = Guid.NewGuid();

        tenant.Delete(TenantObligations.None, by, Now);

        tenant.IsDeleted.ShouldBeTrue();
        tenant.DeletedBy.ShouldBe(by);
        tenant.DeletedAt.ShouldBe(Now);
        tenant.FirstName.ShouldBe("Sipho"); // the record is kept, not wiped
    }
}
