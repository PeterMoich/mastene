import { Ledger, LeaseSummary } from '../../core/api.models';
import { currentLease, greeting, leaseProgress, rentSummary } from './tenant-home';

const lease = (o: Partial<LeaseSummary>): LeaseSummary => ({
  id: 'l1', leaseNumber: 'L2026-0001', version: 1, status: 'Active', isMonthToMonth: false, propertyId: 'p', propertyName: 'Marula Gardens',
  unitId: 'u', unitLabel: 'Cottage A', tenantId: 't', tenantName: 'Lerato Mokoena', startDate: '2025-12-01', endDate: '2026-11-30',
  monthlyRent: 5800, terminatesOn: null, ...o,
});

const ledger = (o: Partial<Ledger>): Ledger => ({
  leaseId: 'l1', leaseNumber: 'L2026-0001', version: 1, totalDue: 0, totalPaid: 0, balance: 0, arrears: 0, credit: 0, graceDays: 7,
  lines: [], ...o,
});

describe('tenant home helpers', () => {
  it('greets by the time of day', () => {
    expect(greeting(8)).toBe('Good morning');
    expect(greeting(12)).toBe('Good afternoon');
    expect(greeting(19)).toBe('Good evening');
  });

  it('picks the lease in force over a newer one waiting for signatures', () => {
    const picked = currentLease([
      lease({ id: 'next', status: 'PendingSignature', startDate: '2026-12-01' }),
      lease({ id: 'now', status: 'Expiring' }),
    ]);
    expect(picked?.id).toBe('now');
    expect(currentLease([])).toBeNull();
  });

  it('measures the lease term, counting the last day', () => {
    expect(leaseProgress('2025-12-01', '2026-11-30', '2026-11-05')).toEqual({ percent: 93, daysLeft: 25 });
    expect(leaseProgress('2025-12-01', '2026-11-30', '2026-11-30')).toEqual({ percent: 100, daysLeft: 0 });
    expect(leaseProgress('2025-12-01', '2026-11-30', '2025-10-01').percent).toBe(0);
  });

  it('puts arrears first, then the next rent due, then credit', () => {
    expect(rentSummary(ledger({ arrears: 5800 }), '2026-11-05')).toMatchObject({ tone: 'danger', amount: 5800, title: 'Overdue' });

    const due = ledger({
      lines: [{ period: '2026-11', amountDue: 5800, dueDate: '2026-11-07', daysCharged: 30, prorated: false, paid: 0, outstanding: 5800, state: 'Due', overdueNoticeOn: null }],
    });
    expect(rentSummary(due, '2026-11-05')).toMatchObject({ tone: 'warning', title: 'Due in 2 days', amount: 5800 });
    expect(rentSummary(due, '2026-11-07').title).toBe('Due today');

    expect(rentSummary(ledger({ credit: 200 }), '2026-11-05')).toMatchObject({ tone: 'success', title: 'In credit' });
    expect(rentSummary(ledger({}), '2026-11-05').title).toBe('All paid up');
  });
});
