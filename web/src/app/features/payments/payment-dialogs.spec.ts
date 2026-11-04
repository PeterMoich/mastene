import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { checkProofFile, UploadPaymentDialog } from './payment-dialogs';

const file = (name: string, type: string, size: number) => new File([new Uint8Array(size)], name, { type });

describe('checkProofFile (BR12, mirrored from the API)', () => {
  it('accepts PDF, JPG and PNG up to 5 MB', () => {
    expect(checkProofFile(file('eft.pdf', 'application/pdf', 1000))).toBeNull();
    expect(checkProofFile(file('photo.jpg', 'image/jpeg', 1000))).toBeNull();
    expect(checkProofFile(file('scan.png', 'image/png', 5 * 1024 * 1024))).toBeNull();
  });

  it('rejects files over 5 MB, empty files and other types', () => {
    expect(checkProofFile(file('big.pdf', 'application/pdf', 5 * 1024 * 1024 + 1))).toContain('5 MB');
    expect(checkProofFile(file('empty.pdf', 'application/pdf', 0))).toContain('empty');
    expect(checkProofFile(file('notes.docx', 'application/msword', 10))).toContain('PDF, JPG or PNG');
    expect(checkProofFile(null)).toContain('Choose');
  });
});

describe('UploadPaymentDialog', () => {
  const api = { submitPayment: vi.fn() };
  const ref = { close: vi.fn() };

  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({
      imports: [UploadPaymentDialog],
      providers: [
        { provide: ApiService, useValue: api },
        { provide: MatDialogRef, useValue: ref },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            leaseId: 'l1',
            openLines: [
              { period: '2026-10', amountDue: 6500, dueDate: '2026-10-01', daysCharged: 31, prorated: false,
                paid: 3000, outstanding: 3500, state: 'Overdue', overdueNoticeOn: null },
            ],
          },
        },
      ],
    }).compileComponents();
  });

  it('suggests the oldest outstanding amount', async () => {
    const fixture = TestBed.createComponent(UploadPaymentDialog);
    await fixture.whenStable();
    const amount = (fixture.nativeElement as HTMLElement).querySelector('input[type="number"]') as HTMLInputElement;
    expect(amount.value).toBe('3500');
  });

  it('does not upload without a valid file', async () => {
    const fixture = TestBed.createComponent(UploadPaymentDialog);
    await fixture.componentInstance.save();
    fixture.detectChanges();
    expect(api.submitPayment).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Choose the file');
  });

  it('uploads the file with the form values and closes', async () => {
    api.submitPayment.mockReturnValue(of({ id: 'p1' }));
    const fixture = TestBed.createComponent(UploadPaymentDialog);
    const proof = file('eft.pdf', 'application/pdf', 2048);
    fixture.componentInstance.onFile(proof);

    await fixture.componentInstance.save();

    expect(api.submitPayment).toHaveBeenCalledWith(
      expect.objectContaining({ leaseId: 'l1', amount: 3500, period: null, file: proof }),
    );
    expect(ref.close).toHaveBeenCalledWith({ id: 'p1' });
  });
});
