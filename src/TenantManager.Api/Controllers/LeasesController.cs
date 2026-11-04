using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Common;
using TenantManager.Application.Leases;
using TenantManager.Application.Settings;

namespace TenantManager.Api.Controllers;

/// <summary>
/// Leases. Landlords manage them; a tenant can list, read, download and sign only their own.
/// Policies are set per action (a class-level policy would combine with them).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/leases")]
public sealed class LeasesController(LeaseService leases) : ControllerBase
{
    /// <summary>Lists leases. Filter by status, propertyId, tenantId and a from/to date range; sort by startDate, endDate, leaseNumber, status, monthlyRent or tenantName.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<PagedResult<LeaseSummaryDto>> List([FromQuery] LeaseListQuery query, CancellationToken ct) =>
        leases.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<LeaseDto> Get(Guid id, CancellationToken ct) => leases.GetAsync(id, ct);

    /// <summary>Every version of this lease (the original and its renewals), oldest first.</summary>
    [HttpGet("{id:guid}/versions")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<IReadOnlyList<LeaseSummaryDto>> Versions(Guid id, CancellationToken ct) =>
        leases.VersionsAsync(id, ct);

    /// <summary>Creates a draft. The end date is always 12 months from the start (BR02).</summary>
    [HttpPost]
    [Authorize(Policy = Policies.Landlord)]
    [ProducesResponseType<LeaseDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<LeaseDto>> Create(LeaseRequest request, CancellationToken ct)
    {
        var created = await leases.CreateAsync(request, ct);
        return Created($"/api/v1/leases/{created.Id}", created);
    }

    /// <summary>Changes a draft. Returns 409 once the lease has been sent for signing (BR04).</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<LeaseDto> Update(Guid id, LeaseRequest request, CancellationToken ct) =>
        leases.UpdateAsync(id, request, ct);

    /// <summary>Deletes a draft. Leases already sent for signing can only be cancelled.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Landlord)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Discard(Guid id, CancellationToken ct)
    {
        await leases.DiscardAsync(id, ct);
        return NoContent();
    }

    /// <summary>Renders the PDF and sends the draft for signing. 409 if the unit or tenant clash with another lease (BR03).</summary>
    [HttpPost("{id:guid}/generate")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<LeaseDto> Generate(Guid id, CancellationToken ct) => leases.GenerateAsync(id, ct);

    /// <summary>Signs as the current user (landlord or tenant). The second signature starts the lease if its start date has come.</summary>
    [HttpPost("{id:guid}/sign")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<LeaseDto> Sign(Guid id, CancellationToken ct) => leases.SignAsync(id, ct);

    /// <summary>Creates the renewal as a new draft version with escalated rent (BR06).</summary>
    [HttpPost("{id:guid}/renewals")]
    [Authorize(Policy = Policies.Landlord)]
    [ProducesResponseType<LeaseDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<LeaseDto>> Renew(Guid id, RenewLeaseRequest request, CancellationToken ct)
    {
        var renewal = await leases.RenewAsync(id, request, ct);
        return Created($"/api/v1/leases/{renewal.Id}", renewal);
    }

    /// <summary>Cancels a lease waiting for signatures, or schedules the last day of an active lease.</summary>
    [HttpPost("{id:guid}/terminate")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<LeaseDto> Terminate(Guid id, TerminateLeaseRequest request, CancellationToken ct) =>
        leases.TerminateAsync(id, request, ct);

    [HttpGet("{id:guid}/documents/{documentId:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    [Produces("application/pdf")]
    public async Task<IActionResult> Document(Guid id, Guid documentId, CancellationToken ct)
    {
        var (content, fileName) = await leases.OpenDocumentAsync(id, documentId, ct);
        return File(content, "application/pdf", fileName);
    }
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/lease-checks")]
[Authorize(Policy = Policies.Landlord)]
public sealed class LeaseChecksController(LeaseLifecycleService lifecycle) : ControllerBase
{
    /// <summary>Runs the daily lease and rent checks now (they also run every night at 00:15). Safe to repeat.</summary>
    [HttpPost("run")]
    public Task<DailyChecksSummary> Run(CancellationToken ct) => lifecycle.RunAsync(ct);
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/settings")]
public sealed class SettingsController(SettingsService settings) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.AdminOrLandlord)]
    public Task<SettingsDto> Get(CancellationToken ct) => settings.GetAsync(ct);

    /// <summary>Admin only. Fields listed in legalReviewNeeded have placeholder defaults (ADR-0011).</summary>
    [HttpPut]
    [Authorize(Policy = Policies.Admin)]
    public Task<SettingsDto> Update(SettingsRequest request, CancellationToken ct) => settings.UpdateAsync(request, ct);
}
