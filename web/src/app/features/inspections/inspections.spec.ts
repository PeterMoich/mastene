import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { Inspection, InspectionItem } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { InspectionDetail, worseThanMoveIn } from './inspections';

const item = (overrides: Partial<InspectionItem> = {}): InspectionItem => ({
  id: 'x1', area: 'Bathroom', order: 0, condition: 'Damaged', notes: 'Cracked basin', photos: [],
  moveInCondition: 'Fair', moveInNotes: 'Chipped tile', moveInPhotos: [], ...overrides,
});

function inspection(overrides: Partial<Inspection> = {}): Inspection {
  return {
    id: 'n1', leaseId: 'l1', leaseNumber: 'L2026-0001', kind: 'MoveOut', status: 'Completed', inspectedOn: '2026-11-01',
    generalNotes: null, completedAt: '2026-11-01T10:00:00Z', completedByName: 'Thandi Nkosi', tenantAcknowledgedAt: null,
    tenantComment: null, propertyName: 'Jacaranda Court', unitLabel: 'Flat 1', tenantName: 'Sipho Dlamini',
    moveInInspectionId: 'm1', items: [item(), item({ id: 'x2', area: 'Lounge', condition: 'Good', notes: null, moveInCondition: 'Good', moveInNotes: null })],
    allowedActions: ['acknowledge'], ...overrides,
  };
}

describe('worseThanMoveIn', () => {
  it('flags an area that got worse, not one that stayed the same', () => {
    expect(worseThanMoveIn(item())).toBe(true);
    expect(worseThanMoveIn(item({ condition: 'Fair' }))).toBe(false);
    expect(worseThanMoveIn(item({ moveInCondition: null }))).toBe(false);
  });
});

describe('InspectionDetail', () => {
  const api = { getInspection: vi.fn(), inspectionPhotoUrl: vi.fn(() => '/x'), image: vi.fn() };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [InspectionDetail],
      providers: [provideRouter([]), { provide: ApiService, useValue: api }, { provide: MatDialog, useValue: { open: vi.fn() } }],
    }).compileComponents();
  });

  async function render(data: Inspection) {
    api.getInspection.mockReturnValue(of(data));
    const fixture = TestBed.createComponent(InspectionDetail);
    fixture.componentRef.setInput('id', data.id);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows a completed move-out next to move-in, flagging what got worse, read-only', async () => {
    const el = await render(inspection());

    expect(el.querySelector('h1')?.textContent).toContain('Move-out inspection');
    expect(el.textContent).toContain('At move-in: Fair · Chipped tile');
    expect(el.textContent).toContain('Worse than at move-in');
    expect(el.textContent).toContain('Waiting for the tenant to acknowledge');
    expect(el.querySelector('mat-select')).toBeNull();
    expect(el.querySelector('button')?.textContent).not.toContain('Complete');
  });

  it('lets the landlord fill in a draft', async () => {
    const el = await render(inspection({ kind: 'MoveIn', status: 'Draft', completedAt: null, completedByName: null,
      moveInInspectionId: null, items: [item({ condition: null, moveInCondition: null })], allowedActions: ['edit', 'complete'] }));

    expect(el.querySelectorAll('mat-select').length).toBe(1);
    expect(el.textContent).toContain('0 of 1 areas checked');
    expect(el.textContent).toContain('Complete inspection');
  });
});
