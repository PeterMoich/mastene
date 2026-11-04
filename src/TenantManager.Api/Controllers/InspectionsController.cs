using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Files;
using TenantManager.Application.Inspections;

namespace TenantManager.Api.Controllers;

/// <summary>
/// Move-in and move-out inspections with photos. The landlord fills them in and completes them; the
/// tenant sees completed ones and acknowledges them.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class InspectionsController(InspectionService inspections) : ControllerBase
{
    /// <summary>The lease's inspections (a tenant sees completed ones only) and which can be started.</summary>
    [HttpGet("leases/{leaseId:guid}/inspections")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<LeaseInspectionsDto> ForLease(Guid leaseId, CancellationToken ct) => inspections.ForLeaseAsync(leaseId, ct);

    /// <summary>Starts a move-in or move-out checklist. A move-out uses the move-in's areas.</summary>
    [HttpPost("leases/{leaseId:guid}/inspections")]
    [Authorize(Policy = Policies.Landlord)]
    [ProducesResponseType<InspectionDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<InspectionDto>> Start(Guid leaseId, StartInspectionRequest request, CancellationToken ct)
    {
        var created = await inspections.StartAsync(leaseId, request, ct);
        return Created($"/api/v1/inspections/{created.Id}", created);
    }

    [HttpGet("inspections/{id:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<InspectionDto> Get(Guid id, CancellationToken ct) => inspections.GetAsync(id, ct);

    [HttpPut("inspections/{id:guid}")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<InspectionDto> UpdateDetails(Guid id, InspectionDetailsRequest request, CancellationToken ct) =>
        inspections.UpdateDetailsAsync(id, request, ct);

    [HttpPost("inspections/{id:guid}/items")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<InspectionDto> AddItem(Guid id, InspectionAreaRequest request, CancellationToken ct) =>
        inspections.AddItemAsync(id, request, ct);

    /// <summary>Sets an area's condition and notes.</summary>
    [HttpPut("inspections/{id:guid}/items/{itemId:guid}")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<InspectionDto> UpdateItem(Guid id, Guid itemId, InspectionItemRequest request, CancellationToken ct) =>
        inspections.UpdateItemAsync(id, itemId, request, ct);

    [HttpDelete("inspections/{id:guid}/items/{itemId:guid}")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<InspectionDto> RemoveItem(Guid id, Guid itemId, CancellationToken ct) =>
        inspections.RemoveItemAsync(id, itemId, ct);

    /// <summary>Adds a photo to an area: JPG or PNG up to 5 MB, at most five per area.</summary>
    [HttpPost("inspections/{id:guid}/items/{itemId:guid}/photos")]
    [Authorize(Policy = Policies.Landlord)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    public async Task<ActionResult<InspectionDto>> AddPhoto(Guid id, Guid itemId, [FromForm] PhotoForm form,
        CancellationToken ct)
    {
        if (form.File is null) return IssuesController.NoFile(this);
        await using var stream = form.File.OpenReadStream();
        return await inspections.AddPhotoAsync(id, itemId, new UploadedFile(form.File.FileName, form.File.Length, stream), ct);
    }

    [HttpGet("inspections/{id:guid}/photos/{photoId:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public async Task<IActionResult> Photo(Guid id, Guid photoId, CancellationToken ct)
    {
        var (content, contentType) = await inspections.OpenPhotoAsync(id, photoId, ct);
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(content, contentType);
    }

    /// <summary>Every area needs a condition. A completed inspection can't change.</summary>
    [HttpPost("inspections/{id:guid}/complete")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<InspectionDto> Complete(Guid id, CancellationToken ct) => inspections.CompleteAsync(id, ct);

    /// <summary>The tenant acknowledges the completed inspection, with an optional comment.</summary>
    [HttpPost("inspections/{id:guid}/acknowledge")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<InspectionDto> Acknowledge(Guid id, AcknowledgeInspectionRequest request, CancellationToken ct) =>
        inspections.AcknowledgeAsync(id, request, ct);
}
