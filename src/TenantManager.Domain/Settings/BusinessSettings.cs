using System.Globalization;
using TenantManager.Domain.Common;
using TenantManager.Domain.Issues;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;

namespace TenantManager.Domain.Settings;

/// <summary>
/// The environment's settings, one row (single landlord per environment, ADR-0007). Edited by an
/// Admin. Values flagged LEGAL REVIEW NEEDED are placeholders, not legal advice (ADR-0011).
/// </summary>
public sealed class BusinessSettings : Entity, IAuditable
{
    private BusinessSettings() { } // EF Core

    /// <summary>Comma-separated days before the end date, e.g. "90,60,30".</summary>
    public string ExpiryNoticeDays { get; private set; } = string.Empty;

    public decimal DefaultEscalationPct { get; private set; }

    /// <summary>LEGAL REVIEW NEEDED.</summary>
    public decimal MaxEscalationPct { get; private set; }

    /// <summary>LEGAL REVIEW NEEDED.</summary>
    public bool MonthToMonthOnLapse { get; private set; }

    /// <summary>LEGAL REVIEW NEEDED. Calendar days.</summary>
    public int TerminationNoticeDays { get; private set; }

    /// <summary>LEGAL REVIEW NEEDED.</summary>
    public string LeaseTemplateVersion { get; private set; } = string.Empty;

    /// <summary>Day of the month rent is due (1 to 28, so every month has it).</summary>
    public int RentDueDay { get; private set; }

    /// <summary>Days after the due day before unpaid rent is overdue.</summary>
    public int RentGraceDays { get; private set; }

    /// <summary>Charge partial first and last months by days. A commercial choice.</summary>
    public bool ProrateRent { get; private set; }

    /// <summary>LEGAL REVIEW NEEDED. Days after the lease ends by which the deposit should be refunded.</summary>
    public int DepositRefundDays { get; private set; }

    /// <summary>BR15 targets, in hours from when the issue was opened. Suggested values, not legal ones.</summary>
    public int SlaUrgentResponseHours { get; private set; }
    public int SlaUrgentResolutionHours { get; private set; }
    public int SlaHighResponseHours { get; private set; }
    public int SlaHighResolutionHours { get; private set; }
    public int SlaMediumResponseHours { get; private set; }
    public int SlaMediumResolutionHours { get; private set; }
    public int SlaLowResponseHours { get; private set; }
    public int SlaLowResolutionHours { get; private set; }

    /// <summary>Days after an issue is resolved before it closes by itself if the tenant doesn't reply.</summary>
    public int IssueAutoCloseDays { get; private set; }

    public static BusinessSettings CreateDefault()
    {
        var settings = new BusinessSettings();
        settings.Apply(LeaseSettings.Defaults);
        settings.ApplyRent(RentSettings.Defaults);
        settings.ApplySla(SlaTargets.Defaults);
        return settings;
    }

    public void ApplySla(SlaTargets value)
    {
        value.Validate();
        (SlaUrgentResponseHours, SlaUrgentResolutionHours) = (value.Urgent.ResponseHours, value.Urgent.ResolutionHours);
        (SlaHighResponseHours, SlaHighResolutionHours) = (value.High.ResponseHours, value.High.ResolutionHours);
        (SlaMediumResponseHours, SlaMediumResolutionHours) = (value.Medium.ResponseHours, value.Medium.ResolutionHours);
        (SlaLowResponseHours, SlaLowResolutionHours) = (value.Low.ResponseHours, value.Low.ResolutionHours);
        IssueAutoCloseDays = value.AutoCloseDays;
    }

    public SlaTargets ToSlaTargets() => new(
        new SlaTarget(SlaUrgentResponseHours, SlaUrgentResolutionHours),
        new SlaTarget(SlaHighResponseHours, SlaHighResolutionHours),
        new SlaTarget(SlaMediumResponseHours, SlaMediumResolutionHours),
        new SlaTarget(SlaLowResponseHours, SlaLowResolutionHours),
        IssueAutoCloseDays);

    public void ApplyRent(RentSettings value)
    {
        if (value.DueDay is < 1 or > 28)
            throw new DomainRuleException("rentDueDay.invalid", "Rent due day must be between 1 and 28.");
        if (value.GraceDays is < 0 or > 31)
            throw new DomainRuleException("rentGraceDays.invalid", "Grace period must be between 0 and 31 days.");
        if (value.DepositRefundDays is < 0 or > 365)
            throw new DomainRuleException("depositRefundDays.invalid", "Refund days must be between 0 and 365.");
        RentDueDay = value.DueDay;
        RentGraceDays = value.GraceDays;
        ProrateRent = value.ProrateFirstAndLastMonth;
        DepositRefundDays = value.DepositRefundDays;
    }

    public RentSettings ToRentSettings() => new(RentDueDay, RentGraceDays, ProrateRent, DepositRefundDays);

    public void Apply(LeaseSettings value)
    {
        var days = value.ExpiryNoticeDays;
        if (days.Count is < 1 or > 5 || days.Any(d => d is < 1 or > 365) || days.Distinct().Count() != days.Count)
            throw new DomainRuleException("expiryNoticeDays.invalid",
                "Give one to five different reminder days, each between 1 and 365.");
        if (value.MaxEscalationPct is < 0 or > 50)
            throw new DomainRuleException("maxEscalationPct.invalid", "Maximum escalation must be between 0% and 50%.");
        if (value.DefaultEscalationPct < 0 || value.DefaultEscalationPct > value.MaxEscalationPct)
            throw new DomainRuleException("defaultEscalationPct.invalid",
                "Default escalation must be between 0% and the maximum.");
        if (value.TerminationNoticeDays is < 0 or > 365)
            throw new DomainRuleException("terminationNoticeDays.invalid", "Notice must be between 0 and 365 days.");

        ExpiryNoticeDays = string.Join(",", days.OrderDescending().Select(d => d.ToString(CultureInfo.InvariantCulture)));
        DefaultEscalationPct = value.DefaultEscalationPct;
        MaxEscalationPct = value.MaxEscalationPct;
        MonthToMonthOnLapse = value.MonthToMonthOnLapse;
        TerminationNoticeDays = value.TerminationNoticeDays;
        LeaseTemplateVersion = Guard.NotBlank(value.TemplateVersion, "leaseTemplateVersion", Lease.TemplateVersionMax);
    }

    public LeaseSettings ToLeaseSettings() => new(
        ExpiryNoticeDays.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => int.Parse(d, CultureInfo.InvariantCulture)).ToArray(),
        DefaultEscalationPct, MaxEscalationPct, MonthToMonthOnLapse, TerminationNoticeDays, LeaseTemplateVersion);
}
