using Shouldly;
using TenantManager.Domain.Common;
using TenantManager.Domain.Inspections;
using TenantManager.Domain.Issues;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Settings;
using TenantManager.Domain.Tenants;

namespace TenantManager.Domain.Tests;

public class IssueTests
{
    private static readonly DateTimeOffset Opened = new(2026, 11, 4, 8, 0, 0, TimeSpan.FromHours(2));
    private static readonly IssueActor Landlord = new(Guid.NewGuid(), "Thandi Nkosi", IsTenant: false);
    private static readonly IssueActor TenantUser = new(Guid.NewGuid(), "Sipho Dlamini", IsTenant: true);
    private static readonly SlaTargets Targets = SlaTargets.Defaults;

    private static Issue NewIssue(IssuePriority priority = IssuePriority.High, bool withTenant = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), withTenant ? Guid.NewGuid() : null, withTenant ? Guid.NewGuid() : null,
            IssueCategory.Plumbing, priority, "Kitchen tap leaking", "Drips all night.",
            withTenant ? TenantUser : Landlord, Targets, Opened);

    private static Issue InProgress()
    {
        var issue = NewIssue();
        issue.Acknowledge(Landlord, Opened.AddHours(1));
        issue.Start(Landlord, Opened.AddHours(2));
        return issue;
    }

    private static Issue Resolved()
    {
        var issue = InProgress();
        issue.Resolve("Washer replaced", Landlord, Opened.AddHours(30));
        return issue;
    }

    private static StoredFile Photo(string sha = "") =>
        new($"issues/x/{Guid.NewGuid():N}.jpg", "tap.jpg", "image/jpeg", 2048,
            sha.Length > 0 ? sha : Guid.NewGuid().ToString("N").PadRight(64, '0'));

    [Theory]
    [InlineData(IssuePriority.Urgent, 4, 24)]
    [InlineData(IssuePriority.High, 24, 72)]
    [InlineData(IssuePriority.Medium, 48, 168)]
    [InlineData(IssuePriority.Low, 72, 336)]
    public void BR15_Targets_come_from_the_priority(IssuePriority priority, int responseHours, int resolutionHours)
    {
        var issue = NewIssue(priority);

        issue.Status.ShouldBe(IssueStatus.Open);
        issue.ResponseDueAt.ShouldBe(Opened.AddHours(responseHours));
        issue.ResolutionDueAt.ShouldBe(Opened.AddHours(resolutionHours));
        issue.Events.Single().Kind.ShouldBe(IssueEventKind.Opened);
    }

    [Fact]
    public void BR14_Walks_the_full_flow_with_a_timestamp_at_every_step()
    {
        var issue = NewIssue();
        issue.Acknowledge(Landlord, Opened.AddHours(1));
        issue.Start(Landlord, Opened.AddHours(2));
        issue.Resolve("Washer replaced", Landlord, Opened.AddHours(30));
        issue.Close(TenantUser, Opened.AddHours(40));

        issue.Status.ShouldBe(IssueStatus.Closed);
        issue.AcknowledgedAt.ShouldBe(Opened.AddHours(1));
        issue.StartedAt.ShouldBe(Opened.AddHours(2));
        issue.ResolvedAt.ShouldBe(Opened.AddHours(30));
        issue.ClosedAt.ShouldBe(Opened.AddHours(40));
        issue.ResolutionNote.ShouldBe("Washer replaced");
        issue.ResponseMet(Opened.AddDays(9)).ShouldBe(true);
        issue.ResolutionMet(Opened.AddDays(9)).ShouldBe(true);
        issue.Events.Select(e => e.Kind).ShouldBe([IssueEventKind.Opened, IssueEventKind.Acknowledged,
            IssueEventKind.Started, IssueEventKind.Resolved, IssueEventKind.Closed]);
    }

    [Fact]
    public void BR14_Steps_cannot_be_skipped_or_repeated()
    {
        var issue = NewIssue();
        Should.Throw<DomainRuleException>(() => issue.Resolve(null, Landlord, Opened)).Code.ShouldBe("issue.invalid_transition");
        Should.Throw<DomainRuleException>(() => issue.Close(TenantUser, Opened)).Code.ShouldBe("issue.invalid_transition");
        issue.Acknowledge(Landlord, Opened);
        Should.Throw<DomainRuleException>(() => issue.Acknowledge(Landlord, Opened)).Kind.ShouldBe(RuleKind.Conflict);
    }

    [Fact]
    public void BR14_Starting_an_open_issue_acknowledges_it_too()
    {
        var issue = NewIssue();
        issue.Start(Landlord, Opened.AddHours(3));

        issue.Status.ShouldBe(IssueStatus.InProgress);
        issue.AcknowledgedAt.ShouldBe(Opened.AddHours(3));
    }

    [Fact]
    public void BR14_Only_the_landlord_moves_an_issue_along()
    {
        var issue = NewIssue();
        Should.Throw<UnauthorizedAccessException>(() => issue.Acknowledge(TenantUser, Opened));
        Should.Throw<UnauthorizedAccessException>(() => issue.Start(TenantUser, Opened));
        Should.Throw<UnauthorizedAccessException>(() => issue.ChangePriority(IssuePriority.Urgent, TenantUser, Targets, Opened));
    }

    [Fact]
    public void BR14_The_tenant_confirms_the_fix_not_the_landlord()
    {
        var issue = Resolved();
        Should.Throw<DomainRuleException>(() => issue.Close(Landlord, Opened.AddHours(31))).Code.ShouldBe("issue.tenant_confirms");
    }

    [Fact]
    public void BR14_The_landlord_closes_an_issue_on_a_vacant_unit()
    {
        var issue = NewIssue(withTenant: false);
        issue.Start(Landlord, Opened);
        issue.Resolve(null, Landlord, Opened.AddHours(1));
        issue.Close(Landlord, Opened.AddHours(2));
        issue.Status.ShouldBe(IssueStatus.Closed);
    }

    [Fact]
    public void BR14_Reopening_needs_a_reason_and_goes_back_to_in_progress()
    {
        var issue = Resolved();
        Should.Throw<DomainRuleException>(() => issue.Reopen(" ", TenantUser, Opened.AddHours(31))).Code.ShouldBe("reason.required");

        issue.Reopen("Still dripping", TenantUser, Opened.AddHours(31));

        issue.Status.ShouldBe(IssueStatus.InProgress);
        issue.ResolvedAt.ShouldBeNull();
        issue.ReopenCount.ShouldBe(1);
        issue.ResolutionDueAt.ShouldBe(Opened.AddHours(72)); // the target doesn't move
        issue.Events.Last().Note.ShouldBe("Still dripping");
    }

    [Fact]
    public void BR15_A_missed_response_target_escalates_once()
    {
        var issue = NewIssue(IssuePriority.Urgent); // respond within 4 hours

        issue.CheckTargets(Opened.AddHours(4)).ShouldBeFalse(); // exactly on time is not late
        issue.IsEscalated.ShouldBeFalse();
        issue.ResponseMet(Opened.AddHours(4)).ShouldBeNull();

        issue.CheckTargets(Opened.AddHours(4).AddMinutes(1)).ShouldBeTrue();
        issue.IsEscalated.ShouldBeTrue();
        issue.ResponseMissedAt.ShouldNotBeNull();
        issue.ResponseMet(Opened.AddHours(5)).ShouldBe(false);

        issue.CheckTargets(Opened.AddHours(6)).ShouldBeFalse(); // not again
        issue.Events.Count(e => e.Kind == IssueEventKind.ResponseTargetMissed).ShouldBe(1);
        issue.Events.Last().ByName.ShouldBe("System");
    }

    [Fact]
    public void BR15_A_missed_resolution_target_escalates_even_after_a_timely_response()
    {
        var issue = InProgress(); // High: resolve within 72 hours

        issue.CheckTargets(Opened.AddHours(71)).ShouldBeFalse();
        issue.CheckTargets(Opened.AddHours(73)).ShouldBeTrue();

        issue.ResponseMissedAt.ShouldBeNull();
        issue.ResolutionMissedAt.ShouldNotBeNull();
        issue.IsEscalated.ShouldBeTrue();
    }

    [Fact]
    public void BR15_Resolved_and_closed_issues_are_not_escalated()
    {
        var resolved = Resolved();
        resolved.CheckTargets(Opened.AddDays(30)).ShouldBeFalse();
        resolved.IsEscalated.ShouldBeFalse();
    }

    [Fact]
    public void BR15_Changing_the_priority_recalculates_the_targets_from_when_it_was_opened()
    {
        var issue = NewIssue(IssuePriority.Low);
        issue.ChangePriority(IssuePriority.Urgent, Landlord, Targets, Opened.AddHours(1));

        issue.ResponseDueAt.ShouldBe(Opened.AddHours(4));
        issue.ResolutionDueAt.ShouldBe(Opened.AddHours(24));
        issue.Events.Last().Note.ShouldBe("Low to Urgent");
    }

    [Fact]
    public void BR15_A_missed_target_stays_recorded_when_the_priority_is_lowered()
    {
        var issue = NewIssue(IssuePriority.Urgent);
        issue.CheckTargets(Opened.AddHours(5));
        issue.ChangePriority(IssuePriority.Low, Landlord, Targets, Opened.AddHours(6));

        issue.IsEscalated.ShouldBeTrue();
        issue.ResponseMissedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Priority_is_locked_once_resolved()
    {
        Should.Throw<DomainRuleException>(() => Resolved().ChangePriority(IssuePriority.Low, Landlord, Targets, Opened))
            .Code.ShouldBe("issue.priority_locked");
    }

    [Fact]
    public void A_resolved_issue_closes_by_itself_after_the_set_days()
    {
        var issue = Resolved(); // resolved at +30h
        issue.AutoClose(Opened.AddHours(30).AddDays(7).AddMinutes(-1), 7).ShouldBeFalse();
        issue.AutoClose(Opened.AddHours(30).AddDays(7), 7).ShouldBeTrue();

        issue.Status.ShouldBe(IssueStatus.Closed);
        issue.Events.Last().Kind.ShouldBe(IssueEventKind.AutoClosed);
        NewIssue().AutoClose(Opened.AddYears(1), 7).ShouldBeFalse();
    }

    [Fact]
    public void BR12_An_issue_has_at_most_five_photos_each_added_once()
    {
        var issue = NewIssue();
        var first = Photo();
        issue.AddPhoto(first, TenantUser, Opened);
        Should.Throw<DomainRuleException>(() => issue.AddPhoto(first, TenantUser, Opened)).Code.ShouldBe("issue.duplicate_photo");
        for (var i = 0; i < 4; i++) issue.AddPhoto(Photo(), TenantUser, Opened);

        Should.Throw<DomainRuleException>(() => issue.AddPhoto(Photo(), TenantUser, Opened)).Code.ShouldBe("issue.too_many_photos");
        issue.Photos.Count.ShouldBe(Issue.MaxPhotos);
    }

    [Fact]
    public void Comments_are_kept_with_the_author_and_refused_once_closed()
    {
        var issue = Resolved();
        var comment = issue.AddComment("Thanks, fixed", TenantUser, Opened.AddHours(31));
        comment.AuthorName.ShouldBe("Sipho Dlamini");
        comment.ByTenant.ShouldBeTrue();

        Should.Throw<DomainRuleException>(() => issue.AddComment(new string('x', IssueComment.BodyMax + 1), TenantUser, Opened))
            .Code.ShouldBe("body.too_long");
        issue.Close(TenantUser, Opened.AddHours(32));
        Should.Throw<DomainRuleException>(() => issue.AddComment("Hello?", TenantUser, Opened)).Code.ShouldBe("issue.closed");
    }

    [Fact]
    public void Assigning_a_contractor_is_recorded_in_the_history()
    {
        var issue = InProgress();
        var contractor = Guid.NewGuid();
        issue.AssignContractor(contractor, "Bongani Plumbing", Landlord, Opened.AddHours(3));
        issue.AssignContractor(contractor, "Bongani Plumbing", Landlord, Opened.AddHours(3)); // no change, no event

        issue.ContractorId.ShouldBe(contractor);
        issue.Events.Count(e => e.Kind == IssueEventKind.ContractorAssigned).ShouldBe(1);
    }
}

public class SlaTargetTests
{
    [Fact]
    public void Defaults_are_valid_and_round_trip_through_settings()
    {
        SlaTargets.Defaults.Validate();
        BusinessSettings.CreateDefault().ToSlaTargets().ShouldBe(SlaTargets.Defaults);
    }

    [Fact]
    public void Response_cannot_be_later_than_resolution()
    {
        Should.Throw<DomainRuleException>(() => (SlaTargets.Defaults with { High = new SlaTarget(80, 72) }).Validate())
            .Code.ShouldBe("sla.high.invalid");
    }

    [Fact]
    public void A_more_urgent_priority_cannot_have_slower_targets()
    {
        Should.Throw<DomainRuleException>(() => (SlaTargets.Defaults with { Urgent = new SlaTarget(48, 100) }).Validate())
            .Code.ShouldBe("sla.order_invalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Auto_close_days_are_limited(int days)
    {
        Should.Throw<DomainRuleException>(() => (SlaTargets.Defaults with { AutoCloseDays = days }).Validate())
            .Code.ShouldBe("issueAutoCloseDays.invalid");
    }
}

public class ContractorTests
{
    [Fact]
    public void Phone_is_normalised_and_email_lower_cased()
    {
        var c = new Contractor(Guid.NewGuid(), "Bongani Plumbing", "Plumber", "082 555 0123", "Info@Example.COM", null);
        c.Phone.ShouldBe("+27825550123");
        c.Email.ShouldBe("info@example.com");
    }

    [Fact]
    public void Cannot_be_deleted_while_assigned_to_unfinished_issues()
    {
        var c = new Contractor(Guid.NewGuid(), "Bongani Plumbing", "Plumber", "0825550123", null, null);
        Should.Throw<DomainRuleException>(() => c.Delete(2, Guid.NewGuid(), DateTimeOffset.UtcNow))
            .Code.ShouldBe("contractor.has_open_issues");
        c.Delete(0, Guid.NewGuid(), DateTimeOffset.UtcNow);
        c.IsDeleted.ShouldBeTrue();
    }
}

public class InspectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 4);

    private static Lease NewLease(bool sign = true)
    {
        var owner = Guid.NewGuid();
        var property = new Property(owner, "Jacaranda Court",
            new PropertyAddress("14 Jacaranda Street", null, "Pretoria", "Gauteng", "0083"));
        var unit = property.AddUnit("Flat 1", 6500m, 6500m);
        var tenant = new Tenant(owner, "Sipho", "Dlamini", "sipho@example.com", "0825550101");
        var lease = Leases.Lease.CreateDraft("L2026-0001", unit, tenant, new DateOnly(2026, 11, 1), 6500m, 6500m, 8m,
            LeaseSettings.Defaults);
        if (!sign) return lease;
        lease.Generate("Thandi Nkosi", "v0-test", new LeaseDocument(LeaseDocumentKind.Unsigned, "leases/x.pdf", new string('A', 64), 10));
        lease.Sign(LeaseParty.Landlord, Guid.NewGuid(), "Thandi Nkosi", null, Now, Today);
        lease.Sign(LeaseParty.Tenant, Guid.NewGuid(), "Sipho Dlamini", null, Now, Today);
        return lease;
    }

    private static StoredFile Photo() =>
        new($"inspections/{Guid.NewGuid():N}.jpg", "lounge.jpg", "image/jpeg", 1000, Guid.NewGuid().ToString("N").PadRight(64, '0'));

    [Fact]
    public void Move_in_starts_with_the_default_checklist()
    {
        var inspection = Inspection.StartMoveIn(NewLease(), Today);

        inspection.Kind.ShouldBe(InspectionKind.MoveIn);
        inspection.Status.ShouldBe(InspectionStatus.Draft);
        inspection.Items.Select(i => i.Area).ShouldBe(Inspection.DefaultAreas);
        inspection.LeaseNumber.ShouldBe("L2026-0001");
    }

    [Fact]
    public void A_draft_lease_has_no_inspection()
    {
        Should.Throw<DomainRuleException>(() => Inspection.StartMoveIn(NewLease(sign: false), Today))
            .Code.ShouldBe("inspection.lease_not_in_force");
        Should.Throw<DomainRuleException>(() => Inspection.StartMoveOut(NewLease(sign: false), null, Today))
            .Code.ShouldBe("inspection.lease_not_started");
    }

    [Fact]
    public void Move_out_uses_the_areas_from_move_in()
    {
        var lease = NewLease();
        var moveIn = Inspection.StartMoveIn(lease, Today);
        moveIn.AddItem("Garage");

        var moveOut = Inspection.StartMoveOut(lease, moveIn, Today);

        moveOut.Items.Select(i => i.Area).Last().ShouldBe("Garage");
        moveOut.Items.Count.ShouldBe(Inspection.DefaultAreas.Count + 1);
    }

    [Fact]
    public void Every_area_needs_a_condition_before_completing_and_then_it_is_locked()
    {
        var inspection = Inspection.StartMoveIn(NewLease(), Today);
        Should.Throw<DomainRuleException>(() => inspection.Complete("Thandi Nkosi", Now)).Code.ShouldBe("inspection.items_unchecked");

        foreach (var item in inspection.Items) inspection.UpdateItem(item, ItemCondition.Good, null);
        inspection.Complete("Thandi Nkosi", Now);

        inspection.Status.ShouldBe(InspectionStatus.Completed);
        Should.Throw<DomainRuleException>(() => inspection.AddItem("Garage")).Code.ShouldBe("inspection.completed");
        Should.Throw<DomainRuleException>(() => inspection.UpdateItem(inspection.Items.First(), ItemCondition.Poor, "Scuffed"))
            .Kind.ShouldBe(RuleKind.Conflict);
    }

    [Fact]
    public void Areas_are_unique_on_a_checklist()
    {
        var inspection = Inspection.StartMoveIn(NewLease(), Today);
        Should.Throw<DomainRuleException>(() => inspection.AddItem("lounge")).Code.ShouldBe("area.duplicate");
    }

    [Fact]
    public void Each_area_has_at_most_five_photos()
    {
        var inspection = Inspection.StartMoveIn(NewLease(), Today);
        var item = inspection.Items.First();
        for (var i = 0; i < Inspection.MaxPhotosPerItem; i++) inspection.AddPhoto(item, Photo(), Now);
        Should.Throw<DomainRuleException>(() => inspection.AddPhoto(item, Photo(), Now)).Code.ShouldBe("inspection.too_many_photos");
    }

    [Fact]
    public void The_tenant_acknowledges_a_completed_inspection_once_with_an_optional_comment()
    {
        var inspection = Inspection.StartMoveIn(NewLease(), Today);
        Should.Throw<DomainRuleException>(() => inspection.Acknowledge(null, Now)).Code.ShouldBe("inspection.not_completed");
        foreach (var item in inspection.Items) inspection.UpdateItem(item, ItemCondition.Good, null);
        inspection.Complete("Thandi Nkosi", Now);

        inspection.Acknowledge("The bathroom tile was already cracked", Now);

        inspection.TenantComment.ShouldBe("The bathroom tile was already cracked");
        Should.Throw<DomainRuleException>(() => inspection.Acknowledge(null, Now)).Code.ShouldBe("inspection.already_acknowledged");
    }

    [Fact]
    public void The_inspection_date_cannot_be_in_the_future()
    {
        Should.Throw<DomainRuleException>(() => Inspection.StartMoveIn(NewLease(), Today).UpdateDetails(Today.AddDays(1), null, Today))
            .Code.ShouldBe("inspectedOn.in_future");
    }
}
