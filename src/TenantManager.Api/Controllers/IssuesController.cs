using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Common;
using TenantManager.Application.Files;
using TenantManager.Application.Issues;

namespace TenantManager.Api.Controllers;

/// <summary>Multipart form for one photo.</summary>
public sealed class PhotoForm
{
    /// <summary>JPG or PNG, up to 5 MB.</summary>
    public IFormFile? File { get; set; }
}

/// <summary>
/// Maintenance issues (BR14, BR15). Tenants log and follow their own; the landlord works through all of
/// theirs. Which steps the current user may take is returned in each issue's allowedActions.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/issues")]
public sealed class IssuesController(IssueService issues) : ControllerBase
{
    /// <summary>Lists issues, newest first. Filter by status, priority, propertyId, leaseId, contractorId, escalated and openOnly; sort by openedAt, priority, status, resolutionDueAt or title.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<PagedResult<IssueSummaryDto>> List([FromQuery] IssueListQuery query, CancellationToken ct) =>
        issues.ListAsync(query, ct);

    /// <summary>The issue with its photos, comments and history.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<IssueDto> Get(Guid id, CancellationToken ct) => issues.GetAsync(id, ct);

    /// <summary>Logs an issue. A tenant gives their lease; the landlord gives a unit (or a lease).</summary>
    [HttpPost]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    [ProducesResponseType<IssueDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<IssueDto>> Create(CreateIssueRequest request, CancellationToken ct)
    {
        var created = await issues.CreateAsync(request, ct);
        return Created($"/api/v1/issues/{created.Id}", created);
    }

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<IssueDto> Acknowledge(Guid id, CancellationToken ct) => issues.AcknowledgeAsync(id, ct);

    /// <summary>Work has started. Also acknowledges an issue that wasn't acknowledged yet.</summary>
    [HttpPost("{id:guid}/start")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<IssueDto> Start(Guid id, CancellationToken ct) => issues.StartAsync(id, ct);

    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<IssueDto> Resolve(Guid id, ResolveIssueRequest request, CancellationToken ct) =>
        issues.ResolveAsync(id, request, ct);

    /// <summary>Confirms the fix: the tenant for their issue, the landlord for an issue on a vacant unit.</summary>
    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<IssueDto> Close(Guid id, CancellationToken ct) => issues.CloseAsync(id, ct);

    /// <summary>Not fixed: back to In progress, with a reason.</summary>
    [HttpPost("{id:guid}/reopen")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<IssueDto> Reopen(Guid id, ReopenIssueRequest request, CancellationToken ct) =>
        issues.ReopenAsync(id, request, ct);

    /// <summary>Assigns a contractor, or removes them with a null contractorId.</summary>
    [HttpPut("{id:guid}/contractor")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<IssueDto> AssignContractor(Guid id, AssignContractorRequest request, CancellationToken ct) =>
        issues.AssignContractorAsync(id, request, ct);

    /// <summary>Changes the priority; the targets are recalculated from when the issue was opened.</summary>
    [HttpPut("{id:guid}/priority")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<IssueDto> ChangePriority(Guid id, ChangePriorityRequest request, CancellationToken ct) =>
        issues.ChangePriorityAsync(id, request, ct);

    [HttpPost("{id:guid}/comments")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<IssueDto> Comment(Guid id, IssueCommentRequest request, CancellationToken ct) =>
        issues.AddCommentAsync(id, request, ct);

    /// <summary>Adds a photo: JPG or PNG up to 5 MB, at most five per issue. The type is checked from the file's contents.</summary>
    [HttpPost("{id:guid}/photos")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    public async Task<ActionResult<IssueDto>> AddPhoto(Guid id, [FromForm] PhotoForm form, CancellationToken ct)
    {
        if (form.File is null) return NoFile(this);
        await using var stream = form.File.OpenReadStream();
        return await issues.AddPhotoAsync(id, new UploadedFile(form.File.FileName, form.File.Length, stream), ct);
    }

    [HttpGet("{id:guid}/photos/{photoId:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public async Task<IActionResult> Photo(Guid id, Guid photoId, CancellationToken ct)
    {
        var (content, contentType) = await issues.OpenPhotoAsync(id, photoId, ct);
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(content, contentType);
    }

    /// <summary>Response and resolution times against the targets, per priority, for issues opened in the last N days (default 90).</summary>
    [HttpGet("sla-report")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<SlaReportDto> SlaReport([FromQuery] int days = 90, CancellationToken ct = default) =>
        issues.SlaReportAsync(days, ct);

    internal static ActionResult NoFile(ControllerBase controller) =>
        controller.ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            ["file"] = ["Choose the photo to upload."],
        }));
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/contractors")]
[Authorize(Policy = Policies.Landlord)]
public sealed class ContractorsController(ContractorService contractors) : ControllerBase
{
    /// <summary>Lists contractors by name. Search matches name, trade or phone.</summary>
    [HttpGet]
    public Task<PagedResult<ContractorDto>> List([FromQuery] ContractorListQuery query, CancellationToken ct) =>
        contractors.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<ContractorDto> Get(Guid id, CancellationToken ct) => contractors.GetAsync(id, ct);

    [HttpPost]
    [ProducesResponseType<ContractorDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ContractorDto>> Create(ContractorRequest request, CancellationToken ct)
    {
        var created = await contractors.CreateAsync(request, ct);
        return Created($"/api/v1/contractors/{created.Id}", created);
    }

    [HttpPut("{id:guid}")]
    public Task<ContractorDto> Update(Guid id, ContractorRequest request, CancellationToken ct) =>
        contractors.UpdateAsync(id, request, ct);

    /// <summary>Soft delete. Refused while they are assigned to an issue that isn't resolved.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await contractors.DeleteAsync(id, ct);
        return NoContent();
    }
}
