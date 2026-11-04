using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Properties;

namespace TenantManager.Application.Properties;

/// <summary>Use cases for properties and their units. Landlord only (enforced at the API).</summary>
public sealed class PropertyService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    IValidator<PropertyRequest> propertyValidator,
    IValidator<UnitRequest> unitValidator)
{
    private static readonly Dictionary<string, Expression<Func<Property, object>>> PropertySorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = p => p.Name,
            ["city"] = p => p.City,
            ["createdAt"] = p => p.CreatedAt,
        };

    private static readonly Dictionary<string, Expression<Func<Unit, object>>> UnitSorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["label"] = u => u.Label,
            ["rentAmount"] = u => u.RentAmount,
            ["status"] = u => u.Status,
        };

    private static readonly Expression<Func<Unit, UnitDto>> ToUnitDto = u =>
        new UnitDto(u.Id, u.PropertyId, u.Property.Name, u.Label, u.RentAmount, u.DepositAmount, u.Status.ToString());

    private IQueryable<Property> OwnedProperties()
    {
        var ownerId = user.RequireOwnerId();
        return db.Properties.Where(p => p.OwnerId == ownerId);
    }

    public Task<PagedResult<PropertySummaryDto>> ListAsync(PropertyListQuery query, CancellationToken ct)
    {
        var q = OwnedProperties();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(p => p.Name.Contains(term) || p.City.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(query.Province))
            q = q.Where(p => p.Province == query.Province.Trim());

        return q.ApplySort(query.Sort, PropertySorts, "name").ToPagedAsync(query, p => new PropertySummaryDto(
            p.Id, p.Name, p.City, p.Province,
            p.Units.Count(u => !u.IsDeleted),
            p.Units.Count(u => !u.IsDeleted && u.Status == UnitStatus.Occupied),
            p.CreatedAt), ct);
    }

    public async Task<PropertyDto> GetAsync(Guid id, CancellationToken ct)
    {
        var property = await OwnedProperties().AsNoTracking().Include(p => p.Units)
                           .FirstOrDefaultAsync(p => p.Id == id, ct)
                       ?? throw new NotFoundException("Property", id);
        return ToDto(property);
    }

    public async Task<PropertyDto> CreateAsync(PropertyRequest request, CancellationToken ct)
    {
        await propertyValidator.ValidateAndThrowAsync(request, ct);
        var property = new Property(user.RequireOwnerId(), request.Name, ToAddress(request));
        db.Properties.Add(property);
        await db.SaveChangesAsync(ct);
        return ToDto(property);
    }

    public async Task<PropertyDto> UpdateAsync(Guid id, PropertyRequest request, CancellationToken ct)
    {
        await propertyValidator.ValidateAndThrowAsync(request, ct);
        var property = await LoadForWriteAsync(id, ct);
        property.Update(request.Name, ToAddress(request));
        await db.SaveChangesAsync(ct);
        return ToDto(property);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var property = await LoadForWriteAsync(id, ct);
        var inForce = LeaseTerm.InForce;
        if (await db.Leases.AnyAsync(l => l.Unit.PropertyId == id && inForce.Contains(l.Status), ct))
            throw new DomainRuleException("property.has_leases",
                "A property with leases in force (signed or waiting for signatures) cannot be deleted.", RuleKind.Conflict);
        property.Delete(user.RequireUserId(), clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<UnitDto>> ListUnitsAsync(Guid propertyId, UnitListQuery query, CancellationToken ct)
    {
        if (!await OwnedProperties().AnyAsync(p => p.Id == propertyId, ct))
            throw new NotFoundException("Property", propertyId);

        var q = db.Units.Where(u => u.PropertyId == propertyId);
        if (query.Status is { } status) q = q.Where(u => u.Status == status);
        return await q.ApplySort(query.Sort, UnitSorts, "label").ToPagedAsync(query, ToUnitDto, ct);
    }

    public async Task<UnitDto> GetUnitAsync(Guid unitId, CancellationToken ct)
    {
        var ownerId = user.RequireOwnerId();
        return await db.Units.Where(u => u.Id == unitId && u.Property.OwnerId == ownerId)
                   .Select(ToUnitDto).FirstOrDefaultAsync(ct)
               ?? throw new NotFoundException("Unit", unitId);
    }

    public async Task<UnitDto> AddUnitAsync(Guid propertyId, UnitRequest request, CancellationToken ct)
    {
        await unitValidator.ValidateAndThrowAsync(request, ct);
        var property = await LoadForWriteAsync(propertyId, ct);
        var unit = property.AddUnit(request.Label, request.RentAmount, request.DepositAmount);
        await db.SaveChangesAsync(ct);
        return MapUnit(unit);
    }

    public async Task<UnitDto> UpdateUnitAsync(Guid unitId, UnitRequest request, CancellationToken ct)
    {
        await unitValidator.ValidateAndThrowAsync(request, ct);
        var unit = await LoadUnitForWriteAsync(unitId, ct);
        unit.Update(request.Label, request.RentAmount, request.DepositAmount);
        await db.SaveChangesAsync(ct);
        return MapUnit(unit);
    }

    public async Task DeleteUnitAsync(Guid unitId, CancellationToken ct)
    {
        var unit = await LoadUnitForWriteAsync(unitId, ct);
        var inForce = LeaseTerm.InForce;
        if (await db.Leases.AnyAsync(l => l.UnitId == unitId && inForce.Contains(l.Status), ct))
            throw new DomainRuleException("unit.has_lease",
                "A unit with a lease in force (signed or waiting for signatures) cannot be deleted.", RuleKind.Conflict);
        unit.Delete(user.RequireUserId(), clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    // Loads the whole aggregate (property + live units) because label uniqueness and
    // delete rules are checked across all units.
    private async Task<Property> LoadForWriteAsync(Guid id, CancellationToken ct) =>
        await OwnedProperties().Include(p => p.Units).FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new NotFoundException("Property", id);

    private async Task<Unit> LoadUnitForWriteAsync(Guid unitId, CancellationToken ct)
    {
        var ownerId = user.RequireOwnerId();
        var propertyId = await db.Units.Where(u => u.Id == unitId && u.Property.OwnerId == ownerId)
            .Select(u => (Guid?)u.PropertyId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Unit", unitId);
        var property = await LoadForWriteAsync(propertyId, ct);
        return property.Units.First(u => u.Id == unitId);
    }

    private static UnitDto MapUnit(Unit u) =>
        new(u.Id, u.PropertyId, u.Property.Name, u.Label, u.RentAmount, u.DepositAmount, u.Status.ToString());

    private static PropertyAddress ToAddress(PropertyRequest r) =>
        new(r.AddressLine1, r.AddressLine2, r.City, r.Province, r.PostalCode);

    private static PropertyDto ToDto(Property p) => new(
        p.Id, p.Name, p.AddressLine1, p.AddressLine2, p.City, p.Province, p.PostalCode,
        p.Units.Where(u => !u.IsDeleted).OrderBy(u => u.Label)
            .Select(MapUnit)
            .ToList(),
        p.CreatedAt, p.UpdatedAt);
}
