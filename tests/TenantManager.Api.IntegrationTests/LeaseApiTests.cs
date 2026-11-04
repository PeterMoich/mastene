using System.Net;
using System.Text;
using Shouldly;
using TenantManager.Application.Auditing;
using TenantManager.Application.Common;
using TenantManager.Application.Leases;
using TenantManager.Application.Properties;
using TenantManager.Application.Settings;
using TenantManager.Domain.Leases;
using UglyToad.PdfPig;

namespace TenantManager.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class LeaseApiTests(ApiFactory api)
{
    private static DateOnly Today => LeaseScenario.Today;

    private async Task<LeaseScenario> ScenarioAsync() =>
        await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));

    [Fact]
    public async Task Full_lifecycle_draft_to_active_with_both_PDFs()
    {
        var s = await ScenarioAsync();

        var draft = await s.DraftAsync(Today);
        draft.Status.ShouldBe(LeaseStatus.Draft);
        draft.LeaseNumber.ShouldStartWith($"L{Today.Year}-");
        draft.AllowedActions.ShouldBe(["edit", "discard", "generate"]);

        var pending = await s.GenerateAsync(draft.Id);
        pending.Status.ShouldBe(LeaseStatus.PendingSignature);
        pending.LandlordName.ShouldBe("Thandi Nkosi");
        pending.Documents.Single().Kind.ShouldBe(LeaseDocumentKind.Unsigned);

        var active = await s.SignBothAsync(draft.Id);
        active.Status.ShouldBe(LeaseStatus.Active);
        active.ActivatedOn.ShouldBe(Today);
        active.Signatures.Select(x => x.Party).ShouldBe([LeaseParty.Landlord, LeaseParty.Tenant]);
        active.Documents.Select(d => d.Kind).ShouldBe([LeaseDocumentKind.Unsigned, LeaseDocumentKind.Signed]);
        active.AllowedActions.ShouldBe(["download"]); // SignBothAsync returns the tenant's view
        (await s.GetAsync(draft.Id)).AllowedActions.ShouldBe(["download", "renew", "terminate"]); // landlord's view

        var unit = await (await s.Landlord.GetAsync($"/api/v1/units/{s.UnitId}")).ReadAsync<UnitDto>();
        unit.Status.ShouldBe("Occupied");
    }

    [Fact]
    public async Task Lease_PDF_contains_the_parties_and_the_signature_record()
    {
        var s = await ScenarioAsync();
        var lease = await s.ActiveAsync(Today);
        var signed = lease.Documents.Single(d => d.Kind == LeaseDocumentKind.Signed);

        var response = await s.Landlord.GetAsync($"/api/v1/leases/{lease.Id}/documents/{signed.Id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");

        using var pdf = PdfDocument.Open(bytes);
        var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        text.ShouldContain(lease.LeaseNumber);
        text.ShouldContain("Lease Tester");
        text.ShouldContain("Thandi Nkosi");
        text.ShouldContain("6,500.00");
        text.ShouldContain("LEGAL REVIEW NEEDED");
        text.ShouldContain("IP address");
    }

    [Fact]
    public async Task BR02_End_date_is_set_by_the_API_and_ignores_what_the_client_might_want()
    {
        var s = await ScenarioAsync();
        var draft = await s.DraftAsync(new DateOnly(2027, 3, 1));
        draft.EndDate.ShouldBe(new DateOnly(2028, 2, 29));
    }

    [Fact]
    public async Task BR04_A_generated_lease_cannot_be_edited_or_discarded()
    {
        var s = await ScenarioAsync();
        var draft = await s.DraftAsync(Today.AddDays(30));
        await s.GenerateAsync(draft.Id);

        await (await s.Landlord.PutJsonAsync($"/api/v1/leases/{draft.Id}",
                new LeaseRequest(s.UnitId, s.TenantId, Today.AddDays(30), 1m, 0m, 0m)))
            .ShouldBeAsync(HttpStatusCode.Conflict, "lease.not_draft");
        await (await s.Landlord.DeleteAsync($"/api/v1/leases/{draft.Id}"))
            .ShouldBeAsync(HttpStatusCode.Conflict, "lease.not_draft");
        await (await s.Landlord.PostAsync($"/api/v1/leases/{draft.Id}/generate", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "lease.not_draft");
        (await s.GetAsync(draft.Id)).MonthlyRent.ShouldBe(6500m);
    }

    [Fact]
    public async Task Drafts_can_be_edited_and_discarded()
    {
        var s = await ScenarioAsync();
        var draft = await s.DraftAsync(Today.AddDays(30));

        var edited = await (await s.Landlord.PutJsonAsync($"/api/v1/leases/{draft.Id}",
                new LeaseRequest(s.SecondUnitId, s.TenantId, Today.AddDays(31), 7100m, 7000m, 5m)))
            .ReadAsync<LeaseDto>();
        edited.UnitLabel.ShouldBe("Flat 2");
        edited.MonthlyRent.ShouldBe(7100m);

        (await s.Landlord.DeleteAsync($"/api/v1/leases/{draft.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Landlord.GetAsync($"/api/v1/leases/{draft.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BR03_A_second_lease_for_overlapping_dates_on_the_same_unit_is_rejected()
    {
        var s = await ScenarioAsync();
        await s.ActiveAsync(Today);
        await s.NewTenantAsync();

        // Rejected at the draft already, so the landlord finds out straight away.
        var response = await s.Landlord.PostJsonAsync("/api/v1/leases",
            new LeaseRequest(s.UnitId, s.TenantId, Today.AddMonths(6), 6500m, 6500m, 0m));
        await response.ShouldBeAsync(HttpStatusCode.Conflict, "lease.overlap");
    }

    [Fact]
    public async Task BR03_Back_to_back_leases_on_the_same_unit_are_allowed()
    {
        var s = await ScenarioAsync();
        var first = await s.ActiveAsync(Today);
        await s.NewTenantAsync();

        var next = await s.DraftAsync(first.EndDate.AddDays(1));
        (await s.GenerateAsync(next.Id)).Status.ShouldBe(LeaseStatus.PendingSignature);
    }

    [Fact]
    public async Task BR03_Concurrent_generation_of_overlapping_leases_lets_exactly_one_through()
    {
        var s = await ScenarioAsync();
        var first = await s.DraftAsync(Today.AddDays(10));
        await s.NewTenantAsync();
        var second = await s.DraftAsync(Today.AddDays(20)); // drafts don't block each other

        var responses = await Task.WhenAll(
            s.Landlord.PostAsync($"/api/v1/leases/{first.Id}/generate", null),
            s.Landlord.PostAsync($"/api/v1/leases/{second.Id}/generate", null));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var loser = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        await loser.ShouldBeAsync(HttpStatusCode.Conflict, "lease.overlap");
    }

    [Fact]
    public async Task BR03_A_tenant_cannot_lease_a_second_unit_in_the_same_property()
    {
        var s = await ScenarioAsync();
        await s.ActiveAsync(Today);

        await (await s.Landlord.PostJsonAsync("/api/v1/leases",
                new LeaseRequest(s.SecondUnitId, s.TenantId, Today.AddMonths(1), 7000m, 7000m, 0m)))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "lease.tenant_has_lease_in_property");
    }

    [Fact]
    public async Task BR03_The_same_tenant_may_lease_a_unit_in_a_different_property()
    {
        var s = await ScenarioAsync();
        await s.ActiveAsync(Today);
        var other = await ScenarioAsync();

        var response = await s.Landlord.PostJsonAsync("/api/v1/leases",
            new LeaseRequest(other.UnitId, s.TenantId, Today, 6500m, 6500m, 0m));
        await response.ShouldBeAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task BR01_A_tenant_sees_only_their_own_leases_and_never_drafts()
    {
        var s = await ScenarioAsync();
        var active = await s.ActiveAsync(Today);
        var draftForThem = await s.DraftAsync(active.EndDate.AddDays(1));
        var tenant = await s.TenantClientAsync();

        var mine = await (await tenant.GetAsync("/api/v1/leases?pageSize=100")).ReadAsync<PagedResult<LeaseSummaryDto>>();
        mine.Items.Select(l => l.Id).ShouldBe([active.Id]);
        (await tenant.GetAsync($"/api/v1/leases/{draftForThem.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var someoneElses = await ScenarioAsync();
        var theirLease = await someoneElses.ActiveAsync(Today);
        (await tenant.GetAsync($"/api/v1/leases/{theirLease.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var otherDoc = theirLease.Documents[0].Id;
        (await tenant.GetAsync($"/api/v1/leases/{theirLease.Id}/documents/{otherDoc}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BR01_A_tenant_cannot_manage_leases()
    {
        var s = await ScenarioAsync();
        var draft = await s.DraftAsync(Today.AddDays(5));
        var tenant = await s.TenantClientAsync();

        (await tenant.PostAsync($"/api/v1/leases/{draft.Id}/generate", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tenant.PostJsonAsync("/api/v1/leases", new LeaseRequest(s.UnitId, s.TenantId, Today, 1m, 0m, 0m)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tenant.PostAsync("/api/v1/lease-checks/run", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tenant_signs_their_own_lease_and_cannot_sign_twice()
    {
        var s = await ScenarioAsync();
        var draft = await s.DraftAsync(Today.AddDays(14));
        await s.GenerateAsync(draft.Id);
        var tenant = await s.TenantClientAsync();

        var signed = await (await tenant.PostAsync($"/api/v1/leases/{draft.Id}/sign", null)).ReadAsync<LeaseDto>();
        signed.Signatures.Single().Party.ShouldBe(LeaseParty.Tenant);
        signed.Signatures.Single().SignerName.ShouldBe("Lease Tester");
        signed.AllowedActions.ShouldBe(["download"]);

        await (await tenant.PostAsync($"/api/v1/leases/{draft.Id}/sign", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "lease.already_signed");
    }

    [Fact]
    public async Task A_fully_signed_lease_with_a_future_start_waits_until_its_start_date()
    {
        var s = await ScenarioAsync();
        var draft = await s.DraftAsync(Today.AddDays(14));
        await s.GenerateAsync(draft.Id);

        var signed = await s.SignBothAsync(draft.Id);

        signed.Status.ShouldBe(LeaseStatus.PendingSignature);
        signed.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task BR05_The_daily_checks_flag_a_lease_Expiring_and_record_the_notice()
    {
        var s = await ScenarioAsync();
        var lease = await s.ActiveAsync(Today.AddMonths(-11)); // ends in about a month

        await s.RunChecksAsync();

        var after = await s.GetAsync(lease.Id);
        after.Status.ShouldBe(LeaseStatus.Expiring);
        after.ExpiryNoticesRecorded.ShouldHaveSingleItem().ShouldBeOneOf(30, 60);
    }

    [Fact]
    public async Task BR06_Renewal_escalates_rent_needs_both_signatures_and_replaces_the_old_lease()
    {
        var s = await ScenarioAsync();
        var lease = await s.ActiveAsync(Today.AddMonths(-12)); // ends yesterday or earlier

        var renewal = await (await s.Landlord.PostJsonAsync($"/api/v1/leases/{lease.Id}/renewals",
            new RenewLeaseRequest(8m, null))).ReadAsync<LeaseDto>();
        renewal.Version.ShouldBe(2);
        renewal.LeaseNumber.ShouldBe(lease.LeaseNumber);
        renewal.MonthlyRent.ShouldBe(7020m);
        renewal.StartDate.ShouldBe(lease.EndDate.AddDays(1));
        renewal.PreviousLeaseId.ShouldBe(lease.Id);
        (await s.GetAsync(lease.Id)).RenewalLeaseId.ShouldBe(renewal.Id);

        await (await s.Landlord.PostJsonAsync($"/api/v1/leases/{lease.Id}/renewals", new RenewLeaseRequest(8m, null)))
            .ShouldBeAsync(HttpStatusCode.Conflict, "lease.renewal_exists");

        await s.GenerateAsync(renewal.Id);
        await (await s.Landlord.PostAsync($"/api/v1/leases/{renewal.Id}/sign", null)).ShouldBeAsync(HttpStatusCode.OK);
        (await s.GetAsync(lease.Id)).Status.ShouldBe(LeaseStatus.Active); // landlord alone isn't enough

        var tenant = await s.TenantClientAsync();
        var started = await (await tenant.PostAsync($"/api/v1/leases/{renewal.Id}/sign", null)).ReadAsync<LeaseDto>();
        started.Status.ShouldBe(LeaseStatus.Active);
        (await s.GetAsync(lease.Id)).Status.ShouldBe(LeaseStatus.Renewed);

        var versions = await (await s.Landlord.GetAsync($"/api/v1/leases/{renewal.Id}/versions"))
            .ReadAsync<List<LeaseSummaryDto>>();
        versions.Select(v => v.Version).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task BR06_Escalation_above_the_settings_maximum_is_rejected()
    {
        var s = await ScenarioAsync();
        var lease = await s.ActiveAsync(Today);

        await (await s.Landlord.PostJsonAsync($"/api/v1/leases/{lease.Id}/renewals", new RenewLeaseRequest(15m, null)))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "escalationPct.invalid");
    }

    [Fact]
    public async Task BR07_A_lapsed_lease_goes_month_to_month_or_ends_depending_on_the_setting()
    {
        var admin = await api.ClientForAsync(Seeded.Admin);
        var original = await (await admin.GetAsync("/api/v1/settings")).ReadAsync<SettingsDto>();
        var s = await ScenarioAsync();
        var continues = await s.ActiveAsync(Today.AddMonths(-12).AddDays(-3));
        var other = await ScenarioAsync();
        var ends = await other.ActiveAsync(Today.AddMonths(-12).AddDays(-3));

        // Setting on (the default): the first lease continues month to month.
        await s.RunChecksAsync();
        var mtm = await s.GetAsync(continues.Id);
        mtm.Status.ShouldBe(LeaseStatus.Active);
        mtm.IsMonthToMonth.ShouldBeTrue();

        // Setting off: a lease that lapses after the change ends instead, and its unit is freed.
        try
        {
            await (await admin.PutJsonAsync("/api/v1/settings", ToRequest(original) with { MonthToMonthOnLapse = false }))
                .ShouldBeAsync(HttpStatusCode.OK);
            var third = await ScenarioAsync();
            var lapsed = await third.ActiveAsync(Today.AddMonths(-12).AddDays(-3));
            await third.RunChecksAsync();

            var ended = await third.GetAsync(lapsed.Id);
            ended.Status.ShouldBe(LeaseStatus.Ended);
            var unit = await (await third.Landlord.GetAsync($"/api/v1/units/{third.UnitId}")).ReadAsync<UnitDto>();
            unit.Status.ShouldBe("Vacant");
        }
        finally
        {
            await admin.PutJsonAsync("/api/v1/settings", ToRequest(original));
        }
        (await other.GetAsync(ends.Id)).IsMonthToMonth.ShouldBeTrue(); // processed by the first run
    }

    [Fact]
    public async Task Termination_respects_the_notice_period_and_cancelling_a_pending_lease_is_immediate()
    {
        var s = await ScenarioAsync();
        var lease = await s.ActiveAsync(Today);

        await (await s.Landlord.PostJsonAsync($"/api/v1/leases/{lease.Id}/terminate",
                new TerminateLeaseRequest("Moving out", Today.AddDays(5))))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "lease.notice_too_short");

        var scheduled = await (await s.Landlord.PostJsonAsync($"/api/v1/leases/{lease.Id}/terminate",
            new TerminateLeaseRequest("Moving out", Today.AddDays(45)))).ReadAsync<LeaseDto>();
        scheduled.Status.ShouldBe(LeaseStatus.Active);
        scheduled.TerminatesOn.ShouldBe(Today.AddDays(45));
        scheduled.AllowedActions.ShouldNotContain("renew");

        await s.NewTenantAsync();
        var pending = await s.DraftAsync(Today.AddDays(46));
        await s.GenerateAsync(pending.Id); // allowed: the first lease now ends on day 45
        var cancelled = await (await s.Landlord.PostJsonAsync($"/api/v1/leases/{pending.Id}/terminate",
            new TerminateLeaseRequest("Changed plans", Today))).ReadAsync<LeaseDto>();
        cancelled.Status.ShouldBe(LeaseStatus.Terminated);
    }

    [Fact]
    public async Task A_unit_or_property_with_a_lease_in_force_cannot_be_deleted()
    {
        var s = await ScenarioAsync();
        var draft = await s.DraftAsync(Today.AddDays(10));
        await s.GenerateAsync(draft.Id);

        await (await s.Landlord.DeleteAsync($"/api/v1/units/{s.UnitId}"))
            .ShouldBeAsync(HttpStatusCode.Conflict, "unit.has_lease");
        await (await s.Landlord.DeleteAsync($"/api/v1/properties/{s.PropertyId}"))
            .ShouldBeAsync(HttpStatusCode.Conflict, "property.has_leases");
    }

    [Fact]
    public async Task BR20_Lease_status_changes_and_signatures_are_audited()
    {
        var s = await ScenarioAsync();
        var lease = await s.ActiveAsync(Today);

        var rows = (await (await s.Landlord.GetAsync($"/api/v1/audit?entityType=Lease&entityId={lease.Id}&pageSize=50"))
            .ReadAsync<PagedResult<AuditLogDto>>()).Items;
        // Created as a draft, updated when sent for signing, updated when the second signature started it.
        rows.Count(r => r.Action == "Created").ShouldBe(1);
        rows.Count(r => r.Action == "Updated").ShouldBe(2);
        rows.ShouldContain(r => r.AfterJson!.Contains("\"Status\":\"PendingSignature\""));
        rows.ShouldContain(r => r.AfterJson!.Contains("\"Status\":\"Active\""));

        var signatures = (await (await s.Landlord.GetAsync("/api/v1/audit?entityType=LeaseSignature&pageSize=100"))
            .ReadAsync<PagedResult<AuditLogDto>>()).Items;
        signatures.Count(r => r.AfterJson!.Contains(lease.Id.ToString())).ShouldBe(2);
    }

    [Fact]
    public async Task Settings_are_read_by_landlords_and_changed_only_by_admins()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var admin = await api.ClientForAsync(Seeded.Admin);

        var settings = await (await landlord.GetAsync("/api/v1/settings")).ReadAsync<SettingsDto>();
        settings.LegalReviewNeeded.ShouldContain("terminationNoticeDays");
        (await landlord.PutJsonAsync("/api/v1/settings", ToRequest(settings))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await (await admin.PutJsonAsync("/api/v1/settings", ToRequest(settings) with { DefaultEscalationPct = 50m }))
            .ShouldBeAsync(HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Seeded_demo_leases_exist_and_the_expiring_one_is_flagged()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var page = await (await landlord.GetAsync("/api/v1/leases?search=Mokoena")).ReadAsync<PagedResult<LeaseSummaryDto>>();
        page.Items.ShouldContain(l => l.Status == LeaseStatus.Expiring && l.UnitLabel == "Cottage A");
    }

    internal static SettingsRequest ToRequest(SettingsDto s) => new(s.ExpiryNoticeDays, s.DefaultEscalationPct,
        s.MaxEscalationPct, s.MonthToMonthOnLapse, s.TerminationNoticeDays, s.LeaseTemplateVersion, s.RentDueDay,
        s.RentGraceDays, s.ProrateRent, s.DepositRefundDays, s.SlaUrgentResponseHours, s.SlaUrgentResolutionHours,
        s.SlaHighResponseHours, s.SlaHighResolutionHours, s.SlaMediumResponseHours, s.SlaMediumResolutionHours,
        s.SlaLowResponseHours, s.SlaLowResolutionHours, s.IssueAutoCloseDays, s.RentReminderDaysBefore);
}
