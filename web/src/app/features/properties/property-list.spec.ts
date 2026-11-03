import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { PropertyList } from './property-list';

describe('PropertyList', () => {
  const api = {
    listProperties: vi.fn(() =>
      of({
        items: [
          { id: 'p1', name: 'Jacaranda Court', city: 'Pretoria', province: 'Gauteng', unitCount: 3, occupiedCount: 1, createdAt: '' },
          { id: 'p2', name: 'Marula Gardens', city: 'Midrand', province: 'Gauteng', unitCount: 2, occupiedCount: 0, createdAt: '' },
        ],
        page: 1,
        pageSize: 20,
        totalCount: 2,
      }),
    ),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({
      imports: [PropertyList],
      providers: [provideRouter([]), { provide: ApiService, useValue: api }],
    }).compileComponents();
  });

  it('shows a row per property with occupancy and a link to its page', async () => {
    const fixture = TestBed.createComponent(PropertyList);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    const rows = el.querySelectorAll('tr.mat-mdc-row');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Jacaranda Court');
    expect(rows[0].textContent).toContain('1 of 3');
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe('/properties/p1');
  });

  it('asks the API for the first page sorted by name', async () => {
    const fixture = TestBed.createComponent(PropertyList);
    await fixture.whenStable();

    expect(api.listProperties).toHaveBeenCalledWith({ page: 1, pageSize: 20, sort: 'name', search: '' });
  });

  it('has an accessible table caption', async () => {
    const fixture = TestBed.createComponent(PropertyList);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('caption')?.textContent).toContain('Your properties');
  });
});
