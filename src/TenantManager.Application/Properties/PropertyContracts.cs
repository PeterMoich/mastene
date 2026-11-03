using FluentValidation;
using TenantManager.Application.Common;
using TenantManager.Domain.Properties;

namespace TenantManager.Application.Properties;

public sealed record PropertyRequest(
    string Name, string AddressLine1, string? AddressLine2, string City, string Province, string PostalCode);

public sealed record PropertySummaryDto(
    Guid Id, string Name, string City, string Province, int UnitCount, int OccupiedCount, DateTimeOffset CreatedAt);

public sealed record PropertyDto(
    Guid Id, string Name, string AddressLine1, string? AddressLine2, string City, string Province, string PostalCode,
    IReadOnlyList<UnitDto> Units, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record UnitRequest(string Label, decimal RentAmount, decimal DepositAmount);

public sealed record UnitDto(
    Guid Id, Guid PropertyId, string PropertyName, string Label, decimal RentAmount, decimal DepositAmount, string Status);

public sealed record PropertyListQuery : PagedQuery
{
    /// <summary>Matches property name or city.</summary>
    public string? Search { get; init; }
    public string? Province { get; init; }
}

public sealed record UnitListQuery : PagedQuery
{
    /// <summary>Vacant or Occupied.</summary>
    public UnitStatus? Status { get; init; }
}

public sealed class PropertyRequestValidator : AbstractValidator<PropertyRequest>
{
    public PropertyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Property.NameMax);
        RuleFor(x => x.AddressLine1).NotEmpty().MaximumLength(Property.AddressLineMax);
        RuleFor(x => x.AddressLine2).MaximumLength(Property.AddressLineMax);
        RuleFor(x => x.City).NotEmpty().MaximumLength(Property.CityMax);
        RuleFor(x => x.Province).NotEmpty()
            .Must(p => SouthAfricanProvinces.All.Contains(p?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Province must be one of: {string.Join(", ", SouthAfricanProvinces.All)}.");
        RuleFor(x => x.PostalCode).NotEmpty().Matches(@"^\s*\d{4}\s*$").WithMessage("Postal code must be four digits.");
    }
}

public sealed class UnitRequestValidator : AbstractValidator<UnitRequest>
{
    public UnitRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(Unit.LabelMax);
        RuleFor(x => x.RentAmount).GreaterThan(0).PrecisionScale(18, 2, ignoreTrailingZeros: true)
            .LessThanOrEqualTo(1_000_000).WithMessage("Rent must be between R0.01 and R1,000,000.");
        RuleFor(x => x.DepositAmount).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, ignoreTrailingZeros: true)
            .LessThanOrEqualTo(10_000_000);
    }
}
