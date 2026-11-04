using System.Globalization;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;

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

    public static BusinessSettings CreateDefault()
    {
        var settings = new BusinessSettings();
        settings.Apply(LeaseSettings.Defaults);
        return settings;
    }

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
