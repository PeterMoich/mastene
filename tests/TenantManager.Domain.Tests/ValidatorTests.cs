using Shouldly;
using TenantManager.Application.Accounts;
using TenantManager.Application.Properties;
using TenantManager.Application.Tenants;

namespace TenantManager.Domain.Tests;

/// <summary>Request validators give field-level 400 errors before the domain is touched.</summary>
public class ValidatorTests
{
    [Fact]
    public void Property_request_reports_each_bad_field()
    {
        var result = new PropertyRequestValidator().Validate(
            new PropertyRequest("", "", null, "", "Atlantis", "12"));

        result.Errors.Select(e => e.PropertyName).Distinct().Order()
            .ShouldBe(["AddressLine1", "City", "Name", "PostalCode", "Province"]);
    }

    [Fact]
    public void Valid_property_request_passes()
    {
        new PropertyRequestValidator().Validate(
                new PropertyRequest("Jacaranda Court", "14 Jacaranda Street", null, "Pretoria", "Gauteng", "0083"))
            .IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6500.123, 0)]
    [InlineData(6500, -1)]
    public void Unit_request_rejects_bad_amounts(decimal rent, decimal deposit)
    {
        new UnitRequestValidator().Validate(new UnitRequest("Flat 1", rent, deposit)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Tenant_request_rejects_bad_phone()
    {
        var result = new TenantRequestValidator().Validate(
            new TenantRequest("Sipho", "Dlamini", "sipho@example.com", "123"));
        result.Errors.ShouldContain(e => e.PropertyName == "Phone");
    }

    [Theory]
    [InlineData("Tenant")]
    [InlineData("SuperUser")]
    public void Staff_users_can_only_be_Admin_or_Landlord(string role)
    {
        new CreateUserRequestValidator().Validate(
                new CreateUserRequest("staff@example.com", "Staff", role, "a-long-password-1"))
            .Errors.ShouldContain(e => e.PropertyName == "Role");
    }
}
