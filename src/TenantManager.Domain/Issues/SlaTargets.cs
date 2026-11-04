using TenantManager.Domain.Common;

namespace TenantManager.Domain.Issues;

/// <summary>Hours to acknowledge an issue and hours to resolve it, counted from when it was opened.</summary>
public sealed record SlaTarget(int ResponseHours, int ResolutionHours);

/// <summary>
/// BR15: target response and resolution times per priority. Calendar hours, not business hours: a
/// burst geyser at 22:00 on a Saturday counts from 22:00. The defaults are suggestions for a small
/// residential landlord, not legal requirements; whether some repairs have a legal deadline is a
/// question for legal review.
/// </summary>
public sealed record SlaTargets(SlaTarget Urgent, SlaTarget High, SlaTarget Medium, SlaTarget Low, int AutoCloseDays)
{
    public const int MaxHours = 24 * 90;

    public static SlaTargets Defaults { get; } = new(
        Urgent: new(4, 24),
        High: new(24, 72),
        Medium: new(48, 7 * 24),
        Low: new(72, 14 * 24),
        AutoCloseDays: 7);

    public SlaTarget For(IssuePriority priority) => priority switch
    {
        IssuePriority.Urgent => Urgent,
        IssuePriority.High => High,
        IssuePriority.Medium => Medium,
        _ => Low,
    };

    /// <summary>Each target is 1 to 2160 hours, response no later than resolution, and more urgent never slower.</summary>
    public void Validate()
    {
        var ordered = new[] { (IssuePriority.Urgent, Urgent), (IssuePriority.High, High), (IssuePriority.Medium, Medium),
            (IssuePriority.Low, Low) };
        foreach (var (priority, t) in ordered)
        {
            var name = priority.ToString().ToLowerInvariant();
            if (t.ResponseHours is < 1 or > MaxHours || t.ResolutionHours is < 1 or > MaxHours)
                throw new DomainRuleException($"sla.{name}.invalid", $"{priority} targets must be between 1 and {MaxHours} hours.");
            if (t.ResponseHours > t.ResolutionHours)
                throw new DomainRuleException($"sla.{name}.invalid",
                    $"{priority}: the response target can't be later than the resolution target.");
        }
        for (var i = 1; i < ordered.Length; i++)
        {
            var (morePriority, more) = ordered[i - 1];
            var (lessPriority, less) = ordered[i];
            if (more.ResponseHours > less.ResponseHours || more.ResolutionHours > less.ResolutionHours)
                throw new DomainRuleException("sla.order_invalid",
                    $"{morePriority} targets can't be slower than {lessPriority} targets.");
        }
        if (AutoCloseDays is < 1 or > 60)
            throw new DomainRuleException("issueAutoCloseDays.invalid", "Auto-close must be between 1 and 60 days.");
    }
}
