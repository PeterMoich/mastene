using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Common;

namespace TenantManager.Api.Controllers;

public sealed record JobsDashboardLink(string Url, DateTimeOffset ExpiresAt);

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin/jobs-dashboard")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminJobsController(IConfiguration configuration, ICurrentUser user, IClock clock) : ControllerBase
{
    /// <summary>A link to the background jobs dashboard, valid for two minutes.</summary>
    [HttpPost]
    public JobsDashboardLink Create()
    {
        if (!configuration.GetValue<bool>("Jobs:Enabled"))
            throw new DomainRuleException("jobs.disabled", "Background jobs are switched off in this environment.",
                RuleKind.Conflict);
        var ticket = JobsDashboard.CreateTicket(HttpContext.RequestServices, user.RequireUserId());
        return new JobsDashboardLink($"{JobsDashboard.Path}?ticket={Uri.EscapeDataString(ticket)}", clock.UtcNow.AddMinutes(2));
    }
}
