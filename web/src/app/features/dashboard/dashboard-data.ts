import { Arrears, IssueSummary, LeaseSummary, Payment, PropertySummary } from '../../core/api.models';

const DAY = 86_400_000;
const utc = (iso: string) => Date.UTC(+iso.slice(0, 4), +iso.slice(5, 7) - 1, +iso.slice(8, 10));

/** Today as yyyy-MM-dd in the browser's time zone. */
export function localToday(now = new Date()): string {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

const inForce = (l: LeaseSummary) => l.status === 'Active' || l.status === 'Expiring';

/** Rent collected this calendar month (verified payments by paid-on date) against the rent of leases in force. */
export function rentCollection(leases: LeaseSummary[], payments: Payment[], today: string) {
  const month = today.slice(0, 7);
  const expected = leases.filter(inForce).reduce((sum, l) => sum + l.monthlyRent, 0);
  const collected = payments
    .filter((p) => p.status === 'Verified' && p.paidOn.startsWith(month))
    .reduce((sum, p) => sum + p.amount, 0);
  const percent = expected > 0 ? Math.min(100, Math.round((collected / expected) * 100)) : 0;
  return { expected, collected, percent };
}

/** Occupied units over all units, across every property. */
export function occupancy(properties: PropertySummary[]) {
  const units = properties.reduce((sum, p) => sum + p.unitCount, 0);
  const occupied = properties.reduce((sum, p) => sum + p.occupiedCount, 0);
  return { units, occupied, vacant: units - occupied, percent: units ? Math.round((occupied / units) * 100) : 0 };
}

export function arrearsTotal(arrears: Arrears[]) {
  return { total: arrears.reduce((sum, a) => sum + a.arrears, 0), tenants: new Set(arrears.map((a) => a.tenantId)).size };
}

export interface EndingLease {
  lease: LeaseSummary;
  endsOn: string;
  daysLeft: number;
}

/** Leases in force that end within the window (default 60 days), soonest first. Month-to-month leases don't end on a date. */
export function leasesEndingSoon(leases: LeaseSummary[], today: string, withinDays = 60): EndingLease[] {
  return leases
    .filter((l) => inForce(l) && (!l.isMonthToMonth || l.terminatesOn))
    .map((l) => {
      const endsOn = l.terminatesOn ?? l.endDate;
      return { lease: l, endsOn, daysLeft: Math.round((utc(endsOn) - utc(today)) / DAY) };
    })
    .filter((e) => e.daysLeft >= 0 && e.daysLeft <= withinDays)
    .sort((a, b) => a.daysLeft - b.daysLeft);
}

/** Open issues with the escalated ones first, then urgent before low, then oldest first. */
export function issuesNeedingAttention(issues: IssueSummary[]): IssueSummary[] {
  const rank = { Urgent: 0, High: 1, Medium: 2, Low: 3 } as const;
  return [...issues].sort(
    (a, b) =>
      Number(b.isEscalated) - Number(a.isEscalated) ||
      rank[a.priority] - rank[b.priority] ||
      a.openedAt.localeCompare(b.openedAt),
  );
}
