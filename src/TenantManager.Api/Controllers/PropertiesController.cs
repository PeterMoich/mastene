using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Common;
using TenantManager.Application.Properties;

namespace TenantManager.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/properties")]
[Authorize(Policy = Policies.Landlord)]
public sealed class PropertiesController(PropertyService properties) : ControllerBase
{
    /// <summary>Lists properties. Sort by name, city or createdAt (prefix '-' for descending).</summary>
    [HttpGet]
    public Task<PagedResult<PropertySummaryDto>> List([FromQuery] PropertyListQuery query, CancellationToken ct) =>
        properties.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<PropertyDto> Get(Guid id, CancellationToken ct) => properties.GetAsync(id, ct);

    [HttpPost]
    [ProducesResponseType<PropertyDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PropertyDto>> Create(PropertyRequest request, CancellationToken ct)
    {
        var created = await properties.CreateAsync(request, ct);
        return Created($"/api/v1/properties/{created.Id}", created);
    }

    [HttpPut("{id:guid}")]
    public Task<PropertyDto> Update(Guid id, PropertyRequest request, CancellationToken ct) =>
        properties.UpdateAsync(id, request, ct);

    /// <summary>Soft-deletes the property and its units. Returns 409 if any unit is occupied.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await properties.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Units of one property. Filter by status (Vacant, Occupied); sort by label, rentAmount or status.</summary>
    [HttpGet("{id:guid}/units")]
    public Task<PagedResult<UnitDto>> ListUnits(Guid id, [FromQuery] UnitListQuery query, CancellationToken ct) =>
        properties.ListUnitsAsync(id, query, ct);

    [HttpPost("{id:guid}/units")]
    [ProducesResponseType<UnitDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UnitDto>> AddUnit(Guid id, UnitRequest request, CancellationToken ct)
    {
        var unit = await properties.AddUnitAsync(id, request, ct);
        return Created($"/api/v1/units/{unit.Id}", unit);
    }
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/units")]
[Authorize(Policy = Policies.Landlord)]
public sealed class UnitsController(PropertyService properties) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public Task<UnitDto> Get(Guid id, CancellationToken ct) => properties.GetUnitAsync(id, ct);

    [HttpPut("{id:guid}")]
    public Task<UnitDto> Update(Guid id, UnitRequest request, CancellationToken ct) =>
        properties.UpdateUnitAsync(id, request, ct);

    /// <summary>Soft-deletes a unit. Returns 409 if it is occupied.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await properties.DeleteUnitAsync(id, ct);
        return NoContent();
    }
}
