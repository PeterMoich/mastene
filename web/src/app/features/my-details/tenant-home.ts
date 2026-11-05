import { Ledger, LeaseSummary } from '../../core/api.models';
import { monthName } from '../../shared/download';

/** "Good morning" before 12:00, "Good afternoon" until 17:00, then "Good evening". */
export function greeting(hour: number): string {
  return hour < 12 ? 'Good morning' : hour < 17 ? 'Good afternoon' : 'Good evening';
}

/** The lease the home page is about: one in force first, then one waiting for signatures, then the newest. */
export function currentLease(leases: LeaseSummary[]): LeaseSummary | null {
  const rank = (l: LeaseSummary) =>
    l.status === 'Active' || l.status === 'Expiring' ? 0 : l.status === 'PendingSignature' ? 1 : l.status === 'Draft' ? 3 : 2;
  return [...leases].sort((a, b) => rank(a) - rank(b) || b.startDate.localeCompare(a.startDate))[0] ?? null;
}

const DAY = 86_400_000;
const utc = (iso: string) => Date.UTC(+iso.slice(0, 4), +iso.slice(5, 7) - 1, +iso.slice(8, 10));

/** How far through the lease term today is (0-100) and the days left, counting the last day. */
export function leaseProgress(startDate: string, endDate: string, today: string): { percent: number; daysLeft: number } {
  const start = utc(startDate);
  const end = utc(endDate) + DAY;
  const now = utc(today);
  const percent = Math.round(Math.min(100, Math.max(0, ((now - start) / (end - start)) * 100)));
  return { percent, daysLeft: Math.max(0, Math.round((end - now) / DAY) - 1) };
}

export interface RentSummary {
  tone: 'success' | 'warning' | 'danger' | 'neutral';
  icon: string;
  amount: number | null;
  title: string;
  detail: string;
}

/** What the tenant needs to know about rent, from the lease's ledger. */
export function rentSummary(ledger: Ledger, today: string): RentSummary {
  if (ledger.arrears > 0) {
    return {
      tone: 'danger', icon: 'error', amount: ledger.arrears, title: 'Overdue',
      detail: 'Pay as soon as you can, then upload your proof of payment.',
    };
  }
  const next = ledger.lines
    .filter((l) => l.outstanding > 0 && l.state === 'Due')
    .sort((a, b) => a.dueDate.localeCompare(b.dueDate))[0];
  if (next) {
    const days = Math.round((utc(next.dueDate) - utc(today)) / DAY);
    const when = days <= 0 ? 'today' : days === 1 ? 'tomorrow' : `in ${days} days`;
    return {
      tone: days <= 5 ? 'warning' : 'neutral', icon: 'schedule', amount: next.outstanding,
      title: `Due ${when}`, detail: `Rent for ${monthName(next.period)}. Upload your proof of payment once you've paid.`,
    };
  }
  if (ledger.credit > 0) {
    return { tone: 'success', icon: 'savings', amount: ledger.credit, title: 'In credit', detail: 'It goes towards your next rent.' };
  }
  return { tone: 'success', icon: 'check_circle', amount: null, title: 'All paid up', detail: 'Nothing is owed right now.' };
}
