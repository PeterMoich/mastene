namespace TenantManager.Domain.Leases;

public sealed record LifecycleSummary(
    int Activated, int Renewed, int MarkedExpiring, int NoticesRecorded, int MovedToMonthToMonth, int Ended,
    int Terminated);

/// <summary>
/// The daily lease run. Idempotent: running it twice on the same day changes nothing the second time.
/// </summary>
public static class LeaseLifecycle
{
    /// <param name="leases">
    /// Every lease in <see cref="LeaseTerm.InForce"/> statuses, with Unit, Signatures, ExpiryNotices and
    /// Previous loaded. Unit occupancy is recalculated from this set, so it must be complete.
    /// </param>
    public static LifecycleSummary Run(IReadOnlyCollection<Lease> leases, DateOnly today, LeaseSettings settings)
    {
        int activated = 0, renewed = 0, expiring = 0, notices = 0, monthToMonth = 0, ended = 0, terminated = 0;

        // 1. Start fully signed leases whose start date has arrived. A renewal replaces its predecessor.
        foreach (var lease in leases
                     .Where(l => l.Status == LeaseStatus.PendingSignature && l.IsFullySigned && l.StartDate <= today)
                     .OrderBy(l => l.StartDate)
                     .ToList())
        {
            var replacesCurrent = lease.Previous?.IsCurrent == true;
            lease.Activate(today);
            activated++;
            if (replacesCurrent) renewed++;
        }

        // Leases whose renewal is already signed don't need expiry notices.
        var hasSignedRenewal = leases
            .Where(l => l.Previous is not null && l.IsFullySigned && l.Status != LeaseStatus.Terminated)
            .Select(l => l.Previous!)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        // 2. Termination, lapse at the end date (BR07), and the Expiring flag with notices (BR05).
        foreach (var lease in leases.Where(l => l.IsCurrent).ToList())
        {
            var change = lease.Advance(today, settings, hasSignedRenewal.Contains(lease));
            if (change.HasFlag(LeaseChange.MarkedExpiring)) expiring++;
            if (change.HasFlag(LeaseChange.NoticeRecorded)) notices++;
            if (change.HasFlag(LeaseChange.MovedToMonthToMonth)) monthToMonth++;
            if (change.HasFlag(LeaseChange.Ended)) ended++;
            if (change.HasFlag(LeaseChange.Terminated)) terminated++;
        }

        // 3. A unit is occupied exactly when one of its leases is Active or Expiring.
        foreach (var unitLeases in leases.GroupBy(l => l.Unit, ReferenceEqualityComparer.Instance))
        {
            var unit = unitLeases.First().Unit;
            if (unitLeases.Any(l => l.IsCurrent)) unit.MarkOccupied();
            else unit.MarkVacant();
        }

        return new LifecycleSummary(activated, renewed, expiring, notices, monthToMonth, ended, terminated);
    }
}
