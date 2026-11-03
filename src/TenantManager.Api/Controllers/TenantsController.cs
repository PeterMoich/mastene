using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Accounts;
using TenantManager.Application.Auditing;
using TenantManager.Application.Common;
using TenantManager.Application.Tenants;

namespace TenantManager.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tenants")]
// Policies are per action: stacking a class-level policy would AND it with the action's,
// which would lock tenants out of reading their own record.
public sealed class TenantsController(TenantService tenants) : ControllerBase
{
    /// <summary>Lists tenants. Search matches name, email or phone; sort by lastName, firstName, email or createdAt.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.Landlord)]
    public Task<PagedResult<TenantDto>> List([FromQuery] TenantListQuery query, CancellationToken ct) =>
        tenants.ListAsync(query, ct);

    /// <summary>A tenant may read their own record; anyone else's returns 404.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<TenantDto> Get(Guid id, CancellationToken ct) => tenants.GetAsync(id, ct);

    [HttpPost]
    [Authorize(Policy = Policies.Landlord)]
    [ProducesResponseType<TenantDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TenantDto>> Create(TenantRequest request, CancellationToken ct)
    {
        var created = await tenants.CreateAsync(request, ct);
        return Created($"/api/v1/tenants/{created.Id}", created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<TenantDto> Update(Guid id, TenantRequest request, CancellationToken ct) =>
        tenants.UpdateAsync(id, request, ct);

    /// <summary>BR19: soft delete; 409 while the tenant has an active lease or an outstanding balance.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Landlord)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await tenants.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Creates the tenant's login and returns a one-time temporary password to pass on.</summary>
    [HttpPost("{id:guid}/login")]
    [Authorize(Policy = Policies.Landlord)]
    [ProducesResponseType<TenantLoginCreated>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TenantLoginCreated>> CreateLogin(Guid id, CancellationToken ct)
    {
        var created = await tenants.CreateLoginAsync(id, ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit")]
[Authorize(Policy = Policies.AdminOrLandlord)]
public sealed class AuditController(AuditService audit) : ControllerBase
{
    /// <summary>The audit trail, newest first. Filter by entityType (e.g. Tenant), entityId and a date range.</summary>
    [HttpGet]
    public Task<PagedResult<AuditLogDto>> List([FromQuery] AuditListQuery query, CancellationToken ct) =>
        audit.ListAsync(query, ct);
}
