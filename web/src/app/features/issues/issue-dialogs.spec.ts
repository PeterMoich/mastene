import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { TextDialog, TextDialogData } from './issue-dialogs';

describe('TextDialog', () => {
  const close = vi.fn();

  async function render(data: Partial<TextDialogData>) {
    await TestBed.configureTestingModule({
      imports: [TextDialog],
      providers: [
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: { title: 'Reopen', label: 'Why?', confirmLabel: 'Reopen', required: true, maxLength: 100, ...data } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TextDialog);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('returns the text when the form is submitted', async () => {
    close.mockClear();
    const { fixture, el } = await render({});
    const box = el.querySelector('textarea') as HTMLTextAreaElement;
    box.value = '  Still leaking  ';
    box.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));

    expect(close).toHaveBeenCalledWith('Still leaking');
  });

  it('does not close without text when text is required', async () => {
    close.mockClear();
    const { el } = await render({});
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    expect(close).not.toHaveBeenCalled();
  });
});
