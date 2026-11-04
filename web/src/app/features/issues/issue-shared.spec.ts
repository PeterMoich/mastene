import { IssueSummary } from '../../core/api.models';
import { nextTarget } from './issue-list';
import { formatDuration, targetText } from './issue-shared';

describe('formatDuration', () => {
  it('reads naturally from minutes to days', () => {
    expect(formatDuration(25 * 60000)).toBe('25 min');
    expect(formatDuration(3 * 3600000)).toBe('3 h');
    expect(formatDuration(52 * 3600000)).toBe('2 d 4 h');
    expect(formatDuration(48 * 3600000)).toBe('2 d');
  });
});

describe('targetText (BR15)', () => {
  const due = '2026-11-04T12:00:00Z';

  it('says how long is left while waiting', () => {
    expect(targetText(due, null, new Date('2026-11-04T09:00:00Z'))).toEqual({ text: '3 h left', late: false });
  });

  it('says how late it is once the target has passed, in words not only colour', () => {
    expect(targetText(due, null, new Date('2026-11-04T14:30:00Z'))).toEqual({ text: 'Overdue by 2 h', late: true });
  });

  it('says whether a finished step met its target', () => {
    expect(targetText(due, '2026-11-04T11:59:00Z')).toEqual({ text: 'Met on time', late: false });
    expect(targetText(due, '2026-11-04T13:00:00Z')).toEqual({ text: 'Met 60 min late', late: true });
  });
});

describe('nextTarget', () => {
  const base: IssueSummary = {
    id: 'i1', title: 'Tap', category: 'Plumbing', priority: 'High', status: 'Open', unitId: 'u1',
    propertyName: 'Jacaranda Court', unitLabel: 'Flat 1', tenantName: 'Sipho Dlamini', contractorName: null,
    openedAt: '2026-11-04T08:00:00Z', responseDueAt: '2026-11-05T08:00:00Z', resolutionDueAt: '2026-11-07T08:00:00Z',
    isEscalated: false, photoCount: 0,
  };
  const now = new Date('2026-11-04T20:00:00Z');

  it('is the response target until the issue is acknowledged, then the resolution target', () => {
    expect(nextTarget(base, now)?.text).toBe('Respond: 12 h left');
    expect(nextTarget({ ...base, status: 'InProgress' }, now)?.text).toBe('Resolve: 2 d 12 h left');
    expect(nextTarget({ ...base, status: 'Resolved' }, now)).toBeNull();
  });
});
