using System.Globalization;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;

namespace TenantManager.Domain.Payments;

/// <summary>A rent month, e.g. 2026-03. Stored and sent as "yyyy-MM".</summary>
public readonly record struct BillingPeriod(int Year, int Month) : IComparable<BillingPeriod>
{
    public static BillingPeriod Of(DateOnly date) => new(date.Year, date.Month);

    public static BillingPeriod Parse(string value)
    {
        if (DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var first))
            return Of(first);
        throw new DomainRuleException("period.invalid", "Period must look like 2026-03.");
    }

    public DateOnly FirstDay => new(Year, Month, 1);
    public int DaysInMonth => DateTime.DaysInMonth(Year, Month);
    public DateOnly LastDay => new(Year, Month, DaysInMonth);
    public BillingPeriod Next() => Of(FirstDay.AddMonths(1));
    public int CompareTo(BillingPeriod other) => FirstDay.CompareTo(other.FirstDay);
    public override string ToString() => $"{Year:D4}-{Month:D2}";

    public static bool operator <(BillingPeriod a, BillingPeriod b) => a.CompareTo(b) < 0;
    public static bool operator >(BillingPeriod a, BillingPeriod b) => a.CompareTo(b) > 0;
    public static bool operator <=(BillingPeriod a, BillingPeriod b) => a.CompareTo(b) <= 0;
    public static bool operator >=(BillingPeriod a, BillingPeriod b) => a.CompareTo(b) >= 0;
}

/// <summary>Rent values from settings (BR08). Proration is a commercial choice, so it is a setting too.</summary>
public sealed record RentSettings(int DueDay, int GraceDays, bool ProrateFirstAndLastMonth, int DepositRefundDays)
{
    public static RentSettings Defaults { get; } = new(DueDay: 1, GraceDays: 3, ProrateFirstAndLastMonth: true,
        DepositRefundDays: 14);
}

/// <summary>One month's rent owed under one lease version. Raised on the first of the month (or on activation).</summary>
public sealed class RentCharge : Entity, IAuditable
{
    private RentCharge() { } // EF Core

    public RentCharge(Guid leaseId, BillingPeriod period, decimal amountDue, DateOnly dueDate, int daysCharged)
    {
        LeaseId = leaseId;
        Period = period.ToString();
        AmountDue = amountDue;
        DueDate = dueDate;
        DaysCharged = daysCharged;
    }

    public Guid LeaseId { get; private set; }

    /// <summary>"yyyy-MM".</summary>
    public string Period { get; private set; } = string.Empty;

    public decimal AmountDue { get; private set; }
    public DateOnly DueDate { get; private set; }

    /// <summary>Days of the month the lease covers; less than the month length when prorated.</summary>
    public int DaysCharged { get; private set; }

    /// <summary>BR08: set once, when the charge first becomes overdue. Phase 5 sends the reminder.</summary>
    public DateOnly? OverdueNoticeOn { get; private set; }

    public BillingPeriod BillingPeriod => BillingPeriod.Parse(Period);

    /// <returns>True if this call recorded the notice (it is only ever recorded once).</returns>
    public bool RecordOverdueNotice(DateOnly today)
    {
        if (OverdueNoticeOn is not null) return false;
        OverdueNoticeOn = today;
        return true;
    }
}

public sealed record RentChargeSpec(BillingPeriod Period, decimal AmountDue, DateOnly DueDate, int DaysCharged);

/// <summary>Works out which monthly charges a lease should have by today.</summary>
public static class RentSchedule
{
    /// <summary>
    /// Every month from the lease's start to today (and not past the lease's last day) gets one charge.
    /// A partial first or last month is prorated by days when the setting is on (rounded to the cent).
    /// The due date is the configured day of the month, or the start date if the lease starts later
    /// in its first month. Months already charged are skipped, so this is safe to run daily.
    /// </summary>
    public static IReadOnlyList<RentChargeSpec> Missing(Lease lease, DateOnly today, RentSettings settings,
        IReadOnlySet<string> alreadyCharged)
    {
        if (!lease.IsCurrent || lease.StartDate > today) return [];

        var lastDay = lease.EffectiveEndDate;
        var specs = new List<RentChargeSpec>();
        for (var period = BillingPeriod.Of(lease.StartDate);
             period.FirstDay <= today && period.FirstDay <= lastDay;
             period = period.Next())
        {
            if (alreadyCharged.Contains(period.ToString())) continue;

            var from = lease.StartDate > period.FirstDay ? lease.StartDate : period.FirstDay;
            var to = lastDay < period.LastDay ? lastDay : period.LastDay;
            var days = to.DayNumber - from.DayNumber + 1;
            var amount = days == period.DaysInMonth || !settings.ProrateFirstAndLastMonth
                ? lease.MonthlyRent
                : decimal.Round(lease.MonthlyRent * days / period.DaysInMonth, 2, MidpointRounding.AwayFromZero);

            var dueDay = new DateOnly(period.Year, period.Month, Math.Min(settings.DueDay, period.DaysInMonth));
            var due = from > dueDay ? from : dueDay;
            specs.Add(new RentChargeSpec(period, amount, due, days));
        }
        return specs;
    }
}

public enum ChargeState
{
    /// <summary>Fully covered by verified payments.</summary>
    Paid,

    /// <summary>Not fully paid yet, but the due date plus grace period hasn't passed.</summary>
    Due,

    /// <summary>Not fully paid and past the due date plus grace period (counts as arrears).</summary>
    Overdue,
}

public sealed record LedgerChargeInput(Guid Id, string Period, decimal AmountDue, DateOnly DueDate);

/// <param name="Period">The month the tenant said this payment is for; paid first when given.</param>
public sealed record LedgerPaymentInput(Guid Id, decimal Amount, string? Period, DateTimeOffset VerifiedAt);

public sealed record LedgerLine(
    Guid ChargeId, string Period, decimal AmountDue, DateOnly DueDate, decimal Paid, decimal Outstanding,
    ChargeState State);

public sealed record Allocation(Guid PaymentId, Guid ChargeId, string Period, decimal Amount);

/// <param name="TotalDue">All rent charged so far.</param>
/// <param name="TotalPaid">All verified payments.</param>
/// <param name="Balance">TotalDue − TotalPaid. Negative means the tenant is in credit.</param>
/// <param name="Arrears">The unpaid part of overdue charges (BR09).</param>
public sealed record LedgerResult(
    IReadOnlyList<LedgerLine> Lines, IReadOnlyList<Allocation> Allocations,
    decimal TotalDue, decimal TotalPaid, decimal Balance, decimal Arrears, decimal Credit);

/// <summary>
/// BR09: works the ledger out from charges and verified payments. Nothing is stored except the inputs,
/// so the ledger can never disagree with them (ADR-0014). Each payment, in the order it was verified,
/// first pays the month it was marked for, then the oldest unpaid months. Anything left over is credit
/// and pays later months as they are charged.
/// </summary>
public static class RentLedger
{
    public static LedgerResult Compute(IEnumerable<LedgerChargeInput> charges, IEnumerable<LedgerPaymentInput> payments,
        DateOnly today, int graceDays)
    {
        var ordered = charges.OrderBy(c => c.Period, StringComparer.Ordinal).ToList();
        var outstanding = ordered.ToDictionary(c => c.Id, c => c.AmountDue);
        var allocations = new List<Allocation>();
        var credit = 0m;

        foreach (var payment in payments.OrderBy(p => p.VerifiedAt).ThenBy(p => p.Id))
        {
            var remaining = payment.Amount;
            var preferred = payment.Period is null ? null : ordered.FirstOrDefault(c => c.Period == payment.Period);
            var targets = preferred is null ? ordered : ordered.Where(c => c.Id != preferred.Id).Prepend(preferred);

            foreach (var charge in targets)
            {
                if (remaining <= 0) break;
                var owed = outstanding[charge.Id];
                if (owed <= 0) continue;
                var paid = Math.Min(owed, remaining);
                outstanding[charge.Id] = owed - paid;
                remaining -= paid;
                allocations.Add(new Allocation(payment.Id, charge.Id, charge.Period, paid));
            }
            credit += remaining;
        }

        var lines = ordered.Select(c =>
        {
            var left = outstanding[c.Id];
            var state = left <= 0 ? ChargeState.Paid
                : today > c.DueDate.AddDays(graceDays) ? ChargeState.Overdue
                : ChargeState.Due;
            return new LedgerLine(c.Id, c.Period, c.AmountDue, c.DueDate, c.AmountDue - left, left, state);
        }).ToList();

        var totalDue = ordered.Sum(c => c.AmountDue);
        var totalPaid = allocations.Sum(a => a.Amount) + credit;
        return new LedgerResult(lines, allocations, totalDue, totalPaid, totalDue - totalPaid,
            lines.Where(l => l.State == ChargeState.Overdue).Sum(l => l.Outstanding), credit);
    }
}
