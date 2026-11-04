import { addDaysIso, escalatedRent, leaseStatusLabel } from './lease-shared';

describe('lease helpers', () => {
  it('escalates rent the same way as the API (nearest rand)', () => {
    expect(escalatedRent(6500, 8)).toBe(7020);
    expect(escalatedRent(6999, 5.5)).toBe(7384);
    expect(escalatedRent(5800, 0)).toBe(5800);
  });

  it('adds days across month and leap-year boundaries', () => {
    expect(addDaysIso('2028-02-28', 1)).toBe('2028-02-29');
    expect(addDaysIso('2026-12-31', 1)).toBe('2027-01-01');
    expect(addDaysIso('2026-11-04', 20)).toBe('2026-11-24');
  });

  it('labels statuses in plain words', () => {
    expect(leaseStatusLabel('PendingSignature')).toBe('Waiting for signatures');
    expect(leaseStatusLabel('Active', true)).toBe('Active (month to month)');
  });
});
