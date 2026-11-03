using TenantManager.Domain.Common;

namespace TenantManager.Domain.Properties;

public enum UnitStatus
{
    Vacant,
    Occupied,
}

/// <summary>A rentable unit (flat, room, cottage) inside a property.</summary>
public sealed class Unit : Entity, ISoftDeletable, IAuditable
{
    public const int LabelMax = 40;

    private Unit() { } // EF Core

    internal Unit(Property property, string label, decimal rentAmount, decimal depositAmount)
    {
        Property = property;
        Label = Guard.NotBlank(label, "label", LabelMax);
        RentAmount = Guard.Money(rentAmount, "rentAmount", allowZero: false);
        DepositAmount = Guard.Money(depositAmount, "depositAmount", allowZero: true);
        Status = UnitStatus.Vacant;
    }

    public Guid PropertyId { get; private set; }
    public Property Property { get; private set; } = null!;
    public string Label { get; private set; } = string.Empty;

    /// <summary>Monthly rent in ZAR.</summary>
    public decimal RentAmount { get; private set; }

    /// <summary>Deposit asked for this unit in ZAR. The deposit actually held is tracked per lease (Phase 3).</summary>
    public decimal DepositAmount { get; private set; }

    /// <summary>Changed only by the lease workflow (Phase 2), never edited directly.</summary>
    public UnitStatus Status { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public void Update(string label, decimal rentAmount, decimal depositAmount)
    {
        var newLabel = Guard.NotBlank(label, "label", LabelMax);
        Property.EnsureLabelIsFree(newLabel, except: this);
        Label = newLabel;
        RentAmount = Guard.Money(rentAmount, "rentAmount", allowZero: false);
        DepositAmount = Guard.Money(depositAmount, "depositAmount", allowZero: true);
    }

    public void Delete(Guid deletedBy, DateTimeOffset at)
    {
        if (IsDeleted) return;
        if (Status == UnitStatus.Occupied)
            throw new DomainRuleException("unit.occupied", "An occupied unit cannot be deleted.", RuleKind.Conflict);
        IsDeleted = true;
        DeletedAt = at;
        DeletedBy = deletedBy;
    }

    // Used by the lease workflow in Phase 2. Internal so nothing outside the domain can flip it.
    internal void MarkOccupied() => Status = UnitStatus.Occupied;
    internal void MarkVacant() => Status = UnitStatus.Vacant;
}
