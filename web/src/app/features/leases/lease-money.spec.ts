import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { Ledger } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { LeaseDeposit, LeaseRent } from './lease-money';

const ledger: Ledger = {
  leaseId: 'l1',
  leaseNumber: 'L2026-0001',
  version: 1,
  totalDue: 13000,
  totalPaid: 9500,
  balance: 3500,
  arrears: 3500,
  credit: 0,
  graceDays: 3,
  lines: [
    { period: '2026-09', amountDue: 6500, dueDate: '2026-09-01', daysCharged: 30, prorated: false, paid: 3000,
      outstanding: 3500, state: 'Overdue', overdueNoticeOn: '2026-09-05' },
    { period: '2026-10', amountDue: 6500, dueDate: '2026-10-01', daysCharged: 31, prorated: false, paid: 6500,
      outstanding: 0, state: 'Paid', overdueNoticeOn: null },
  ],
};

describe('LeaseRent', () => {
  const api = {
    getLedger: vi.fn(() => of(ledger)),
    listPayments: vi.fn(() => of({ items: [], page: 1, pageSize: 100, totalCount: 0 })),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LeaseRent],
      providers: [provideRouter([]), { provide: ApiService, useValue: api }, { provide: MatDialog, useValue: {} }],
    }).compileComponents();
  });

  async function render() {
    const fixture = TestBed.createComponent(LeaseRent);
    fixture.componentRef.setInput('leaseId', 'l1');
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the balance and arrears', async () => {
    const el = await render();
    expect(el.textContent).toContain('Balance owing');
    expect(el.textContent).toContain('R3,500.00');
  });

  it('lists months newest first and says what is overdue in words, not just colour', async () => {
    const el = await render();
    const rows = Array.from(el.querySelectorAll('tr.mat-mdc-row')).map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('October 2026');
    expect(rows[0]).toContain('Paid');
    expect(rows[1]).toContain('September 2026');
    expect(rows[1]).toContain('Overdue');
    expect(rows[1]).toContain('R3,500.00 owing');
  });
});

describe('LeaseDeposit', () => {
  it('offers to record the deposit when it has not been received', async () => {
    await TestBed.configureTestingModule({
      imports: [LeaseDeposit],
      providers: [
        {
          provide: ApiService,
          useValue: {
            getDeposit: vi.fn(() =>
              of({
                leaseNumber: 'L2026-0001', amountAgreed: 6500, amountReceived: 0, receivedOn: null,
                status: 'AwaitingPayment', deductions: [], totalDeducted: 0, remaining: 0, amountRefunded: 0,
                refundedOn: null, leaseHasEnded: false, leaseEndedOn: null, refundDueBy: null,
                allowedActions: ['receive'],
              }),
            ),
          },
        },
        { provide: MatDialog, useValue: {} },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(LeaseDeposit);
    fixture.componentRef.setInput('leaseId', 'l1');
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Not received yet');
    expect(el.querySelector('button')?.textContent).toContain('Record deposit received');
  });
});
