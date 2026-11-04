import { CurrencyPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import {
  LedgerLine,
  Payment,
  PROOF_OF_PAYMENT_MAX_BYTES,
  PROOF_OF_PAYMENT_TYPES,
} from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { applyServerErrors, errorMessage } from '../../core/problem';
import { monthName } from '../../shared/download';
import { todayIso } from '../leases/lease-shared';

export interface UploadPaymentData {
  leaseId: string;
  /** Months with something still owing, offered in the "for month" list. */
  openLines: LedgerLine[];
}

/** Checks a chosen file the same way the API will (BR12), so the user hears about it before uploading. */
export function checkProofFile(file: File | null): string | null {
  if (!file) return 'Choose the file to upload.';
  if (file.size === 0) return 'The file is empty.';
  if (file.size > PROOF_OF_PAYMENT_MAX_BYTES) return 'The file is larger than 5 MB.';
  const byName = /\.(pdf|jpe?g|png)$/i.test(file.name);
  if (!PROOF_OF_PAYMENT_TYPES.includes(file.type) && !byName) return 'Only PDF, JPG or PNG files are accepted.';
  return null;
}

@Component({
  selector: 'app-upload-payment-dialog',
  imports: [ReactiveFormsModule, CurrencyPipe, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Upload proof of payment</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        <div class="file">
          <label for="proof-file" class="file-label">Bank confirmation or photo (PDF, JPG or PNG, up to 5 MB)</label>
          <input id="proof-file" type="file" accept=".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png"
                 (change)="onFile($any($event.target).files?.[0] ?? null)" />
          @if (fileError()) { <p class="error" role="alert">{{ fileError() }}</p> }
        </div>
        <mat-form-field>
          <mat-label>Amount paid</mat-label>
          <span matTextPrefix>R&nbsp;</span>
          <input matInput type="number" formControlName="amount" required min="0.01" step="0.01" />
          <mat-error>{{ form.controls.amount.errors?.['server'] ?? 'Enter the amount you paid.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Date paid</mat-label>
          <input matInput type="date" formControlName="paidOn" [max]="today" required />
          <mat-error>{{ form.controls.paidOn.errors?.['server'] ?? 'Enter the date you paid.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>For month</mat-label>
          <mat-select formControlName="period">
            <mat-option value="">Oldest unpaid month first</mat-option>
            @for (line of data.openLines; track line.period) {
              <mat-option [value]="line.period">
                {{ month(line.period) }} ({{ line.outstanding | currency: 'ZAR' : 'symbol-narrow' }} owing)
              </mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Reference (optional)</mat-label>
          <input matInput formControlName="reference" maxlength="50" />
        </mat-form-field>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy()">Upload</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(440px, 80vw); }
    .file { margin-bottom: 16px; display: flex; flex-direction: column; gap: 6px; }
    .file-label { font: var(--mat-sys-body-medium); }
    .error { color: var(--mat-sys-error); margin: 0; }
  `,
})
export class UploadPaymentDialog {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<UploadPaymentDialog, Payment>);
  protected readonly data = inject<UploadPaymentData>(MAT_DIALOG_DATA);
  protected readonly today = todayIso();
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly fileError = signal<string | null>(null);
  private file: File | null = null;

  protected readonly form = inject(FormBuilder).nonNullable.group({
    amount: [this.data.openLines[0]?.outstanding ?? 0, [Validators.required, Validators.min(0.01)]],
    paidOn: [this.today, Validators.required],
    period: [''],
    reference: [''],
  });

  protected month(period: string): string {
    return monthName(period);
  }

  onFile(file: File | null): void {
    this.file = file;
    this.fileError.set(checkProofFile(file));
  }

  async save(): Promise<void> {
    const fileProblem = checkProofFile(this.file);
    this.fileError.set(fileProblem);
    if (this.form.invalid || fileProblem) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const v = this.form.getRawValue();
    try {
      this.ref.close(
        await firstValueFrom(
          this.api.submitPayment({
            leaseId: this.data.leaseId,
            amount: v.amount,
            paidOn: v.paidOn,
            period: v.period || null,
            reference: v.reference || null,
            file: this.file!,
          }),
        ),
      );
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'app-reject-payment-dialog',
  imports: [ReactiveFormsModule, CurrencyPipe, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Reject payment</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        <p>{{ payment.tenantName }} · {{ payment.amount | currency: 'ZAR' : 'symbol-narrow' }} paid on {{ payment.paidOn }}.
           The tenant sees your reason and can upload a corrected proof.</p>
        <mat-form-field>
          <mat-label>Reason</mat-label>
          <textarea matInput formControlName="reason" rows="3" maxlength="500" required></textarea>
          <mat-error>A reason is required.</mat-error>
        </mat-form-field>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Back</button>
        <button mat-flat-button type="submit" [disabled]="busy()">Reject payment</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(420px, 80vw); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class RejectPaymentDialog {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<RejectPaymentDialog, Payment>);
  protected readonly payment = inject<Payment>(MAT_DIALOG_DATA);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    reason: ['', [Validators.required, Validators.maxLength(500)]],
  });

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    try {
      this.ref.close(await firstValueFrom(this.api.rejectPayment(this.payment.id, this.form.getRawValue().reason)));
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
