import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { Issue, Role } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { activity, IssueDetail } from './issue-detail';

function issue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'i1', title: 'Geyser not heating', description: 'No hot water.', category: 'Plumbing', priority: 'High',
    status: 'Open', unitId: 'u1', propertyName: 'Marula Gardens', unitLabel: 'Cottage A', leaseId: 'l1',
    leaseNumber: 'L2026-0002', tenantId: 't1', tenantName: 'Lerato Mokoena', reportedByName: 'Lerato Mokoena',
    contractor: null, openedAt: '2026-11-03T06:00:00Z', acknowledgedAt: null, startedAt: null, resolvedAt: null,
    closedAt: null, responseDueAt: '2026-11-04T06:00:00Z', resolutionDueAt: '2026-11-06T06:00:00Z', responseMet: false,
    resolutionMet: null, isEscalated: true, responseMissedAt: '2026-11-04T06:15:00Z', resolutionMissedAt: null,
    resolutionNote: null, reopenCount: 0, autoCloseDays: 7, photos: [],
    comments: [{ id: 'c1', authorName: 'Thandi Nkosi', byTenant: false, body: 'Electrician on the way.', at: '2026-11-04T07:00:00Z' }],
    events: [
      { kind: 'Opened', byName: 'Lerato Mokoena', at: '2026-11-03T06:00:00Z', note: 'High priority' },
      { kind: 'ResponseTargetMissed', byName: 'System', at: '2026-11-04T06:15:00Z', note: null },
    ],
    allowedActions: ['acknowledge', 'start', 'changePriority', 'assignContractor', 'comment', 'addPhoto'],
    ...overrides,
  };
}

describe('activity', () => {
  it('merges history and comments in time order', () => {
    const entries = activity(issue());
    expect(entries.map((e) => e.text)).toEqual(['Logged the issue', 'Response target missed: escalated', 'Commented']);
    expect(entries[1].bySystem).toBe(true);
    expect(entries[2].detail).toBe('Electrician on the way.');
  });
});

describe('IssueDetail', () => {
  const role = signal<Role>('Landlord');
  const api = { getIssue: vi.fn(), issueStep: vi.fn(), issuePhotoUrl: vi.fn(() => '/x'), image: vi.fn() };

  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({
      imports: [IssueDetail],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: api },
        { provide: AuthService, useValue: { role } },
        { provide: MatDialog, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();
  });

  async function render(data: Issue) {
    api.getIssue.mockReturnValue(of(data));
    const fixture = TestBed.createComponent(IssueDetail);
    fixture.componentRef.setInput('id', data.id);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const buttons = (el: HTMLElement) =>
    Array.from(el.querySelectorAll('[aria-label="Issue actions"] button')).map((b) => b.textContent?.trim() ?? '');

  it('offers the landlord the next steps and says the target was missed in words', async () => {
    role.set('Landlord');
    const { el } = await render(issue());

    expect(buttons(el)).toEqual(['Acknowledge', 'construction Start work', 'engineering Assign contractor', 'Priority']);
    expect(el.textContent).toContain('Escalated');
    expect(el.textContent).toContain('Overdue by');
    expect(el.textContent).toContain('Electrician on the way.');
  });

  it('asks the tenant to confirm a resolved issue or reopen it', async () => {
    role.set('Tenant');
    const { el } = await render(
      issue({ status: 'Resolved', resolvedAt: '2026-11-04T10:00:00Z', resolutionNote: 'Element replaced',
              allowedActions: ['close', 'reopen', 'comment', 'addPhoto'] }),
    );

    expect(buttons(el)).toEqual(["check Yes, it's fixed", 'Not fixed: reopen']);
    expect(el.querySelector('[role="status"]')?.textContent).toContain('Please confirm it is fixed');
    expect(el.textContent).toContain('closes by itself after 7 days');
  });

  it('runs a step and shows the updated issue', async () => {
    role.set('Landlord');
    api.issueStep.mockReturnValue(of(issue({ status: 'Acknowledged', acknowledgedAt: '2026-11-04T08:00:00Z',
                                                allowedActions: ['start', 'comment'] })));
    const { fixture, el } = await render(issue());

    (el.querySelector('[aria-label="Issue actions"] button') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.issueStep).toHaveBeenCalledWith('i1', 'acknowledge');
    expect(el.textContent).toContain('Acknowledged');
    expect(buttons(el)).toEqual(['construction Start work']);
  });
});
