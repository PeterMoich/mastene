using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TenantManager.Application.Auditing;
using TenantManager.Application.Common;
using TenantManager.Application.Issues;
using TenantManager.Application.Leases;
using TenantManager.Domain.Issues;
using TenantManager.Infrastructure.Persistence;

namespace TenantManager.Api.IntegrationTests;

public static class IssueHelpers
{
    /// <summary>A unique file that starts like a PNG (enough for the server's type check).</summary>
    public static byte[] UniquePng(int size = 0)
    {
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var bytes = new byte[Math.Max(size, 64)];
        magic.CopyTo(bytes, 0);
        Guid.NewGuid().ToByteArray().CopyTo(bytes, 16);
        return bytes;
    }

    public static async Task<HttpResponseMessage> UploadPhotoAsync(this HttpClient client, string url, byte[] file,
        string fileName = "photo.png", string contentType = "image/png")
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "File", fileName);
        return await client.PostAsync(url, form);
    }

    public static async Task<IssueDto> LogIssueAsync(this HttpClient tenant, Guid leaseId,
        IssuePriority priority = IssuePriority.High, string title = "Geyser not heating")
    {
        var response = await tenant.PostJsonAsync("/api/v1/issues",
            new CreateIssueRequest(leaseId, null, IssueCategory.Plumbing, priority, title, "No hot water since last night."));
        await response.ShouldBeAsync(HttpStatusCode.Created);
        return await response.ReadAsync<IssueDto>();
    }
}

[Collection(ApiCollection.Name)]
public class IssueApiTests(ApiFactory api)
{
    private async Task<(LeaseScenario S, LeaseDto Lease, HttpClient Tenant)> RunningLeaseAsync()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        var lease = await s.ActiveAsync(LeaseScenario.Today.AddMonths(-1));
        return (s, lease, await s.TenantClientAsync());
    }

    /// <summary>Moves an issue's clock back, as if it had been opened <paramref name="hours"/> hours earlier.</summary>
    private async Task BackdateAsync(Guid issueId, int hours)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE Issues SET OpenedAt = DATEADD(hour, -{hours}, OpenedAt),
                ResponseDueAt = DATEADD(hour, -{hours}, ResponseDueAt),
                ResolutionDueAt = DATEADD(hour, -{hours}, ResolutionDueAt),
                ResolvedAt = CASE WHEN ResolvedAt IS NULL THEN NULL ELSE DATEADD(hour, -{hours}, ResolvedAt) END
            WHERE Id = {issueId}
            """);
    }

    private static async Task<IssueDto> PostAsync(HttpClient client, string url, object? body = null)
    {
        var response = body is null ? await client.PostAsync(url, null) : await client.PostJsonAsync(url, body);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return await response.ReadAsync<IssueDto>();
    }

    [Fact]
    public async Task BR14_Tenant_logs_an_issue_and_the_landlord_takes_it_through_to_closed()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();

        var issue = await tenant.LogIssueAsync(lease.Id, IssuePriority.High);
        issue.Status.ShouldBe(IssueStatus.Open);
        issue.TenantId.ShouldBe(s.TenantId);
        issue.LeaseNumber.ShouldBe(lease.LeaseNumber);
        (issue.ResponseDueAt - issue.OpenedAt).ShouldBe(TimeSpan.FromHours(24));
        (issue.ResolutionDueAt - issue.OpenedAt).ShouldBe(TimeSpan.FromHours(72));
        issue.AllowedActions.ShouldBe(["comment", "addPhoto"]);

        var contractor = await (await s.Landlord.PostJsonAsync("/api/v1/contractors",
            new ContractorRequest($"Plumber {Guid.NewGuid():N}", "Plumber", "082 555 0199", null, null))).ReadAsync<ContractorDto>();

        var acked = await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/acknowledge");
        acked.AllowedActions.ShouldContain("start");
        var assigned = await (await s.Landlord.PutJsonAsync($"/api/v1/issues/{issue.Id}/contractor",
            new AssignContractorRequest(contractor.Id))).ReadAsync<IssueDto>();
        assigned.Contractor!.Name.ShouldBe(contractor.Name);
        await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/start");
        var resolved = await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/resolve", new ResolveIssueRequest("Element replaced"));
        resolved.ResponseMet.ShouldBe(true);
        resolved.ResolutionMet.ShouldBe(true);

        // The landlord can't sign off their own fix; the tenant does.
        await (await s.Landlord.PostAsync($"/api/v1/issues/{issue.Id}/close", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "issue.tenant_confirms");
        var tenantView = await (await tenant.GetAsync($"/api/v1/issues/{issue.Id}")).ReadAsync<IssueDto>();
        tenantView.AllowedActions.ShouldContain("close");
        tenantView.Contractor!.Phone.ShouldBe("+27825550199");
        var closed = await PostAsync(tenant, $"/api/v1/issues/{issue.Id}/close");

        closed.Status.ShouldBe(IssueStatus.Closed);
        closed.Events.Select(e => e.Kind).ShouldBe([IssueEventKind.Opened, IssueEventKind.Acknowledged,
            IssueEventKind.ContractorAssigned, IssueEventKind.Started, IssueEventKind.Resolved, IssueEventKind.Closed]);
        closed.AllowedActions.ShouldBeEmpty();
    }

    [Fact]
    public async Task BR14_Tenants_cannot_move_an_issue_along_or_skip_a_step()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id);

        (await tenant.PostAsync($"/api/v1/issues/{issue.Id}/acknowledge", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tenant.PutJsonAsync($"/api/v1/issues/{issue.Id}/priority", new ChangePriorityRequest(IssuePriority.Urgent)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await (await s.Landlord.PostJsonAsync($"/api/v1/issues/{issue.Id}/resolve", new ResolveIssueRequest(null)))
            .ShouldBeAsync(HttpStatusCode.Conflict, "issue.invalid_transition");
        await (await tenant.PostAsync($"/api/v1/issues/{issue.Id}/close", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "issue.invalid_transition");
    }

    [Fact]
    public async Task BR14_The_tenant_reopens_with_a_reason_when_it_is_not_fixed()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id);
        await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/start");
        await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/resolve", new ResolveIssueRequest(null));

        await (await tenant.PostJsonAsync($"/api/v1/issues/{issue.Id}/reopen", new ReopenIssueRequest("")))
            .ShouldBeAsync(HttpStatusCode.BadRequest, "validation");
        var reopened = await PostAsync(tenant, $"/api/v1/issues/{issue.Id}/reopen", new ReopenIssueRequest("Still no hot water"));

        reopened.Status.ShouldBe(IssueStatus.InProgress);
        reopened.ResolvedAt.ShouldBeNull();
        reopened.ReopenCount.ShouldBe(1);
        reopened.Events.Last().Note.ShouldBe("Still no hot water");
    }

    [Fact]
    public async Task BR01_A_tenant_only_sees_their_own_issues()
    {
        var (_, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id);
        var other = await api.ClientForAsync(Seeded.OtherTenantWithLogin);

        (await other.GetAsync($"/api/v1/issues/{issue.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PostJsonAsync($"/api/v1/issues/{issue.Id}/comments", new IssueCommentRequest("hi")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var list = await (await other.GetAsync("/api/v1/issues?pageSize=100")).ReadAsync<PagedResult<IssueSummaryDto>>();
        list.Items.ShouldNotContain(i => i.Id == issue.Id);

        // Nor can they log one against somebody else's lease.
        (await other.PostJsonAsync("/api/v1/issues", new CreateIssueRequest(lease.Id, null, IssueCategory.Other,
            IssuePriority.Low, "Not mine", "Trying someone else's lease"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_landlord_logs_an_issue_on_a_unit_and_it_is_shared_with_the_unit_s_tenant()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();

        var onLetUnit = await (await s.Landlord.PostJsonAsync("/api/v1/issues", new CreateIssueRequest(null, s.UnitId,
            IssueCategory.Garden, IssuePriority.Low, "Hedge trimming", "Quarterly hedge trim."))).ReadAsync<IssueDto>();
        onLetUnit.LeaseId.ShouldBe(lease.Id);
        (await tenant.GetAsync($"/api/v1/issues/{onLetUnit.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await s.Landlord.PostJsonAsync("/api/v1/issues", new CreateIssueRequest(null, s.SecondUnitId,
            IssueCategory.Structural, IssuePriority.Low, "Paint before viewings", "Touch up the walls."));
        await response.ShouldBeAsync(HttpStatusCode.Created);
        var onVacant = await response.ReadAsync<IssueDto>();
        onVacant.TenantId.ShouldBeNull();

        // With no tenant to confirm, the landlord closes it.
        await PostAsync(s.Landlord, $"/api/v1/issues/{onVacant.Id}/start");
        var resolved = await PostAsync(s.Landlord, $"/api/v1/issues/{onVacant.Id}/resolve", new ResolveIssueRequest("Done"));
        resolved.AllowedActions.ShouldContain("close");
        (await PostAsync(s.Landlord, $"/api/v1/issues/{onVacant.Id}/close")).Status.ShouldBe(IssueStatus.Closed);
    }

    [Fact]
    public async Task BR15_A_missed_response_target_escalates_the_issue_and_shows_in_the_filter()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id, IssuePriority.Urgent); // respond within 4 hours

        await s.RunChecksAsync();
        (await (await s.Landlord.GetAsync($"/api/v1/issues/{issue.Id}")).ReadAsync<IssueDto>()).IsEscalated.ShouldBeFalse();

        await BackdateAsync(issue.Id, hours: 5);
        var summary = await (await s.Landlord.PostAsync("/api/v1/lease-checks/run", null)).ReadAsync<DailyChecksSummary>();

        summary.IssuesEscalated.ShouldBeGreaterThanOrEqualTo(1);
        var escalated = await (await s.Landlord.GetAsync($"/api/v1/issues/{issue.Id}")).ReadAsync<IssueDto>();
        escalated.IsEscalated.ShouldBeTrue();
        escalated.ResponseMet.ShouldBe(false);
        escalated.ResponseMissedAt.ShouldNotBeNull();
        escalated.Events.Last().Kind.ShouldBe(IssueEventKind.ResponseTargetMissed);

        var list = await (await s.Landlord.GetAsync($"/api/v1/issues?escalated=true&propertyId={s.PropertyId}"))
            .ReadAsync<PagedResult<IssueSummaryDto>>();
        list.Items.Select(i => i.Id).ShouldBe([issue.Id]);

        // Running the checks again doesn't escalate it twice.
        await s.RunChecksAsync();
        (await (await s.Landlord.GetAsync($"/api/v1/issues/{issue.Id}")).ReadAsync<IssueDto>())
            .Events.Count(e => e.Kind == IssueEventKind.ResponseTargetMissed).ShouldBe(1);
    }

    [Fact]
    public async Task BR15_Raising_the_priority_brings_the_targets_forward()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id, IssuePriority.Low);

        var changed = await (await s.Landlord.PutJsonAsync($"/api/v1/issues/{issue.Id}/priority",
            new ChangePriorityRequest(IssuePriority.Urgent))).ReadAsync<IssueDto>();

        (changed.ResponseDueAt - changed.OpenedAt).ShouldBe(TimeSpan.FromHours(4));
        changed.Events.Last().Note.ShouldBe("Low to Urgent");
    }

    [Fact]
    public async Task A_resolved_issue_closes_by_itself_when_the_tenant_does_not_reply()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id, IssuePriority.Low);
        await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/start");
        await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/resolve", new ResolveIssueRequest(null));

        await BackdateAsync(issue.Id, hours: 8 * 24);
        await s.RunChecksAsync();

        var closed = await (await tenant.GetAsync($"/api/v1/issues/{issue.Id}")).ReadAsync<IssueDto>();
        closed.Status.ShouldBe(IssueStatus.Closed);
        closed.Events.Last().Kind.ShouldBe(IssueEventKind.AutoClosed);
    }

    [Fact]
    public async Task BR12_Photos_are_JPG_or_PNG_up_to_5MB_and_at_most_five_per_issue()
    {
        var (_, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id);
        var url = $"/api/v1/issues/{issue.Id}/photos";

        await (await tenant.UploadPhotoAsync(url, PaymentHelpers.UniquePdf(), "photo.png"))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "file.type_not_allowed"); // a PDF renamed .png
        await (await tenant.UploadPhotoAsync(url, IssueHelpers.UniquePng(5 * 1024 * 1024 + 1)))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "file.too_large");

        var first = IssueHelpers.UniquePng();
        await (await tenant.UploadPhotoAsync(url, first)).ShouldBeAsync(HttpStatusCode.OK);
        await (await tenant.UploadPhotoAsync(url, first)).ShouldBeAsync(HttpStatusCode.Conflict, "issue.duplicate_photo");
        for (var i = 0; i < 4; i++)
            await (await tenant.UploadPhotoAsync(url, IssueHelpers.UniquePng())).ShouldBeAsync(HttpStatusCode.OK);
        await (await tenant.UploadPhotoAsync(url, IssueHelpers.UniquePng()))
            .ShouldBeAsync(HttpStatusCode.Conflict, "issue.too_many_photos");

        var detail = await (await tenant.GetAsync($"/api/v1/issues/{issue.Id}")).ReadAsync<IssueDto>();
        detail.Photos.Count.ShouldBe(5);
        detail.AllowedActions.ShouldNotContain("addPhoto");
        var photo = await tenant.GetAsync($"{url}/{detail.Photos[0].Id}");
        photo.StatusCode.ShouldBe(HttpStatusCode.OK);
        photo.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await photo.Content.ReadAsByteArrayAsync()).ShouldBe(first);

        var other = await api.ClientForAsync(Seeded.OtherTenantWithLogin);
        (await other.GetAsync($"{url}/{detail.Photos[0].Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Comments_from_both_sides_appear_on_the_issue()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id);

        await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/comments", new IssueCommentRequest("Plumber booked for Thursday."));
        var detail = await PostAsync(tenant, $"/api/v1/issues/{issue.Id}/comments", new IssueCommentRequest("Thanks!"));

        detail.Comments.Select(c => (c.Body, c.ByTenant)).ShouldBe([("Plumber booked for Thursday.", false), ("Thanks!", true)]);
        await (await tenant.PostJsonAsync($"/api/v1/issues/{issue.Id}/comments", new IssueCommentRequest(new string('x', 2001))))
            .ShouldBeAsync(HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task An_issue_can_only_be_logged_on_a_running_lease()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        var pending = await s.DraftAsync(LeaseScenario.Today.AddMonths(1));
        await s.GenerateAsync(pending.Id);
        var tenant = await s.TenantClientAsync();

        await (await tenant.PostJsonAsync("/api/v1/issues", new CreateIssueRequest(pending.Id, null, IssueCategory.Other,
                IssuePriority.Low, "Before moving in", "Not running yet")))
            .ShouldBeAsync(HttpStatusCode.Conflict, "lease.not_running");
    }

    [Fact]
    public async Task BR20_Issue_changes_are_audited()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id);
        await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/acknowledge");

        var admin = await api.ClientForAsync(Seeded.Admin);
        var audit = await admin.GetAsync($"/api/v1/audit?entityType=Issue&entityId={issue.Id}");
        await audit.ShouldBeAsync(HttpStatusCode.OK);
        var rows = await audit.ReadAsync<PagedResult<AuditLogDto>>();
        rows.Items.Count.ShouldBeGreaterThanOrEqualTo(2);
        rows.Items.ShouldContain(r => r.AfterJson != null && r.AfterJson.Contains("Acknowledged"));
    }

    [Fact]
    public async Task The_SLA_report_counts_targets_met_per_priority()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var issue = await tenant.LogIssueAsync(lease.Id, IssuePriority.High);
        await PostAsync(s.Landlord, $"/api/v1/issues/{issue.Id}/acknowledge");

        var report = await (await s.Landlord.GetAsync("/api/v1/issues/sla-report?days=30")).ReadAsync<SlaReportDto>();

        report.Priorities.Select(p => p.Priority).ShouldBe([IssuePriority.Urgent, IssuePriority.High, IssuePriority.Medium,
            IssuePriority.Low]);
        var high = report.Priorities.Single(p => p.Priority == IssuePriority.High);
        high.ResponseTargetHours.ShouldBe(24);
        high.Acknowledged.ShouldBeGreaterThanOrEqualTo(1);
        high.AcknowledgedOnTime.ShouldBeGreaterThanOrEqualTo(1);
        (await tenant.GetAsync("/api/v1/issues/sla-report")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Seeded_demo_data_includes_an_escalated_issue_and_contractors()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var escalated = await (await landlord.GetAsync("/api/v1/issues?escalated=true&pageSize=100"))
            .ReadAsync<PagedResult<IssueSummaryDto>>();
        escalated.Items.ShouldContain(i => i.Title == "Geyser not heating");

        var contractors = await (await landlord.GetAsync("/api/v1/contractors")).ReadAsync<PagedResult<ContractorDto>>();
        contractors.Items.ShouldContain(c => c.Name == "Bongani Mthembu Plumbing" && c.UnfinishedIssues == 1);
    }
}

[Collection(ApiCollection.Name)]
public class ContractorApiTests(ApiFactory api)
{
    [Fact]
    public async Task Contractors_are_validated_and_only_for_the_landlord()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        await (await landlord.PostJsonAsync("/api/v1/contractors", new ContractorRequest("", "Plumber", "123", "not-an-email", null)))
            .ShouldBeAsync(HttpStatusCode.BadRequest, "validation");

        var tenant = await api.ClientForAsync(Seeded.TenantWithLogin);
        (await tenant.GetAsync("/api/v1/contractors")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_contractor_on_an_unfinished_issue_cannot_be_deleted()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var s = await LeaseScenario.CreateAsync(api, landlord);
        var contractor = await (await landlord.PostJsonAsync("/api/v1/contractors",
            new ContractorRequest($"Sparky {Guid.NewGuid():N}", "Electrician", "0835550190", null, null))).ReadAsync<ContractorDto>();
        var issue = await (await landlord.PostJsonAsync("/api/v1/issues", new CreateIssueRequest(null, s.UnitId,
            IssueCategory.Electrical, IssuePriority.High, "DB board trips", "Trips every evening."))).ReadAsync<IssueDto>();
        await (await landlord.PutJsonAsync($"/api/v1/issues/{issue.Id}/contractor", new AssignContractorRequest(contractor.Id)))
            .ShouldBeAsync(HttpStatusCode.OK);

        await (await landlord.DeleteAsync($"/api/v1/contractors/{contractor.Id}"))
            .ShouldBeAsync(HttpStatusCode.Conflict, "contractor.has_open_issues");

        await (await landlord.PutJsonAsync($"/api/v1/issues/{issue.Id}/contractor", new AssignContractorRequest(null)))
            .ShouldBeAsync(HttpStatusCode.OK);
        await (await landlord.DeleteAsync($"/api/v1/contractors/{contractor.Id}")).ShouldBeAsync(HttpStatusCode.NoContent);
        (await landlord.GetAsync($"/api/v1/contractors/{contractor.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
