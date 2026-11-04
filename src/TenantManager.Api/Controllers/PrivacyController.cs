using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Privacy;

namespace TenantManager.Api.Controllers;

/// <summary>POPIA requests (ADR-0020): a tenant's data export, and erasure of a former tenant's details by an Admin.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class PrivacyController(PrivacyService privacy) : ControllerBase
{
    /// <summary>Admin: tenants, including deleted ones, with when their details may be erased.</summary>
    [HttpGet("privacy/tenants")]
    [Authorize(Policy = Policies.Admin)]
    public Task<IReadOnlyList<PrivacyTenantDto>> Tenants([FromQuery] string? search, CancellationToken ct) =>
        privacy.ListAsync(search, ct);

    /// <summary>Everything held about the tenant, as a zip (tenant.json plus their documents). The tenant (own) or an Admin.</summary>
    [HttpGet("tenants/{tenantId:guid}/export")]
    [Authorize(Policy = Policies.AdminOrTenant)]
    [Produces("application/zip")]
    public async Task<IActionResult> Export(Guid tenantId, CancellationToken ct)
    {
        var (zip, fileName) = await privacy.ExportAsync(tenantId, ct);
        Response.Headers.CacheControl = "no-store";
        return File(zip, "application/zip", fileName);
    }

    /// <summary>Admin: erases a former tenant's personal details once no lease is in force, nothing is owed and the retention period is over.</summary>
    [HttpPost("tenants/{tenantId:guid}/anonymise")]
    [Authorize(Policy = Policies.Admin)]
    public Task<AnonymiseResult> Anonymise(Guid tenantId, CancellationToken ct) => privacy.AnonymiseAsync(tenantId, ct);
}
