import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { applyServerErrors, errorMessage } from '../../core/problem';

/** Admin: environment settings. Fields the API flags are placeholders needing legal review (ADR-0011). */
@Component({
  selector: 'app-settings',
  imports: [ReactiveFormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule, MatButtonModule],
  template: `
    <h1>Settings</h1>
    <p class="intro">
      Values marked <span class="legal">Legal review needed</span> are placeholders so the app runs.
      They are not legal advice and must be confirmed by a qualified person before any real use.
    </p>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    <mat-card appearance="outlined" class="card">
      <mat-card-content>
        <form [formGroup]="form" (ngSubmit)="save()" novalidate class="fields">
          <h2>Leases</h2>
          <mat-form-field>
            <mat-label>Expiry reminders (days before the end)</mat-label>
            <input matInput formControlName="expiryNoticeDays" required placeholder="90, 60, 30" />
            <mat-hint>The largest value is when a lease is flagged Expiring.</mat-hint>
            <mat-error>{{ message('expiryNoticeDays') }}</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Default escalation at renewal</mat-label>
            <input matInput type="number" formControlName="defaultEscalationPct" min="0" step="0.5" required />
            <span matTextSuffix>%</span>
            <mat-error>{{ message('defaultEscalationPct') }}</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Maximum escalation</mat-label>
            <input matInput type="number" formControlName="maxEscalationPct" min="0" max="50" step="0.5" required />
            <span matTextSuffix>%</span>
            <mat-hint>{{ legal('maxEscalationPct') }}</mat-hint>
            <mat-error>{{ message('maxEscalationPct') }}</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Termination notice</mat-label>
            <input matInput type="number" formControlName="terminationNoticeDays" min="0" max="365" required />
            <span matTextSuffix>days</span>
            <mat-hint>{{ legal('terminationNoticeDays') }}</mat-hint>
            <mat-error>{{ message('terminationNoticeDays') }}</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Lease template version</mat-label>
            <input matInput formControlName="leaseTemplateVersion" required maxlength="20" />
            <mat-hint>{{ legal('leaseTemplateVersion') }}</mat-hint>
            <mat-error>{{ message('leaseTemplateVersion') }}</mat-error>
          </mat-form-field>
          <div class="toggle">
            <mat-slide-toggle formControlName="monthToMonthOnLapse">
              Continue month to month when a lease ends without renewal
            </mat-slide-toggle>
            <span class="hint">Off: the lease ends and the unit becomes vacant. {{ legal('monthToMonthOnLapse') }}</span>
          </div>
          <h2>Rent and deposits</h2>
          <mat-form-field>
            <mat-label>Rent due day of the month</mat-label>
            <input matInput type="number" formControlName="rentDueDay" min="1" max="28" required />
            <mat-hint>1 to 28, so every month has it.</mat-hint>
            <mat-error>{{ message('rentDueDay') }}</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Grace period before rent is overdue</mat-label>
            <input matInput type="number" formControlName="rentGraceDays" min="0" max="31" required />
            <span matTextSuffix>days</span>
            <mat-error>{{ message('rentGraceDays') }}</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Deposit refund due after the lease ends</mat-label>
            <input matInput type="number" formControlName="depositRefundDays" min="0" max="365" required />
            <span matTextSuffix>days</span>
            <mat-hint>{{ legal('depositRefundDays') }}</mat-hint>
            <mat-error>{{ message('depositRefundDays') }}</mat-error>
          </mat-form-field>
          <div class="toggle">
            <mat-slide-toggle formControlName="prorateRent">Charge partial first and last months by days</mat-slide-toggle>
            <span class="hint">Off: a partial month is charged in full.</span>
          </div>
          <div class="actions">
            <button mat-flat-button type="submit" [disabled]="busy()">Save settings</button>
          </div>
        </form>
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    h1 { font: var(--mat-sys-headline-medium); margin: 0 0 8px; }
    h2 { font: var(--mat-sys-title-medium); margin: 8px 0; }
    .intro { max-width: 640px; color: var(--mat-sys-on-surface-variant); }
    .legal { font-weight: 500; color: var(--mat-sys-error); }
    .card { max-width: 640px; }
    .fields { display: flex; flex-direction: column; gap: 4px; }
    .toggle { display: flex; flex-direction: column; gap: 4px; margin: 8px 0 16px; }
    .hint { font: var(--mat-sys-body-small); color: var(--mat-sys-on-surface-variant); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class SettingsPage {
  private readonly api = inject(ApiService);
  private readonly snack = inject(MatSnackBar);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly flagged = signal<string[]>([]);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    expiryNoticeDays: ['', [Validators.required, Validators.pattern(/^\s*\d+(\s*,\s*\d+)*\s*$/)]],
    defaultEscalationPct: [0, [Validators.required, Validators.min(0)]],
    maxEscalationPct: [10, [Validators.required, Validators.min(0), Validators.max(50)]],
    terminationNoticeDays: [20, [Validators.required, Validators.min(0), Validators.max(365)]],
    leaseTemplateVersion: ['', Validators.required],
    monthToMonthOnLapse: [true],
    rentDueDay: [1, [Validators.required, Validators.min(1), Validators.max(28)]],
    rentGraceDays: [3, [Validators.required, Validators.min(0), Validators.max(31)]],
    depositRefundDays: [14, [Validators.required, Validators.min(0), Validators.max(365)]],
    prorateRent: [true],
  });

  constructor() {
    firstValueFrom(this.api.getSettings())
      .then((s) => {
        this.flagged.set(s.legalReviewNeeded);
        this.form.setValue({
          expiryNoticeDays: s.expiryNoticeDays.join(', '),
          defaultEscalationPct: s.defaultEscalationPct,
          maxEscalationPct: s.maxEscalationPct,
          terminationNoticeDays: s.terminationNoticeDays,
          leaseTemplateVersion: s.leaseTemplateVersion,
          monthToMonthOnLapse: s.monthToMonthOnLapse,
          rentDueDay: s.rentDueDay,
          rentGraceDays: s.rentGraceDays,
          depositRefundDays: s.depositRefundDays,
          prorateRent: s.prorateRent,
        });
      })
      .catch((e: unknown) => this.error.set(errorMessage(e)));
  }

  protected legal(field: string): string {
    return this.flagged().includes(field) ? 'LEGAL REVIEW NEEDED' : '';
  }

  protected message(name: keyof typeof this.form.controls): string {
    const errors = this.form.controls[name].errors;
    if (!errors) return '';
    if (errors['server']) return errors['server'] as string;
    if (errors['pattern']) return 'Use whole numbers separated by commas, e.g. 90, 60, 30.';
    if (errors['required']) return 'This field is required.';
    return 'Out of range.';
  }

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    const v = this.form.getRawValue();
    try {
      await firstValueFrom(
        this.api.updateSettings({
          ...v,
          expiryNoticeDays: v.expiryNoticeDays.split(',').map((d) => Number(d.trim())),
        }),
      );
      this.snack.open('Settings saved.', undefined, { duration: 3000 });
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
