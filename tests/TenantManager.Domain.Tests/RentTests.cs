using Shouldly;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Tenants;

namespace TenantManager.Domain.Tests;

public class RentScheduleTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly RentSettings Settings = RentSettings.Defaults; // due on the 1st, 3 days' grace, prorated

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static Lease ActiveLease(DateOnly start, decimal rent = 6500m)
    {
        var property = new Property(Owner, "Jacaranda Court",
            new PropertyAddress("14 Jacaranda Street", null, "Pretoria", "Gauteng", "0083"));
        var unit = property.AddUnit("Flat 1", rent, rent);
        var tenant = new Tenant(Owner, "Sipho", "Dlamini", "sipho@example.com", "0825550101");
        var lease = Lease.CreateDraft("L2026-0001", unit, tenant, start, rent, rent, 8m, LeaseSettings.Defaults);
        lease.Generate("Thandi", "v0-test", new LeaseDocument(LeaseDocumentKind.Unsigned, "k", new string('A', 64), 1));
        lease.Sign(LeaseParty.Landlord, Guid.NewGuid(), "Thandi", null, Now, start);
        lease.Sign(LeaseParty.Tenant, Guid.NewGuid(), "Sipho", null, Now, start);
        return lease;
    }

    private static readonly HashSet<string> None = [];

    [Fact]
    public void BR08_One_charge_per_month_from_the_start_up_to_today_never_ahead()
    {
        var lease = ActiveLease(D(2026, 3, 1));

        var specs = RentSchedule.Missing(lease, D(2026, 5, 20), Settings, None);

        specs.Select(s => s.Period.ToString()).ShouldBe(["2026-03", "2026-04", "2026-05"]);
        specs.ShouldAllBe(s => s.AmountDue == 6500m);
        specs.Select(s => s.DueDate).ShouldBe([D(2026, 3, 1), D(2026, 4, 1), D(2026, 5, 1)]);
    }

    [Fact]
    public void BR08_Due_date_follows_the_configured_day()
    {
        var lease = ActiveLease(D(2026, 3, 1));
        var specs = RentSchedule.Missing(lease, D(2026, 4, 2), Settings with { DueDay = 7 }, None);
        specs.Select(s => s.DueDate).ShouldBe([D(2026, 3, 7), D(2026, 4, 7)]);
    }

    [Fact]
    public void Months_already_charged_are_skipped_so_the_daily_run_is_safe_to_repeat()
    {
        var lease = ActiveLease(D(2026, 3, 1));
        var specs = RentSchedule.Missing(lease, D(2026, 5, 20), Settings, new HashSet<string> { "2026-03", "2026-04" });
        specs.Select(s => s.Period.ToString()).ShouldBe(["2026-05"]);
    }

    [Fact]
    public void A_mid_month_start_is_prorated_and_due_on_the_start_date()
    {
        var lease = ActiveLease(D(2026, 3, 16)); // 16 of 31 days

        var first = RentSchedule.Missing(lease, D(2026, 3, 20), Settings, None).Single();

        first.DaysCharged.ShouldBe(16);
        first.AmountDue.ShouldBe(3354.84m); // 6500 × 16 / 31 = 3354.838…
        first.DueDate.ShouldBe(D(2026, 3, 16));
    }

    [Fact]
    public void Proration_can_be_switched_off()
    {
        var lease = ActiveLease(D(2026, 3, 16));
        RentSchedule.Missing(lease, D(2026, 3, 20), Settings with { ProrateFirstAndLastMonth = false }, None)
            .Single().AmountDue.ShouldBe(6500m);
    }

    [Fact]
    public void The_last_month_is_prorated_to_the_lease_end()
    {
        var lease = ActiveLease(D(2026, 3, 16)); // ends 15 Mar 2027
        var last = RentSchedule.Missing(lease, D(2027, 3, 20), Settings, None).Last();

        last.Period.ToString().ShouldBe("2027-03");
        last.DaysCharged.ShouldBe(15);
        last.AmountDue.ShouldBe(3145.16m); // 6500 × 15 / 31
    }

    [Fact]
    public void A_scheduled_termination_prorates_its_last_month()
    {
        var lease = ActiveLease(D(2026, 3, 1));
        lease.Terminate("Moving out", D(2026, 6, 10), D(2026, 5, 1), LeaseSettings.Defaults);

        var june = RentSchedule.Missing(lease, D(2026, 6, 5), Settings, None).Last();

        june.DaysCharged.ShouldBe(10);
        june.AmountDue.ShouldBe(2166.67m); // 6500 × 10 / 30
    }

    [Fact]
    public void A_lease_waiting_for_signatures_is_not_charged()
    {
        var property = new Property(Owner, "P", new PropertyAddress("1 St", null, "City", "Gauteng", "0001"));
        var unit = property.AddUnit("A", 5000m, 0m);
        var tenant = new Tenant(Owner, "A", "B", "a@example.com", "0825550111");
        var draft = Lease.CreateDraft("L1", unit, tenant, D(2026, 3, 1), 5000m, 0m, 0m, LeaseSettings.Defaults);

        RentSchedule.Missing(draft, D(2026, 6, 1), Settings, None).ShouldBeEmpty();
    }

    [Fact]
    public void A_full_month_split_between_a_lease_and_its_renewal_adds_up_to_one_months_rent()
    {
        var lease = ActiveLease(D(2026, 3, 16));
        var renewal = lease.CreateRenewal(0m, null, D(2027, 1, 10), LeaseSettings.Defaults);
        renewal.Generate("Thandi", "v0-test", new LeaseDocument(LeaseDocumentKind.Unsigned, "k2", new string('B', 64), 1));
        renewal.Sign(LeaseParty.Landlord, Guid.NewGuid(), "Thandi", null, Now, D(2027, 1, 10));
        renewal.Sign(LeaseParty.Tenant, Guid.NewGuid(), "Sipho", null, Now, D(2027, 1, 10));
        var oldMarch = RentSchedule.Missing(lease, D(2027, 3, 15), Settings, None).Single(s => s.Period.ToString() == "2027-03");
        LeaseLifecycle.Run([lease, renewal], D(2027, 3, 16), LeaseSettings.Defaults); // renewal takes over
        var newMarch = RentSchedule.Missing(renewal, D(2027, 3, 20), Settings, None).Single();

        (oldMarch.AmountDue + newMarch.AmountDue).ShouldBe(6500m, tolerance: 0.01m);
    }
}

public class RentLedgerTests
{
    private static readonly DateOnly Today = new(2026, 6, 20);
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private static LedgerChargeInput Charge(string period, decimal amount = 6500m, int dueDay = 1) =>
        new(Guid.NewGuid(), period, amount, new DateOnly(int.Parse(period[..4]), int.Parse(period[5..]), dueDay));

    private static LedgerPaymentInput Pay(decimal amount, int minutes, string? period = null) =>
        new(Guid.NewGuid(), amount, period, T0.AddMinutes(minutes));

    [Fact]
    public void BR09_Partial_payments_add_up_and_arrears_clear_when_paid_in_full()
    {
        var april = Charge("2026-04");

        var afterFirst = RentLedger.Compute([april], [Pay(3000m, 1)], Today, graceDays: 3);
        afterFirst.Lines.Single().Paid.ShouldBe(3000m);
        afterFirst.Lines.Single().Outstanding.ShouldBe(3500m);
        afterFirst.Lines.Single().State.ShouldBe(ChargeState.Overdue);
        afterFirst.Arrears.ShouldBe(3500m);
        afterFirst.Balance.ShouldBe(3500m);

        var afterSecond = RentLedger.Compute([april], [Pay(3000m, 1), Pay(3500m, 2)], Today, graceDays: 3);
        afterSecond.Lines.Single().State.ShouldBe(ChargeState.Paid);
        afterSecond.Arrears.ShouldBe(0m);
        afterSecond.Balance.ShouldBe(0m);
    }

    [Fact]
    public void BR09_Payments_without_a_month_pay_the_oldest_unpaid_month_first()
    {
        var (apr, may, jun) = (Charge("2026-04"), Charge("2026-05"), Charge("2026-06"));

        var ledger = RentLedger.Compute([jun, apr, may], [Pay(8000m, 1)], Today, graceDays: 3);

        ledger.Lines.Select(l => (l.Period, l.Paid)).ShouldBe([("2026-04", 6500m), ("2026-05", 1500m), ("2026-06", 0m)]);
        ledger.Allocations.Select(a => a.Period).ShouldBe(["2026-04", "2026-05"]);
    }

    [Fact]
    public void BR09_A_payment_marked_for_a_month_pays_that_month_first()
    {
        var (apr, may) = (Charge("2026-04"), Charge("2026-05"));

        var ledger = RentLedger.Compute([apr, may], [Pay(6500m, 1, period: "2026-05")], Today, graceDays: 3);

        ledger.Lines.Single(l => l.Period == "2026-05").State.ShouldBe(ChargeState.Paid);
        ledger.Lines.Single(l => l.Period == "2026-04").Outstanding.ShouldBe(6500m);
    }

    [Fact]
    public void Overpayment_becomes_credit_and_pays_the_next_month_when_it_is_charged()
    {
        var may = Charge("2026-05");
        var payments = new[] { Pay(10000m, 1) };

        var before = RentLedger.Compute([may], payments, Today, graceDays: 3);
        before.Credit.ShouldBe(3500m);
        before.Balance.ShouldBe(-3500m);

        var after = RentLedger.Compute([may, Charge("2026-06")], payments, Today, graceDays: 3);
        after.Credit.ShouldBe(0m);
        after.Lines.Single(l => l.Period == "2026-06").Paid.ShouldBe(3500m);
        after.Balance.ShouldBe(3000m);
    }

    [Theory]
    [InlineData(4, ChargeState.Due)]     // due 1 June + 3 days' grace = 4 June is the last day
    [InlineData(5, ChargeState.Overdue)]
    public void BR08_Rent_becomes_overdue_the_day_after_the_grace_period(int day, ChargeState expected)
    {
        var june = Charge("2026-06");
        RentLedger.Compute([june], [], new DateOnly(2026, 6, day), graceDays: 3).Lines.Single().State.ShouldBe(expected);
    }

    [Fact]
    public void Only_overdue_charges_count_as_arrears()
    {
        var ledger = RentLedger.Compute([Charge("2026-05"), Charge("2026-06", dueDay: 20)], [], Today, graceDays: 3);
        ledger.Balance.ShouldBe(13000m);
        ledger.Arrears.ShouldBe(6500m);
    }

    [Fact]
    public void BR08_The_overdue_notice_is_recorded_only_once()
    {
        var charge = new RentCharge(Guid.NewGuid(), new BillingPeriod(2026, 6), 6500m, new DateOnly(2026, 6, 1), 30);
        charge.RecordOverdueNotice(new DateOnly(2026, 6, 5)).ShouldBeTrue();
        charge.RecordOverdueNotice(new DateOnly(2026, 6, 6)).ShouldBeFalse();
        charge.OverdueNoticeOn.ShouldBe(new DateOnly(2026, 6, 5));
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("26-01")]
    [InlineData("March")]
    public void Invalid_periods_are_rejected(string value)
    {
        Should.Throw<DomainRuleException>(() => BillingPeriod.Parse(value)).Code.ShouldBe("period.invalid");
    }
}

public class ProofOfPaymentTests
{
    private static readonly DateOnly Today = new(2026, 6, 20);
    private static readonly DateTimeOffset Now = new(2026, 6, 20, 9, 0, 0, TimeSpan.Zero);

    private static ProofOfPayment Submitted(decimal amount = 6500m) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        amount, Today, new BillingPeriod(2026, 6), "RENT JUNE",
        new StoredFile("payments/x/y.pdf", "eft.pdf", "application/pdf", 1234, new string('C', 64)), Guid.NewGuid(), Today);

    [Fact]
    public void BR11_Verifying_records_who_and_when()
    {
        var p = Submitted();
        var reviewer = Guid.NewGuid();
        p.Verify(reviewer, "Thandi Nkosi", Now);

        p.Status.ShouldBe(PaymentStatus.Verified);
        p.ReviewedBy.ShouldBe(reviewer);
        p.ReviewedByName.ShouldBe("Thandi Nkosi");
        p.ReviewedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BR11_Rejecting_needs_a_reason(string reason)
    {
        var p = Submitted();
        Should.Throw<DomainRuleException>(() => p.Reject(reason, Guid.NewGuid(), "Thandi", Now)).Code.ShouldBe("reason.required");
        p.Status.ShouldBe(PaymentStatus.Submitted);
    }

    [Fact]
    public void BR11_A_reviewed_payment_cannot_be_reviewed_again()
    {
        var verified = Submitted();
        verified.Verify(Guid.NewGuid(), "Thandi", Now);
        Should.Throw<DomainRuleException>(() => verified.Reject("Wrong amount", Guid.NewGuid(), "Thandi", Now))
            .Code.ShouldBe("payment.already_reviewed");

        var rejected = Submitted();
        rejected.Reject("Wrong amount", Guid.NewGuid(), "Thandi", Now);
        Should.Throw<DomainRuleException>(() => rejected.Verify(Guid.NewGuid(), "Thandi", Now))
            .Code.ShouldBe("payment.already_reviewed");
    }

    [Fact]
    public void A_payment_date_in_the_future_is_rejected()
    {
        Should.Throw<DomainRuleException>(() => new ProofOfPayment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m,
                Today.AddDays(1), null, null, new StoredFile("k", "f.pdf", "application/pdf", 1, "H"), Guid.NewGuid(), Today))
            .Code.ShouldBe("paidOn.in_future");
    }

    [Fact]
    public void Only_a_verified_payment_gets_a_receipt()
    {
        Should.Throw<InvalidOperationException>(() => Submitted().AttachReceipt("R2026-0001", "receipts/x.pdf"));
    }
}

public class DepositTests
{
    private static readonly DateOnly Today = new(2027, 3, 10);

    private static Deposit Held(decimal amount = 6500m)
    {
        var deposit = new Deposit(Guid.NewGuid(), "L2026-0001", 6500m);
        deposit.Receive(amount, new DateOnly(2026, 3, 1), Today);
        return deposit;
    }

    [Fact]
    public void BR13_A_deposit_starts_awaiting_payment_and_is_held_once_received()
    {
        var deposit = new Deposit(Guid.NewGuid(), "L2026-0001", 6500m);
        deposit.Status.ShouldBe(DepositStatus.AwaitingPayment);

        deposit.Receive(6500m, new DateOnly(2026, 3, 1), Today);

        deposit.Status.ShouldBe(DepositStatus.Held);
        deposit.AmountReceived.ShouldBe(6500m);
        Should.Throw<DomainRuleException>(() => deposit.Receive(1m, Today, Today)).Code.ShouldBe("deposit.already_received");
    }

    [Fact]
    public void BR13_Deductions_need_a_reason_and_the_lease_to_have_ended()
    {
        var deposit = Held();
        Should.Throw<DomainRuleException>(() => deposit.AddDeduction(500m, "Broken window", leaseHasEnded: false))
            .Code.ShouldBe("deposit.lease_not_ended");
        Should.Throw<DomainRuleException>(() => deposit.AddDeduction(500m, "", leaseHasEnded: true))
            .Code.ShouldBe("reason.required");
    }

    [Fact]
    public void BR13_Deductions_can_never_exceed_what_is_held()
    {
        var deposit = Held();
        deposit.AddDeduction(4000m, "Repaint", leaseHasEnded: true);

        Should.Throw<DomainRuleException>(() => deposit.AddDeduction(2500.01m, "Carpet", leaseHasEnded: true))
            .Code.ShouldBe("deposit.deduction_too_large");
        deposit.AddDeduction(2500m, "Carpet", leaseHasEnded: true);
        deposit.Remaining.ShouldBe(0m);
    }

    [Fact]
    public void BR13_Refund_returns_what_is_left_and_sets_the_status()
    {
        var full = Held();
        full.Refund(Today, leaseHasEnded: true, Today);
        full.Status.ShouldBe(DepositStatus.Refunded);
        full.AmountRefunded.ShouldBe(6500m);

        var partial = Held();
        partial.AddDeduction(1200m, "Cleaning", leaseHasEnded: true);
        partial.Refund(Today, leaseHasEnded: true, Today);
        partial.Status.ShouldBe(DepositStatus.PartiallyRefunded);
        partial.AmountRefunded.ShouldBe(5300m);

        Should.Throw<DomainRuleException>(() => partial.AddDeduction(1m, "Late", leaseHasEnded: true))
            .Code.ShouldBe("deposit.not_held");
    }
}
