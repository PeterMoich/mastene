using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Payments;
using TenantManager.Application.Settings;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;

namespace TenantManager.Application.Messaging;

/// <summary>How many reminders one run queued.</summary>
public sealed record ReminderSummary(int RentDue, int RentOverdue, int LeaseExpiry, int RenewalReady)
{
    public int Total => RentDue + RentOverdue + LeaseExpiry + RenewalReady;
}

/// <summary>
/// The daily WhatsApp reminders: rent due soon, rent overdue (BR08), lease ending (BR05) and a renewal
/// waiting for the tenant's signature (BR06). Each is queued once (a dedupe key per charge, notice or
/// lease) and only for tenants who agreed (BR16). Reminders that became due more than a week ago are
/// not sent late, so giving consent later doesn't bring a burst of old reminders.
/// </summary>
public sealed class ReminderService(IApplicationDbContext db, IClock clock, Notifier notifier, RentService rent,
    SettingsService settings, IFeatureFlags features)
{
    private const int CatchUpDays = 7;

    public async Task<ReminderSummary> RunAsync(CancellationToken ct)
    {
        if (!features.IsEnabled(Features.Reminders)) return new ReminderSummary(0, 0, 0, 0);
        var today = clock.Today();
        var running = new[] { LeaseStatus.Active, LeaseStatus.Expiring };
        var leases = await db.Leases.AsNoTracking().Where(l => running.Contains(l.Status))
            .Select(l => new { l.Id, l.TenantId, FirstName = l.Tenant.FirstName, Home = l.Unit.Label + ", " + l.Unit.Property.Name, l.EndDate })
            .ToListAsync(ct);
        var byLease = leases.ToDictionary(l => l.Id);

        // Rent: due in the next few days, or overdue recently.
        var reminderDays = await settings.GetRentReminderDaysAsync(ct);
        var unpaid = await rent.UnpaidChargesAsync(byLease.Keys.ToList(), ct);
        int due = 0, overdue = 0;
        foreach (var (charge, outstanding, state) in unpaid)
        {
            var lease = byLease[charge.LeaseId];
            if (state == ChargeState.Due && charge.DueDate >= today && charge.DueDate <= today.AddDays(reminderDays))
            {
                if (await notifier.QueueAsync(lease.TenantId, Domain.Messaging.MessageTemplates.RentDue,
                        [lease.FirstName, MessageFormat.Money(outstanding), MessageFormat.Month(charge.Period),
                            MessageFormat.Date(charge.DueDate)], $"rent-due:{charge.Id}", ct))
                    due++;
            }
            else if (state == ChargeState.Overdue && charge.OverdueNoticeOn is { } noticeOn &&
                     noticeOn >= today.AddDays(-CatchUpDays))
            {
                if (await notifier.QueueAsync(lease.TenantId, Domain.Messaging.MessageTemplates.RentOverdue,
                        [lease.FirstName, MessageFormat.Month(charge.Period), MessageFormat.Money(outstanding)],
                        $"rent-overdue:{charge.Id}", ct))
                    overdue++;
            }
        }

        // Lease ending: the notices the daily lease run recorded (90, 60, 30 days before the end).
        var since = today.AddDays(-CatchUpDays);
        var notices = await db.Leases.AsNoTracking().Where(l => running.Contains(l.Status))
            .SelectMany(l => l.ExpiryNotices.Where(n => n.RecordedOn >= since), (l, n) => new { l.Id, NoticeId = n.Id })
            .ToListAsync(ct);
        var expiry = 0;
        foreach (var n in notices)
        {
            var lease = byLease[n.Id];
            if (await notifier.QueueAsync(lease.TenantId, Domain.Messaging.MessageTemplates.LeaseExpiry,
                    [lease.FirstName, lease.Home, MessageFormat.Date(lease.EndDate)], $"lease-expiry:{n.NoticeId}", ct))
                expiry++;
        }

        // A renewal the tenant hasn't signed yet.
        var renewals = await db.Leases.AsNoTracking()
            .Where(l => l.PreviousLeaseId != null && l.Status == LeaseStatus.PendingSignature &&
                        !l.Signatures.Any(s => s.Party == LeaseParty.Tenant))
            .Select(l => new { l.Id, l.TenantId, l.Tenant.FirstName, Home = l.Unit.Label + ", " + l.Unit.Property.Name, l.StartDate })
            .ToListAsync(ct);
        var renewal = 0;
        foreach (var r in renewals)
            if (await notifier.QueueAsync(r.TenantId, Domain.Messaging.MessageTemplates.RenewalReady,
                    [r.FirstName, r.Home, MessageFormat.Date(r.StartDate)], $"renewal:{r.Id}", ct))
                renewal++;

        await db.SaveChangesAsync(ct);
        return new ReminderSummary(due, overdue, expiry, renewal);
    }
}
