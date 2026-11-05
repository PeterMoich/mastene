import { IssueSummary, LeaseSummary, Payment, PropertySummary } from '../../core/api.models';
import { arrearsTotal, issuesNeedingAttention, leasesEndingSoon, occupancy, rentCollection } from './dashboard-data';

const lease = (o: Partial<LeaseSummary>): LeaseSummary => ({
  id: 'l', leaseNumber: 'L2026-0001', version: 1, status: 'Active', isMonthToMonth: false, propertyId: 'p', propertyName: 'Jacaranda Court',
  unitId: 'u', unitLabel: 'Flat 1', tenantId: 't', tenantName: 'Sipho Dlamini', startDate: '2026-01-01', endDate: '2026-12-31',
  monthlyRent: 6500, terminatesOn: null, ...o,
});
const payment = (o: Partial<Payment>): Payment =>
  ({ id: 'p', leaseId: 'l', status: 'Verified', amount: 6500, paidOn: '2026-11-02', tenantName: 'Sipho Dlamini', ...o }) as Payment;
const issue = (o: Partial<IssueSummary>): IssueSummary =>
  ({ id: 'i', title: 'Tap', priority: 'Medium', status: 'Open', isEscalated: false, openedAt: '2026-11-01T08:00:00Z', ...o }) as IssueSummary;

describe('landlord dashboard figures', () => {
  it('compares verified rent paid this month with the rent of leases in force', () => {
    const leases = [lease({ monthlyRent: 6500 }), lease({ monthlyRent: 5800, status: 'Expiring' }), lease({ monthlyRent: 9000, status: 'Draft' })];
    const payments = [
      payment({ amount: 6500 }),
      payment({ amount: 5800, status: 'Submitted' }), // not verified yet
      payment({ amount: 5800, paidOn: '2026-10-30' }), // last month
    ];
    expect(rentCollection(leases, payments, '2026-11-05')).toEqual({ expected: 12300, collected: 6500, percent: 53 });
    expect(rentCollection([], [], '2026-11-05').percent).toBe(0);
  });

  it('adds up occupancy across properties', () => {
    const props = [{ unitCount: 3, occupiedCount: 2 }, { unitCount: 1, occupiedCount: 1 }] as PropertySummary[];
    expect(occupancy(props)).toEqual({ units: 4, occupied: 3, vacant: 1, percent: 75 });
    expect(occupancy([]).percent).toBe(0);
  });

  it('totals arrears and counts each tenant once', () => {
    const a = [
      { tenantId: 't1', arrears: 1000 },
      { tenantId: 't1', arrears: 500 },
      { tenantId: 't2', arrears: 250 },
    ] as never[];
    expect(arrearsTotal(a)).toEqual({ total: 1750, tenants: 2 });
  });

  it('lists leases ending within 60 days, soonest first, including early terminations', () => {
    const ending = leasesEndingSoon(
      [
        lease({ id: 'later', endDate: '2027-06-30' }),
        lease({ id: 'soon', status: 'Expiring', endDate: '2026-11-30' }),
        lease({ id: 'notice', endDate: '2027-06-30', terminatesOn: '2026-11-15' }),
        lease({ id: 'm2m', isMonthToMonth: true, endDate: '2026-11-20' }),
        lease({ id: 'ended', status: 'Ended', endDate: '2026-11-10' }),
      ],
      '2026-11-05',
    );
    expect(ending.map((e) => [e.lease.id, e.daysLeft])).toEqual([
      ['notice', 10],
      ['soon', 25],
    ]);
  });

  it('puts escalated issues first, then by priority, then oldest', () => {
    const sorted = issuesNeedingAttention([
      issue({ id: 'low-old', priority: 'Low', openedAt: '2026-10-01T00:00:00Z' }),
      issue({ id: 'urgent', priority: 'Urgent' }),
      issue({ id: 'escalated-low', priority: 'Low', isEscalated: true }),
    ]);
    expect(sorted.map((i) => i.id)).toEqual(['escalated-low', 'urgent', 'low-old']);
  });
});
