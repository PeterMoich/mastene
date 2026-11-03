using TenantManager.Domain.Common;

namespace TenantManager.Domain.Properties;

/// <summary>A building or site owned by the landlord. Aggregate root for its units.</summary>
public sealed class Property : Entity, IOwned, ISoftDeletable, IAuditable
{
    public const int NameMax = 120;
    public const int AddressLineMax = 160;
    public const int CityMax = 80;

    private readonly List<Unit> _units = [];

    private Property() { } // EF Core

    public Property(Guid ownerId, string name, PropertyAddress address)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        OwnerId = ownerId;
        Update(name, address);
    }

    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    // Stored as plain columns (not an owned type) so address edits show up in the
    // property's own audit row instead of a separate, unlabelled one.
    public string AddressLine1 { get; private set; } = string.Empty;
    public string? AddressLine2 { get; private set; }
    public string City { get; private set; } = string.Empty;
    public string Province { get; private set; } = string.Empty;
    public string PostalCode { get; private set; } = string.Empty;

    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public IReadOnlyCollection<Unit> Units => _units.AsReadOnly();

    public PropertyAddress Address => new(AddressLine1, AddressLine2, City, Province, PostalCode);

    public void Update(string name, PropertyAddress address)
    {
        Name = Guard.NotBlank(name, "name", NameMax);
        AddressLine1 = Guard.NotBlank(address.Line1, "addressLine1", AddressLineMax);
        AddressLine2 = Guard.Optional(address.Line2, "addressLine2", AddressLineMax);
        City = Guard.NotBlank(address.City, "city", CityMax);
        Province = SouthAfricanProvinces.Normalise(address.Province);
        PostalCode = PropertyAddress.NormalisePostalCode(address.PostalCode);
    }

    /// <summary>Adds a unit. Labels are unique within a property (case-insensitive), ignoring deleted units.</summary>
    public Unit AddUnit(string label, decimal rentAmount, decimal depositAmount)
    {
        var unit = new Unit(this, label, rentAmount, depositAmount);
        EnsureLabelIsFree(unit.Label, except: null);
        _units.Add(unit);
        return unit;
    }

    internal void EnsureLabelIsFree(string label, Unit? except)
    {
        var taken = _units.Any(u => !u.IsDeleted && u != except &&
                                    string.Equals(u.Label, label, StringComparison.OrdinalIgnoreCase));
        if (taken)
            throw new DomainRuleException("unit.label_taken",
                $"This property already has a unit labelled '{label}'.", RuleKind.Conflict);
    }

    /// <summary>Soft-deletes the property and its units. Not allowed while any unit is occupied.</summary>
    public void Delete(Guid deletedBy, DateTimeOffset at)
    {
        if (IsDeleted) return;
        if (_units.Any(u => !u.IsDeleted && u.Status == UnitStatus.Occupied))
            throw new DomainRuleException("property.has_occupied_units",
                "A property with occupied units cannot be deleted.", RuleKind.Conflict);

        foreach (var unit in _units.Where(u => !u.IsDeleted))
            unit.Delete(deletedBy, at);

        IsDeleted = true;
        DeletedAt = at;
        DeletedBy = deletedBy;
    }
}

public sealed record PropertyAddress(string Line1, string? Line2, string City, string Province, string PostalCode)
{
    /// <summary>South African postal codes are four digits.</summary>
    public static string NormalisePostalCode(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length != 4 || !trimmed.All(char.IsAsciiDigit))
            throw new DomainRuleException("postalCode.invalid", "Postal code must be four digits.");
        return trimmed;
    }
}

public static class SouthAfricanProvinces
{
    public static readonly IReadOnlyList<string> All =
    [
        "Eastern Cape", "Free State", "Gauteng", "KwaZulu-Natal", "Limpopo",
        "Mpumalanga", "Northern Cape", "North West", "Western Cape",
    ];

    /// <summary>Returns the canonical spelling, accepting any casing.</summary>
    public static string Normalise(string? value)
    {
        var match = All.FirstOrDefault(p => string.Equals(p, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new DomainRuleException("province.invalid",
            $"Province must be one of: {string.Join(", ", All)}.");
    }
}
