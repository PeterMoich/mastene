import { HttpErrorResponse } from '@angular/common/http';
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
import { applyServerErrors, errorMessage, ProblemDetails } from '../../core/problem';

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
    <p>
      <button mat-stroked-button type="button" (click)="openJobs()">Open the background jobs dashboard</button>
      <span class="hint"> Reminders, the daily lease run and message sending. Opens in a new tab; the link works once.</span>
    </p>
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
          <h2>Maintenance targets</h2>
          <p class="hint">
            Hours from when an issue is logged, counted around the clock. An issue that misses a target is escalated.
            More urgent priorities can't have slower targets. These are suggestions, not legal deadlines.
          </p>
          <div class="sla" role="group" aria-label="Response and resolution targets per priority">
            @for (p of slaRows; track p.key) {
              <span class="prio" [id]="'sla-' + p.key">{{ p.label }}</span>
              <mat-form-field subscriptSizing="dynamic">
                <mat-label>Respond within</mat-label>
                <input matInput type="number" [formControlName]="p.response" min="1" max="2160" required
                       [attr.aria-describedby]="'sla-' + p.key" />
                <span matTextSuffix>h</span>
              </mat-form-field>
              <mat-form-field subscriptSizing="dynamic">
                <mat-label>Resolve within</mat-label>
                <input matInput type="number" [formControlName]="p.resolution" min="1" max="2160" required
                       [attr.aria-describedby]="'sla-' + p.key" />
                <span matTextSuffix>h</span>
              </mat-form-field>
            }
          </div>
          @if (slaError()) { <p class="error" role="alert">{{ slaError() }}</p> }
          <mat-form-field>
            <mat-label>Close a resolved issue automatically after</mat-label>
            <input matInput type="number" formControlName="issueAutoCloseDays" min="1" max="60" required />
            <span matTextSuffix>days</span>
            <mat-hint>If the tenant neither confirms the fix nor reopens it.</mat-hint>
            <mat-error>{{ message('issueAutoCloseDays') }}</mat-error>
          </mat-form-field>
          <h2>WhatsApp reminders</h2>
          <mat-form-field>
            <mat-label>Remind tenants before rent is due</mat-label>
            <input matInput type="number" formControlName="rentReminderDaysBefore" min="0" max="14" required />
            <span matTextSuffix>days</span>
            <mat-hint>0 sends it on the due day. Only tenants who agreed to WhatsApp messages get reminders.</mat-hint>
            <mat-error>{{ message('rentReminderDaysBefore') }}</mat-error>
          </mat-form-field>
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
    .sla { display: grid; grid-template-columns: 90px 1fr 1fr; gap: 8px 12px; align-items: center; margin: 8px 0 16px; }
    .prio { font: var(--mat-sys-label-large); }
    @media (max-width: 520px) { .sla { grid-template-columns: 1fr 1fr; } .prio { grid-column: 1 / -1; } }
  `,
})
export class SettingsPage {
  private readonly api = inject(ApiService);
  private readonly snack = inject(MatSnackBar);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly flagged = signal<string[]>([]);
  protected readonly slaError = signal<string | null>(null);
  protected readonly slaRows = (['Urgent', 'High', 'Medium', 'Low'] as const).map((label) => ({
    key: label.toLowerCase(),
    label,
    response: `sla${label}ResponseHours` as const,
    resolution: `sla${label}ResolutionHours` as const,
  }));

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
    slaUrgentResponseHours: [4, [Validators.required, Validators.min(1), Validators.max(2160)]],
    slaUrgentResolutionHours: [24, [Validators.required, Validators.min(1), Validators.max(2160)]],
    slaHighResponseHours: [24, [Validators.required, Validators.min(1), Validators.max(2160)]],
    slaHighResolutionHours: [72, [Validators.required, Validators.min(1), Validators.max(2160)]],
    slaMediumResponseHours: [48, [Validators.required, Validators.min(1), Validators.max(2160)]],
    slaMediumResolutionHours: [168, [Validators.required, Validators.min(1), Validators.max(2160)]],
    slaLowResponseHours: [72, [Validators.required, Validators.min(1), Validators.max(2160)]],
    slaLowResolutionHours: [336, [Validators.required, Validators.min(1), Validators.max(2160)]],
    issueAutoCloseDays: [7, [Validators.required, Validators.min(1), Validators.max(60)]],
    rentReminderDaysBefore: [3, [Validators.required, Validators.min(0), Validators.max(14)]],
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
          slaUrgentResponseHours: s.slaUrgentResponseHours,
          slaUrgentResolutionHours: s.slaUrgentResolutionHours,
          slaHighResponseHours: s.slaHighResponseHours,
          slaHighResolutionHours: s.slaHighResolutionHours,
          slaMediumResponseHours: s.slaMediumResponseHours,
          slaMediumResolutionHours: s.slaMediumResolutionHours,
          slaLowResponseHours: s.slaLowResponseHours,
          slaLowResolutionHours: s.slaLowResolutionHours,
          issueAutoCloseDays: s.issueAutoCloseDays,
          rentReminderDaysBefore: s.rentReminderDaysBefore,
        });
      })
      .catch((e: unknown) => this.error.set(errorMessage(e)));
  }

  protected async openJobs(): Promise<void> {
    // Open the tab first (in the click), then point it at the one-time link, so pop-up blockers allow it.
    const tab = window.open('', '_blank');
    try {
      const link = await firstValueFrom(this.api.jobsDashboardLink());
      if (tab) tab.location.href = link.url;
      else window.location.href = link.url;
    } catch (e) {
      tab?.close();
      this.snack.open(errorMessage(e), 'Close');
    }
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
    this.slaError.set(null);
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
      const sla = e instanceof HttpErrorResponse ? (e.error as ProblemDetails | null)?.errors?.['sla'] : undefined;
      if (sla) this.slaError.set(sla[0]);
      else if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
