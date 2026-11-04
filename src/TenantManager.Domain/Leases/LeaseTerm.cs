using TenantManager.Domain.Common;

namespace TenantManager.Domain.Leases;

public enum LeaseStatus
{
    Draft,
    PendingSignature,
    Active,
    Expiring,
    Renewed,
    Ended,
    Terminated,
}

public enum LeaseParty
{
    Landlord,
    Tenant,
}

public enum LeaseDocumentKind
{
    /// <summary>Generated when the lease is sent for signing.</summary>
    Unsigned,

    /// <summary>Generated once both parties have signed; includes the signature record.</summary>
    Signed,
}

/// <summary>
/// The lease values that come from settings (ADR-0011). Several of them are legal choices
/// and carry placeholder defaults marked LEGAL REVIEW NEEDED.
/// </summary>
public sealed record LeaseSettings(
    IReadOnlyList<int> ExpiryNoticeDays,
    decimal DefaultEscalationPct,
    decimal MaxEscalationPct,
    bool MonthToMonthOnLapse,
    int TerminationNoticeDays,
    string TemplateVersion)
{
    public static LeaseSettings Defaults { get; } = new([90, 60, 30], 0m, 10m, true, 20, "v0-test");

    /// <summary>The first threshold (e.g. 90 days before the end), when a lease is flagged Expiring.</summary>
    public int FirstNoticeDays => ExpiryNoticeDays.Count == 0 ? 0 : ExpiryNoticeDays.Max();
}

/// <summary>Date and money rules shared by leases and renewals.</summary>
public static class LeaseTerm
{
    public const int Months = 12;

    /// <summary>Statuses in which a lease holds (or is about to hold) its unit for its dates.</summary>
    public static readonly LeaseStatus[] InForce = [LeaseStatus.PendingSignature, LeaseStatus.Active, LeaseStatus.Expiring];

    /// <summary>
    /// BR02: a lease runs 12 months, ending the day before its anniversary.
    /// 1 Mar 2026 → 28 Feb 2027; 1 Mar 2027 → 29 Feb 2028; 29 Feb 2028 → 28 Feb 2029
    /// (the anniversary of 29 Feb is taken as 1 Mar).
    /// </summary>
    public static DateOnly EndDateFor(DateOnly start)
    {
        var anniversary = start.AddMonths(Months);
        if (anniversary.Day != start.Day) anniversary = anniversary.AddDays(1); // 29 Feb clamped to 28 Feb
        return anniversary.AddDays(-1);
    }

    /// <summary>BR06: renewal rent after escalation, rounded to the nearest rand (Phase 0 assumption 7).</summary>
    public static decimal EscalatedRent(decimal rent, decimal escalationPct) =>
        decimal.Round(rent * (1 + escalationPct / 100m), 0, MidpointRounding.AwayFromZero);

    /// <summary>The last day a lease holds its unit: open-ended when month-to-month, earlier if termination is scheduled.</summary>
    public static DateOnly EffectiveEnd(DateOnly endDate, bool isMonthToMonth, DateOnly? terminatesOn)
    {
        var end = isMonthToMonth ? DateOnly.MaxValue : endDate;
        return terminatesOn is { } last && last < end ? last : end;
    }

    /// <summary>Inclusive date ranges overlap when each starts on or before the other ends.</summary>
    public static bool Overlaps(DateOnly aStart, DateOnly aEnd, DateOnly bStart, DateOnly bEnd) =>
        aStart <= bEnd && bStart <= aEnd;

    public static decimal CheckEscalation(decimal pct, LeaseSettings settings)
    {
        if (pct < 0 || pct > settings.MaxEscalationPct || decimal.Round(pct, 2) != pct)
            throw new DomainRuleException("escalationPct.invalid",
                $"Escalation must be between 0% and {settings.MaxEscalationPct:0.##}% with at most two decimals. " +
                "The maximum is a setting marked LEGAL REVIEW NEEDED.");
        return pct;
    }
}
