import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { Lease, Role } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { LeaseDetail } from './lease-detail';

function lease(overrides: Partial<Lease> = {}): Lease {
  return {
    id: 'l1',
    leaseNumber: 'L2026-0007',
    version: 1,
    status: 'PendingSignature',
    isMonthToMonth: false,
    propertyId: 'p1',
    propertyName: 'Jacaranda Court',
    propertyAddress: '14 Jacaranda Street, Pretoria, Gauteng, 0083',
    unitId: 'u1',
    unitLabel: 'Flat 1',
    tenantId: 't1',
    tenantName: 'Sipho Dlamini',
    landlordName: 'Thandi Nkosi',
    startDate: '2026-12-01',
    endDate: '2027-11-30',
    monthlyRent: 6500,
    depositAmount: 6500,
    escalationPct: 8,
    previousLeaseId: null,
    renewalLeaseId: null,
    activatedOn: null,
    terminatesOn: null,
    terminationReason: null,
    templateVersion: 'v0-test',
    signatures: [{ party: 'Landlord', signerName: 'Thandi Nkosi', signedAt: '2026-11-04T08:00:00Z' }],
    documents: [{ id: 'd1', kind: 'Unsigned', createdAt: '2026-11-04T07:00:00Z', sizeBytes: 40960 }],
    expiryNoticesRecorded: [],
    allowedActions: ['download', 'sign'],
    createdAt: '2026-11-04T07:00:00Z',
    updatedAt: null,
    ...overrides,
  };
}

describe('LeaseDetail', () => {
  const role = signal<Role>('Tenant');
  const api = {
    getLease: vi.fn(),
    signLease: vi.fn(),
    getDeposit: vi.fn(() =>
      of({
        leaseNumber: 'L2026-0007', amountAgreed: 6500, amountReceived: 0, receivedOn: null, status: 'AwaitingPayment',
        deductions: [], totalDeducted: 0, remaining: 0, amountRefunded: 0, refundedOn: null, leaseHasEnded: false,
        leaseEndedOn: null, refundDueBy: null, allowedActions: [],
      }),
    ),
    getLedger: vi.fn(() =>
      of({ leaseId: 'l1', leaseNumber: 'L2026-0007', version: 1, totalDue: 0, totalPaid: 0, balance: 0, arrears: 0,
           credit: 0, graceDays: 3, lines: [] }),
    ),
    listPayments: vi.fn(() => of({ items: [], page: 1, pageSize: 100, totalCount: 0 })),
    leaseInspections: vi.fn(() => of({ leaseNumber: 'L2026-0007', items: [], allowedActions: [] })),
  };
  const dialog = { open: vi.fn(() => ({ afterClosed: () => of(true) })) };

  async function render(data: Lease) {
    api.getLease.mockReturnValue(of(data));
    const fixture = TestBed.createComponent(LeaseDetail);
    fixture.componentRef.setInput('id', data.id);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const buttonLabels = (el: HTMLElement) =>
    Array.from(el.querySelectorAll('[aria-label="Lease actions"] button')).map((b) => b.textContent?.trim() ?? '');

  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({
      imports: [LeaseDetail],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: api },
        { provide: AuthService, useValue: { role } },
        { provide: MatDialog, useValue: dialog },
      ],
    }).compileComponents();
  });

  it('shows only the actions the API allows', async () => {
    const { el } = await render(lease());
    expect(buttonLabels(el)).toEqual([expect.stringContaining('Sign')]);
  });

  it('tells the reader who still has to sign', async () => {
    const { el } = await render(lease());
    expect(el.querySelector('[role="status"]')?.textContent).toContain('Waiting for the tenant to sign');
  });

  it('says when both have signed and the lease starts later', async () => {
    const { el } = await render(
      lease({
        signatures: [
          { party: 'Landlord', signerName: 'Thandi Nkosi', signedAt: '2026-11-04T08:00:00Z' },
          { party: 'Tenant', signerName: 'Sipho Dlamini', signedAt: '2026-11-04T09:00:00Z' },
        ],
        allowedActions: ['download'],
      }),
    );
    expect(el.querySelector('[role="status"]')?.textContent).toContain('Both parties have signed');
    expect(buttonLabels(el)).toEqual([]);
  });

  it('signs after the user confirms, and shows the updated lease', async () => {
    api.signLease.mockReturnValue(of(lease({ status: 'Active', allowedActions: ['download'] })));
    const { fixture, el } = await render(lease());

    (el.querySelector('[aria-label="Lease actions"] button') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(dialog.open).toHaveBeenCalled();
    expect(api.signLease).toHaveBeenCalledWith('l1');
    expect(el.textContent).toContain('Active');
  });

  it('offers renew and terminate to the landlord on an active lease', async () => {
    role.set('Landlord');
    const { el } = await render(lease({ status: 'Active', allowedActions: ['download', 'renew', 'terminate'] }));
    const labels = buttonLabels(el);
    expect(labels.some((l) => l.includes('Renew'))).toBe(true);
    expect(labels.some((l) => l.includes('Terminate'))).toBe(true);
    expect(labels.some((l) => l.includes('Sign'))).toBe(false);
    role.set('Tenant');
  });

  it('lists the documents for download', async () => {
    const { el } = await render(lease());
    expect(el.textContent).toContain('Lease for signing (40 KB)');
  });
});
