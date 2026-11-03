using Shouldly;
using TenantManager.Domain.Common;
using TenantManager.Domain.Properties;

namespace TenantManager.Domain.Tests;

public class PropertyTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    private static Property NewProperty() =>
        new(Owner, "Jacaranda Court", new PropertyAddress("14 Jacaranda Street", null, "Pretoria", "gauteng", "0083"));

    [Fact]
    public void Province_is_normalised_to_its_canonical_spelling()
    {
        NewProperty().Province.ShouldBe("Gauteng");
    }

    [Theory]
    [InlineData("Transvaal")]
    [InlineData("")]
    public void Unknown_province_is_rejected(string province)
    {
        var ex = Should.Throw<DomainRuleException>(() =>
            new Property(Owner, "X", new PropertyAddress("1 Street", null, "City", province, "0001")));
        ex.Code.ShouldBe("province.invalid");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12a4")]
    public void Postal_code_must_be_four_digits(string code)
    {
        Should.Throw<DomainRuleException>(() =>
                new Property(Owner, "X", new PropertyAddress("1 Street", null, "City", "Gauteng", code)))
            .Code.ShouldBe("postalCode.invalid");
    }

    [Fact]
    public void Unit_labels_are_unique_within_a_property_ignoring_case()
    {
        var property = NewProperty();
        property.AddUnit("Flat 1", 6500m, 6500m);

        var ex = Should.Throw<DomainRuleException>(() => property.AddUnit("flat 1", 7000m, 7000m));
        ex.Code.ShouldBe("unit.label_taken");
        ex.Kind.ShouldBe(RuleKind.Conflict);
    }

    [Fact]
    public void A_deleted_unit_frees_its_label()
    {
        var property = NewProperty();
        property.AddUnit("Flat 1", 6500m, 6500m).Delete(Guid.NewGuid(), Now);

        Should.NotThrow(() => property.AddUnit("Flat 1", 6500m, 6500m));
    }

    [Fact]
    public void Renaming_a_unit_to_another_units_label_is_rejected()
    {
        var property = NewProperty();
        property.AddUnit("Flat 1", 6500m, 6500m);
        var flat2 = property.AddUnit("Flat 2", 6500m, 6500m);

        Should.Throw<DomainRuleException>(() => flat2.Update("FLAT 1", 6500m, 6500m)).Code.ShouldBe("unit.label_taken");
        Should.NotThrow(() => flat2.Update("Flat 2", 6900m, 6900m)); // keeping its own label is fine
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(100.555, 0)]
    [InlineData(100, -5)]
    public void Rent_must_be_positive_and_deposit_non_negative_with_cents_only(decimal rent, decimal deposit)
    {
        Should.Throw<DomainRuleException>(() => NewProperty().AddUnit("Flat 1", rent, deposit));
    }

    [Fact]
    public void New_units_start_vacant()
    {
        NewProperty().AddUnit("Flat 1", 6500m, 0m).Status.ShouldBe(UnitStatus.Vacant);
    }

    [Fact]
    public void Property_with_an_occupied_unit_cannot_be_deleted()
    {
        var property = NewProperty();
        property.AddUnit("Flat 1", 6500m, 6500m).MarkOccupied();

        Should.Throw<DomainRuleException>(() => property.Delete(Guid.NewGuid(), Now))
            .Code.ShouldBe("property.has_occupied_units");
        property.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public void Deleting_a_property_soft_deletes_its_units()
    {
        var property = NewProperty();
        var unit = property.AddUnit("Flat 1", 6500m, 6500m);
        var by = Guid.NewGuid();

        property.Delete(by, Now);

        property.IsDeleted.ShouldBeTrue();
        property.DeletedBy.ShouldBe(by);
        property.DeletedAt.ShouldBe(Now);
        unit.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public void Occupied_unit_cannot_be_deleted()
    {
        var unit = NewProperty().AddUnit("Flat 1", 6500m, 6500m);
        unit.MarkOccupied();

        Should.Throw<DomainRuleException>(() => unit.Delete(Guid.NewGuid(), Now)).Code.ShouldBe("unit.occupied");
    }
}
