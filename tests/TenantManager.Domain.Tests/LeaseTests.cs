using Shouldly;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Tenants;

namespace TenantManager.Domain.Tests;

public class LeaseTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid LandlordUser = Guid.NewGuid();
    private static readonly Guid TenantUser = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly LeaseSettings Settings = LeaseSettings.Defaults;

    private static (Unit Unit, Tenant Tenant) Parties()
    {
        var property = new Property(Owner, "Jacaranda Court",
            new PropertyAddress("14 Jacaranda Street", null, "Pretoria", "Gauteng", "0083"));
        var unit = property.AddUnit("Flat 1", 6500m, 6500m);
        var tenant = new Tenant(Owner, "Sipho", "Dlamini", "sipho@example.com", "0825550101");
        return (unit, tenant);
    }

    private static Lease Draft(DateOnly start, decimal rent = 6500m, decimal escalation = 8m)
    {
        var (unit, tenant) = Parties();
        return Lease.CreateDraft("L2026-0001", unit, tenant, start, rent, 6500m, escalation, Settings);
    }

    private static LeaseDocument Pdf(LeaseDocumentKind kind = LeaseDocumentKind.Unsigned) =>
        new(kind, $"leases/test/{Guid.NewGuid():N}.pdf", new string('A', 64), 1234);

    private static Lease Pending(DateOnly start, decimal rent = 6500m, decimal escalation = 8m)
    {
        var lease = Draft(start, rent, escalation);
        lease.Generate("Thandi Nkosi", "v0-test", Pdf());
        return lease;
    }

    private static Lease Active(DateOnly start, DateOnly today, decimal rent = 6500m, decimal escalation = 8m)
    {
        var lease = Pending(start, rent, escalation);
        SignBoth(lease, today);
        lease.Status.ShouldBe(LeaseStatus.Active);
        return lease;
    }

    private static bool SignBoth(Lease lease, DateOnly today)
    {
        lease.Sign(LeaseParty.Landlord, LandlordUser, "Thandi Nkosi", "10.0.0.1", Now, today);
        return lease.Sign(LeaseParty.Tenant, TenantUser, "Sipho Dlamini", "10.0.0.2", Now, today);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // ---- BR02: a lease is always 12 months from its start date ----

    [Theory]
    [InlineData("2026-03-01", "2027-02-28")]
    [InlineData("2027-03-01", "2028-02-29")] // the term includes the leap day
    [InlineData("2028-02-29", "2029-02-28")] // anniversary of 29 Feb is 1 Mar
    [InlineData("2026-01-15", "2027-01-14")]
    [InlineData("2026-01-31", "2027-01-30")]
    [InlineData("2026-12-01", "2027-11-30")]
    public void BR02_Lease_ends_the_day_before_its_first_anniversary(string start, string expectedEnd)
    {
        var lease = Draft(DateOnly.Parse(start));
        lease.EndDate.ShouldBe(DateOnly.Parse(expectedEnd));
        LeaseTerm.EndDateFor(DateOnly.Parse(start)).ShouldBe(DateOnly.Parse(expectedEnd));
    }

    [Fact]
    public void BR02_Changing_a_drafts_start_date_moves_its_end_date()
    {
        var (unit, tenant) = Parties();
        var lease = Lease.CreateDraft("L2026-0001", unit, tenant, D(2026, 3, 1), 6500m, 6500m, 0m, Settings);

        lease.UpdateDraft(unit, tenant, D(2026, 6, 1), 6500m, 6500m, 0m, Settings);

        lease.EndDate.ShouldBe(D(2027, 5, 31));
    }

    [Fact]
    public void Unit_and_tenant_must_belong_to_the_same_landlord()
    {
        var (unit, _) = Parties();
        var otherLandlordsTenant = new Tenant(Guid.NewGuid(), "A", "B", "a@example.com", "0825550111");

        Should.Throw<DomainRuleException>(() =>
                Lease.CreateDraft("L1", unit, otherLandlordsTenant, D(2026, 3, 1), 6500m, 0m, 0m, Settings))
            .Code.ShouldBe("lease.owner_mismatch");
    }

    [Fact]
    public void Escalation_above_the_configured_maximum_is_rejected()
    {
        Should.Throw<DomainRuleException>(() => Draft(D(2026, 3, 1), escalation: 10.5m))
            .Code.ShouldBe("escalationPct.invalid");
    }

    // ---- BR04: signed lease versions are immutable ----

    [Fact]
    public void BR04_A_generated_lease_cannot_be_edited()
    {
        var (unit, tenant) = Parties();
        var lease = Pending(D(2026, 3, 1));

        var ex = Should.Throw<DomainRuleException>(() =>
            lease.UpdateDraft(unit, tenant, D(2026, 4, 1), 7000m, 0m, 0m, Settings));
        ex.Code.ShouldBe("lease.not_draft");
        ex.Kind.ShouldBe(RuleKind.Conflict);
        lease.StartDate.ShouldBe(D(2026, 3, 1));
    }

    [Fact]
    public void BR04_A_lease_can_only_be_generated_once()
    {
        var lease = Pending(D(2026, 3, 1));
        Should.Throw<DomainRuleException>(() => lease.Generate("Thandi", "v0-test", Pdf())).Code.ShouldBe("lease.not_draft");
    }

    [Fact]
    public void BR04_A_renewal_draft_cannot_be_edited_its_terms_come_from_the_current_lease()
    {
        var today = D(2026, 11, 4);
        var renewal = Active(D(2026, 3, 1), today).CreateRenewal(8m, null, today, Settings);
        var (unit, tenant) = Parties();

        Should.Throw<DomainRuleException>(() =>
                renewal.UpdateDraft(unit, tenant, D(2027, 3, 1), 1m, 0m, 0m, Settings))
            .Code.ShouldBe("lease.renewal_not_editable");
    }

    // ---- Signing (BR06: both parties) ----

    [Fact]
    public void Drafts_cannot_be_signed()
    {
        Should.Throw<DomainRuleException>(() =>
                Draft(D(2026, 3, 1)).Sign(LeaseParty.Landlord, LandlordUser, "Thandi", null, Now, D(2026, 3, 1)))
            .Code.ShouldBe("lease.not_awaiting_signature");
    }

    [Fact]
    public void Each_party_signs_once()
    {
        var lease = Pending(D(2026, 3, 1));
        lease.Sign(LeaseParty.Landlord, LandlordUser, "Thandi", null, Now, D(2026, 2, 1));

        Should.Throw<DomainRuleException>(() =>
                lease.Sign(LeaseParty.Landlord, LandlordUser, "Thandi", null, Now, D(2026, 2, 1)))
            .Code.ShouldBe("lease.already_signed");
    }

    [Fact]
    public void One_signature_is_not_enough_to_start_a_lease()
    {
        var lease = Pending(D(2026, 3, 1));
        lease.Sign(LeaseParty.Tenant, TenantUser, "Sipho", null, Now, D(2026, 3, 5)).ShouldBeFalse();

        lease.Status.ShouldBe(LeaseStatus.PendingSignature);
        lease.Unit.Status.ShouldBe(UnitStatus.Vacant);
    }

    [Fact]
    public void Second_signature_starts_a_lease_whose_start_date_has_come_and_occupies_the_unit()
    {
        var lease = Pending(D(2026, 3, 1));

        SignBoth(lease, today: D(2026, 3, 1)).ShouldBeTrue();

        lease.Status.ShouldBe(LeaseStatus.Active);
        lease.ActivatedOn.ShouldBe(D(2026, 3, 1));
        lease.Unit.Status.ShouldBe(UnitStatus.Occupied);
    }

    [Fact]
    public void A_fully_signed_lease_with_a_future_start_waits_for_the_daily_run()
    {
        var lease = Pending(D(2026, 3, 1));
        SignBoth(lease, today: D(2026, 2, 20));
        lease.Status.ShouldBe(LeaseStatus.PendingSignature);

        LeaseLifecycle.Run([lease], D(2026, 2, 28), Settings).Activated.ShouldBe(0);
        LeaseLifecycle.Run([lease], D(2026, 3, 1), Settings).Activated.ShouldBe(1);

        lease.Status.ShouldBe(LeaseStatus.Active);
        lease.Unit.Status.ShouldBe(UnitStatus.Occupied);
    }

    // ---- BR05: Expiring at 90 days, notices at 90, 60 and 30 ----

    [Fact]
    public void BR05_Lease_is_flagged_Expiring_exactly_90_days_before_the_end()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1)); // ends 28 Feb 2027
        var dayBefore = lease.EndDate.AddDays(-91);

        LeaseLifecycle.Run([lease], dayBefore, Settings);
        lease.Status.ShouldBe(LeaseStatus.Active);

        LeaseLifecycle.Run([lease], dayBefore.AddDays(1), Settings).MarkedExpiring.ShouldBe(1);
        lease.Status.ShouldBe(LeaseStatus.Expiring);
        lease.ExpiryNotices.Select(n => n.DaysBefore).ShouldBe([90]);
    }

    [Fact]
    public void BR05_Notices_are_recorded_once_each_at_90_60_and_30_days()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        var end = lease.EndDate;

        foreach (var daysLeft in new[] { 95, 90, 89, 60, 60, 45, 30, 10, 1 })
            LeaseLifecycle.Run([lease], end.AddDays(-daysLeft), Settings);

        lease.ExpiryNotices.Select(n => n.DaysBefore).ShouldBe([90, 60, 30]);
        lease.ExpiryNotices.Select(n => n.RecordedOn).ShouldBe([end.AddDays(-90), end.AddDays(-60), end.AddDays(-30)]);
    }

    [Fact]
    public void BR05_A_lease_that_starts_late_gets_only_the_tightest_notice_not_all_three()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));

        LeaseLifecycle.Run([lease], lease.EndDate.AddDays(-20), Settings);

        lease.ExpiryNotices.Select(n => n.DaysBefore).ShouldBe([30]);
    }

    [Fact]
    public void BR05_No_notices_once_a_renewal_is_signed()
    {
        var today = D(2026, 11, 4);
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        var renewal = lease.CreateRenewal(8m, null, today, Settings);
        renewal.Generate("Thandi", "v0-test", Pdf());
        SignBoth(renewal, today);

        LeaseLifecycle.Run([lease, renewal], lease.EndDate.AddDays(-30), Settings).NoticesRecorded.ShouldBe(0);
    }

    [Fact]
    public void The_daily_run_is_idempotent()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        var today = lease.EndDate.AddDays(-30);

        LeaseLifecycle.Run([lease], today, Settings);
        var second = LeaseLifecycle.Run([lease], today, Settings);

        second.ShouldBe(new LifecycleSummary(0, 0, 0, 0, 0, 0, 0));
    }

    // ---- BR06: renewal with escalation, both parties confirm ----

    [Theory]
    [InlineData(6500, 8, 7020)]
    [InlineData(6999, 5.5, 7384)] // 7383.945 rounds to the nearest rand
    [InlineData(5800, 0, 5800)]
    [InlineData(7250, 10, 7975)]
    public void BR06_Renewal_rent_is_escalated_and_rounded_to_the_nearest_rand(decimal rent, decimal pct, decimal expected)
    {
        var today = D(2026, 11, 4);
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1), rent);

        var renewal = lease.CreateRenewal(pct, null, today, Settings);

        renewal.MonthlyRent.ShouldBe(expected);
        LeaseTerm.EscalatedRent(rent, pct).ShouldBe(expected);
    }

    [Fact]
    public void BR06_Renewal_is_the_next_version_starting_the_day_after_the_lease_ends()
    {
        var today = D(2026, 11, 4);
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));

        var renewal = lease.CreateRenewal(8m, null, today, Settings);

        renewal.Status.ShouldBe(LeaseStatus.Draft);
        renewal.LeaseNumber.ShouldBe(lease.LeaseNumber);
        renewal.Version.ShouldBe(2);
        renewal.Previous.ShouldBeSameAs(lease);
        renewal.StartDate.ShouldBe(D(2027, 3, 1));
        renewal.EndDate.ShouldBe(D(2028, 2, 29));
        renewal.DepositAmount.ShouldBe(lease.DepositAmount);
    }

    [Fact]
    public void BR06_A_renewal_cannot_choose_a_different_start_unless_the_lease_is_month_to_month()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        Should.Throw<DomainRuleException>(() => lease.CreateRenewal(8m, D(2027, 1, 1), D(2026, 11, 4), Settings))
            .Code.ShouldBe("startDate.invalid");
    }

    [Fact]
    public void BR06_Only_active_or_expiring_leases_can_be_renewed()
    {
        Should.Throw<DomainRuleException>(() => Pending(D(2026, 3, 1)).CreateRenewal(8m, null, D(2026, 3, 1), Settings))
            .Code.ShouldBe("lease.not_renewable");
    }

    [Fact]
    public void BR06_A_signed_renewal_replaces_the_old_lease_on_its_start_date_and_the_unit_stays_occupied()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        var renewal = lease.CreateRenewal(8m, null, D(2026, 12, 1), Settings);
        renewal.Generate("Thandi", "v0-test", Pdf());
        SignBoth(renewal, D(2026, 12, 2)).ShouldBeTrue();
        renewal.Status.ShouldBe(LeaseStatus.PendingSignature); // starts 1 Mar 2027

        LeaseLifecycle.Run([lease, renewal], D(2027, 2, 28), Settings).Renewed.ShouldBe(0);
        var summary = LeaseLifecycle.Run([lease, renewal], D(2027, 3, 1), Settings);

        summary.Renewed.ShouldBe(1);
        lease.Status.ShouldBe(LeaseStatus.Renewed);
        renewal.Status.ShouldBe(LeaseStatus.Active);
        renewal.Unit.Status.ShouldBe(UnitStatus.Occupied);
    }

    [Fact]
    public void BR06_An_unsigned_renewal_does_not_replace_the_lease()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        var renewal = lease.CreateRenewal(8m, null, D(2026, 12, 1), Settings);
        renewal.Generate("Thandi", "v0-test", Pdf());
        renewal.Sign(LeaseParty.Landlord, LandlordUser, "Thandi", null, Now, D(2026, 12, 2)); // tenant hasn't signed

        LeaseLifecycle.Run([lease, renewal], D(2027, 3, 1), Settings);

        lease.Status.ShouldBe(LeaseStatus.Active);
        lease.IsMonthToMonth.ShouldBeTrue();
        renewal.Status.ShouldBe(LeaseStatus.PendingSignature);
    }

    // ---- BR07: no action at the end → month-to-month or ended, per setting ----

    [Fact]
    public void BR07_Lapsed_lease_continues_month_to_month_when_the_setting_is_on()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));

        LeaseLifecycle.Run([lease], D(2027, 2, 28), Settings).MovedToMonthToMonth.ShouldBe(0); // last day
        LeaseLifecycle.Run([lease], D(2027, 3, 1), Settings).MovedToMonthToMonth.ShouldBe(1);

        lease.Status.ShouldBe(LeaseStatus.Active);
        lease.IsMonthToMonth.ShouldBeTrue();
        lease.EndDate.ShouldBe(D(2027, 2, 28)); // the signed term is kept as it was
        lease.Unit.Status.ShouldBe(UnitStatus.Occupied);
    }

    [Fact]
    public void BR07_Lapsed_lease_ends_and_frees_the_unit_when_the_setting_is_off()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));

        var summary = LeaseLifecycle.Run([lease], D(2027, 3, 1), Settings with { MonthToMonthOnLapse = false });

        summary.Ended.ShouldBe(1);
        lease.Status.ShouldBe(LeaseStatus.Ended);
        lease.Unit.Status.ShouldBe(UnitStatus.Vacant);
    }

    [Fact]
    public void BR07_A_month_to_month_lease_can_be_renewed_from_a_chosen_date()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        LeaseLifecycle.Run([lease], D(2027, 3, 5), Settings);

        Should.Throw<DomainRuleException>(() => lease.CreateRenewal(5m, D(2027, 3, 1), D(2027, 3, 5), Settings))
            .Code.ShouldBe("startDate.in_past");
        var renewal = lease.CreateRenewal(5m, D(2027, 4, 1), D(2027, 3, 5), Settings);
        renewal.StartDate.ShouldBe(D(2027, 4, 1));
        renewal.EndDate.ShouldBe(D(2028, 3, 31));
    }

    // ---- Termination (notice period is a setting: LEGAL REVIEW NEEDED) ----

    [Fact]
    public void Terminating_with_less_than_the_notice_period_is_rejected()
    {
        var today = D(2026, 6, 1);
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));

        Should.Throw<DomainRuleException>(() => lease.Terminate("Moving out", today.AddDays(19), today, Settings))
            .Code.ShouldBe("lease.notice_too_short");
    }

    [Fact]
    public void Scheduled_termination_ends_the_lease_after_its_last_day_and_frees_the_unit()
    {
        var today = D(2026, 6, 1);
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        lease.Terminate("Moving out", today.AddDays(20), today, Settings);
        lease.Status.ShouldBe(LeaseStatus.Active);
        lease.TerminatesOn.ShouldBe(today.AddDays(20));

        LeaseLifecycle.Run([lease], today.AddDays(20), Settings).Terminated.ShouldBe(0); // still in force on its last day
        LeaseLifecycle.Run([lease], today.AddDays(21), Settings).Terminated.ShouldBe(1);

        lease.Status.ShouldBe(LeaseStatus.Terminated);
        lease.Unit.Status.ShouldBe(UnitStatus.Vacant);
    }

    [Fact]
    public void A_lease_waiting_for_signatures_is_cancelled_at_once()
    {
        var lease = Pending(D(2026, 3, 1));
        lease.Terminate("Tenant changed their mind", D(2026, 2, 1), D(2026, 2, 1), Settings);

        lease.Status.ShouldBe(LeaseStatus.Terminated);
        lease.TerminationReason.ShouldBe("Tenant changed their mind");
    }

    [Fact]
    public void A_termination_after_the_end_date_is_rejected()
    {
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        Should.Throw<DomainRuleException>(() => lease.Terminate("x", D(2027, 6, 1), D(2026, 6, 1), Settings))
            .Code.ShouldBe("lease.after_end");
    }

    [Fact]
    public void A_lease_being_terminated_cannot_be_renewed()
    {
        var today = D(2026, 6, 1);
        var lease = Active(D(2026, 3, 1), D(2026, 3, 1));
        lease.Terminate("Moving out", today.AddDays(30), today, Settings);

        Should.Throw<DomainRuleException>(() => lease.CreateRenewal(8m, null, today, Settings))
            .Code.ShouldBe("lease.termination_scheduled");
    }

    // ---- BR03 building blocks (the database checks are in the integration tests) ----

    [Theory]
    [InlineData("2026-01-01", "2026-12-31", "2026-12-31", "2027-12-30", true)]  // touch on one day
    [InlineData("2026-01-01", "2026-12-31", "2027-01-01", "2027-12-31", false)] // back to back
    [InlineData("2026-03-01", "2026-04-01", "2026-01-01", "2026-12-31", true)]  // inside
    public void BR03_Overlap_is_inclusive_of_both_end_dates(string aStart, string aEnd, string bStart, string bEnd,
        bool overlaps)
    {
        LeaseTerm.Overlaps(DateOnly.Parse(aStart), DateOnly.Parse(aEnd), DateOnly.Parse(bStart), DateOnly.Parse(bEnd))
            .ShouldBe(overlaps);
    }

    [Fact]
    public void BR03_A_month_to_month_lease_holds_its_unit_until_a_termination_date_is_set()
    {
        LeaseTerm.EffectiveEnd(D(2027, 2, 28), isMonthToMonth: true, terminatesOn: null).ShouldBe(DateOnly.MaxValue);
        LeaseTerm.EffectiveEnd(D(2027, 2, 28), isMonthToMonth: true, terminatesOn: D(2027, 5, 31)).ShouldBe(D(2027, 5, 31));
        LeaseTerm.EffectiveEnd(D(2027, 2, 28), isMonthToMonth: false, terminatesOn: null).ShouldBe(D(2027, 2, 28));
    }
}
