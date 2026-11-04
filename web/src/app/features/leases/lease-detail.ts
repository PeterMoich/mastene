import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Lease, LeaseAction, LeaseDocument } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { applyServerErrors, errorMessage } from '../../core/problem';
import { confirm } from '../../shared/dialogs';
import { LeaseForm } from './lease-form';
import { LeaseDeposit, LeaseRent } from './lease-money';
import { saveBlob } from '../../shared/download';
import { addDaysIso, escalatedRent, LeaseStatusChip, todayIso } from './lease-shared';

@Component({
  selector: 'app-renew-dialog',
  imports: [ReactiveFormsModule, CurrencyPipe, DatePipe, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Renew lease {{ lease.leaseNumber }}</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        <p>The renewal is a new 12-month draft. Both you and the tenant sign it before it takes over.</p>
        <mat-form-field>
          <mat-label>Rent escalation</mat-label>
          <input matInput type="number" formControlName="escalationPct" min="0" step="0.5" required />
          <span matTextSuffix>%</span>
          <mat-hint>New rent {{ newRent() | currency: 'ZAR' : 'symbol-narrow' : '1.0-0' }}, rounded to the nearest rand.</mat-hint>
          <mat-error>{{ form.controls.escalationPct.errors?.['server'] ?? 'Enter 0 or more.' }}</mat-error>
        </mat-form-field>
        @if (lease.isMonthToMonth) {
          <mat-form-field>
            <mat-label>Renewal starts on</mat-label>
            <input matInput type="date" formControlName="startDate" required />
          </mat-form-field>
        } @else {
          <p>The renewal starts on {{ renewalStart | date: 'd MMMM y' }}, the day after this lease ends.</p>
        }
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy()">Create renewal</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(420px, 80vw); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class RenewDialog {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<RenewDialog, Lease>);
  protected readonly lease = inject<Lease>(MAT_DIALOG_DATA);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    escalationPct: [this.lease.escalationPct, [Validators.required, Validators.min(0)]],
    startDate: [todayIso()],
  });
  protected readonly renewalStart = addDaysIso(this.lease.endDate, 1);
  private readonly pct = signal(this.lease.escalationPct);
  protected readonly newRent = computed(() => escalatedRent(this.lease.monthlyRent, this.pct() || 0));

  constructor() {
    this.form.controls.escalationPct.valueChanges.subscribe((v) => this.pct.set(Number(v)));
  }

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const v = this.form.getRawValue();
    try {
      this.ref.close(
        await firstValueFrom(
          this.api.renewLease(this.lease.id, {
            escalationPct: v.escalationPct,
            startDate: this.lease.isMonthToMonth ? v.startDate : null,
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

export interface TerminateData {
  lease: Lease;
  /** Cancelling a lease that is still waiting for signatures: no last day needed. */
  cancel: boolean;
  noticeDays: number;
}

@Component({
  selector: 'app-terminate-dialog',
  imports: [ReactiveFormsModule, DatePipe, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ data.cancel ? 'Cancel lease' : 'Terminate lease' }} {{ data.lease.leaseNumber }}</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        @if (data.cancel) {
          <p>The lease hasn't started. Cancelling it stops it for both parties straight away. It is kept on record.</p>
        } @else {
          <p>
            The lease stays active until its last day, then ends and the unit becomes vacant.
            Minimum notice: {{ data.noticeDays }} days, so the earliest last day is {{ earliest | date: 'd MMMM y' }}.
          </p>
          <p class="legal">The notice period is a setting marked LEGAL REVIEW NEEDED. It is not legal advice.</p>
          <mat-form-field>
            <mat-label>Last day of the lease</mat-label>
            <input matInput type="date" formControlName="lastDay" [min]="earliest" required />
            <mat-error>{{ form.controls.lastDay.errors?.['server'] ?? 'Choose the last day.' }}</mat-error>
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>Reason</mat-label>
          <textarea matInput formControlName="reason" rows="3" maxlength="500" required></textarea>
          <mat-error>{{ form.controls.reason.errors?.['server'] ?? 'A reason is required.' }}</mat-error>
        </mat-form-field>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Back</button>
        <button mat-flat-button type="submit" [disabled]="busy()">{{ data.cancel ? 'Cancel lease' : 'Schedule termination' }}</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(440px, 80vw); }
    .legal { font: var(--mat-sys-body-small); color: var(--mat-sys-on-surface-variant); margin-top: -4px; }
    .error { color: var(--mat-sys-error); }
  `,
})
export class TerminateDialog {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<TerminateDialog, Lease>);
  protected readonly data = inject<TerminateData>(MAT_DIALOG_DATA);
  protected readonly earliest = addDaysIso(todayIso(), this.data.noticeDays);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    reason: ['', [Validators.required, Validators.maxLength(500)]],
    lastDay: [this.earliest, Validators.required],
  });

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const v = this.form.getRawValue();
    try {
      this.ref.close(
        await firstValueFrom(
          this.api.terminateLease(this.data.lease.id, {
            reason: v.reason,
            lastDay: this.data.cancel ? todayIso() : v.lastDay,
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
  selector: 'app-lease-detail',
  imports: [RouterLink, CurrencyPipe, DatePipe, DecimalPipe, MatButtonModule, MatIconModule, LeaseStatusChip, LeaseRent, LeaseDeposit],
  template: `
    <a mat-button routerLink="/leases"><mat-icon aria-hidden="true">arrow_back</mat-icon> {{ isTenant() ? 'My leases' : 'All leases' }}</a>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    @if (lease(); as l) {
      <header class="head">
        <div>
          <h1>Lease {{ l.leaseNumber }} <span class="version">version {{ l.version }}</span></h1>
          <app-lease-status [status]="l.status" [monthToMonth]="l.isMonthToMonth" />
        </div>
        <div class="buttons" role="group" aria-label="Lease actions">
          @if (can('sign')) {
            <button mat-flat-button type="button" (click)="sign(l)" [disabled]="busy()"><mat-icon aria-hidden="true">draw</mat-icon> Sign</button>
          }
          @if (can('generate')) {
            <button mat-flat-button type="button" (click)="generate(l)" [disabled]="busy()"><mat-icon aria-hidden="true">send</mat-icon> Send for signing</button>
          }
          @if (can('edit')) {
            <button mat-stroked-button type="button" (click)="edit(l)"><mat-icon aria-hidden="true">edit</mat-icon> Edit</button>
          }
          @if (can('renew')) {
            <button mat-flat-button type="button" (click)="renew(l)"><mat-icon aria-hidden="true">autorenew</mat-icon> Renew</button>
          }
          @if (can('terminate')) {
            <button mat-stroked-button type="button" (click)="terminate(l, false)">Terminate</button>
          }
          @if (can('cancel')) {
            <button mat-stroked-button type="button" (click)="terminate(l, true)">Cancel lease</button>
          }
          @if (can('discard')) {
            <button mat-stroked-button type="button" (click)="discard(l)"><mat-icon aria-hidden="true">delete</mat-icon> Discard draft</button>
          }
        </div>
      </header>

      @if (l.status === 'PendingSignature') {
        <p class="notice" role="status">
          @if (l.signatures.length === 2) {
            Both parties have signed. The lease starts on {{ l.startDate | date: 'd MMMM y' }}.
          } @else {
            Waiting for the {{ waitingFor(l) }} to sign.
          }
        </p>
      }
      @if (l.terminatesOn && l.status !== 'Terminated') {
        <p class="notice warn" role="status">Termination scheduled: last day {{ l.terminatesOn | date: 'd MMMM y' }}. Reason: {{ l.terminationReason }}</p>
      }

      <section class="grid" aria-label="Lease details">
        <dl>
          <dt>Tenant</dt><dd>{{ l.tenantName }}</dd>
          <dt>Premises</dt><dd>{{ l.unitLabel }}, {{ l.propertyName }}<br /><span class="muted">{{ l.propertyAddress }}</span></dd>
          <dt>Landlord</dt><dd>{{ l.landlordName ?? 'Set when the lease is sent for signing' }}</dd>
        </dl>
        <dl>
          <dt>Term</dt>
          <dd>
            {{ l.startDate | date: 'd MMMM y' }} to {{ l.endDate | date: 'd MMMM y' }}
            @if (l.isMonthToMonth) { <br /><span class="muted">Continuing month to month since the end date</span> }
          </dd>
          <dt>Monthly rent</dt><dd>{{ l.monthlyRent | currency: 'ZAR' : 'symbol-narrow' }}</dd>
          <dt>Deposit</dt><dd>{{ l.depositAmount | currency: 'ZAR' : 'symbol-narrow' }}</dd>
          <dt>Escalation at renewal</dt><dd>{{ l.escalationPct }}%</dd>
        </dl>
      </section>

      <section aria-labelledby="sig-heading">
        <h2 id="sig-heading">Signatures</h2>
        @if (l.status === 'Draft') {
          <p class="muted">Signatures are collected once the lease is sent for signing.</p>
        } @else {
          <ul class="plain">
            @for (party of parties; track party) {
              <li>
                <strong>{{ party }}:</strong>
                @if (signature(l, party); as s) {
                  signed by {{ s.signerName }} on {{ s.signedAt | date: 'd MMM y, HH:mm' }}
                } @else {
                  <span class="muted">not signed yet</span>
                }
              </li>
            }
          </ul>
        }
      </section>

      @if (l.activatedOn) {
        <app-lease-rent [leaseId]="l.id" />
      }
      @if (l.status !== 'Draft') {
        <app-lease-deposit [leaseId]="l.id" />
      }

      @if (l.documents.length > 0) {
        <section aria-labelledby="doc-heading">
          <h2 id="doc-heading">Documents</h2>
          <ul class="plain">
            @for (d of l.documents; track d.id) {
              <li>
                <button mat-button type="button" (click)="download(l, d)">
                  <mat-icon aria-hidden="true">picture_as_pdf</mat-icon>
                  {{ d.kind === 'Signed' ? 'Signed lease' : 'Lease for signing' }} ({{ d.sizeBytes / 1024 | number: '1.0-0' }} KB)
                </button>
              </li>
            }
          </ul>
        </section>
      }

      @if (l.previousLeaseId || l.renewalLeaseId) {
        <section aria-labelledby="ver-heading">
          <h2 id="ver-heading">Versions</h2>
          <ul class="plain">
            @if (l.previousLeaseId) { <li><a [routerLink]="['/leases', l.previousLeaseId]">Previous version</a></li> }
            @if (l.renewalLeaseId) { <li><a [routerLink]="['/leases', l.renewalLeaseId]">Renewal</a></li> }
          </ul>
        </section>
      }
    }
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .version { font: var(--mat-sys-title-medium); color: var(--mat-sys-on-surface-variant); }
    .buttons { display: flex; gap: 8px; flex-wrap: wrap; }
    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; margin: 16px 0; }
    dt { font-weight: 500; margin-top: 10px; }
    dd { margin: 0; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    h2 { font: var(--mat-sys-title-large); margin: 24px 0 8px; }
    .plain { list-style: none; padding: 0; margin: 0; }
    .plain li { padding: 4px 0; }
    .notice { background: var(--mat-sys-tertiary-container); color: var(--mat-sys-on-tertiary-container);
              padding: 8px 12px; border-radius: 8px; }
    .notice.warn { background: var(--mat-sys-error-container); color: var(--mat-sys-on-error-container); }
    @media (max-width: 720px) { .grid { grid-template-columns: 1fr; gap: 0; } }
  `,
})
export class LeaseDetail {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);

  /** Route parameter :id. */
  readonly id = input.required<string>();

  protected readonly parties = ['Landlord', 'Tenant'] as const;
  protected readonly lease = signal<Lease | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly isTenant = computed(() => this.auth.role() === 'Tenant');

  constructor() {
    effect(() => void this.load(this.id()));
  }

  protected can(action: LeaseAction): boolean {
    return this.lease()?.allowedActions.includes(action) ?? false;
  }

  protected signature(l: Lease, party: 'Landlord' | 'Tenant') {
    return l.signatures.find((s) => s.party === party) ?? null;
  }

  protected waitingFor(l: Lease): string {
    const missing = this.parties.filter((p) => !this.signature(l, p)).map((p) => p.toLowerCase());
    return missing.join(' and the ');
  }

  private async load(id: string): Promise<void> {
    try {
      this.error.set(null);
      this.lease.set(await firstValueFrom(this.api.getLease(id)));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  private async act(work: () => Promise<Lease | void>, done?: string): Promise<void> {
    this.busy.set(true);
    try {
      const result = await work();
      if (result) this.lease.set(result);
      if (done) this.snack.open(done, undefined, { duration: 4000 });
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    } finally {
      this.busy.set(false);
    }
  }

  protected async sign(l: Lease): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: 'Sign this lease?',
      message: 'Download and read the lease first. Signing records your name, the time and your IP address.',
      confirmLabel: 'Sign lease',
    });
    if (ok) await this.act(() => firstValueFrom(this.api.signLease(l.id)), 'Signed.');
  }

  protected async generate(l: Lease): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: 'Send for signing?',
      message: 'This creates the lease PDF. After this the lease can no longer be changed, only cancelled.',
      confirmLabel: 'Send for signing',
    });
    if (ok) await this.act(() => firstValueFrom(this.api.generateLease(l.id)), 'Lease ready for signing.');
  }

  protected async edit(l: Lease): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog.open<LeaseForm, Lease, Lease>(LeaseForm, { data: l, width: '640px' }).afterClosed(),
    );
    if (saved) this.lease.set(saved);
  }

  protected async discard(l: Lease): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: 'Discard this draft?',
      message: 'The draft is deleted. This cannot be undone.',
      confirmLabel: 'Discard draft',
    });
    if (!ok) return;
    await this.act(async () => {
      await firstValueFrom(this.api.discardLease(l.id));
      await this.router.navigateByUrl('/leases');
    });
  }

  protected async renew(l: Lease): Promise<void> {
    const renewal = await firstValueFrom(
      this.dialog.open<RenewDialog, Lease, Lease>(RenewDialog, { data: l }).afterClosed(),
    );
    if (renewal) await this.router.navigate(['/leases', renewal.id]);
  }

  protected async terminate(l: Lease, cancel: boolean): Promise<void> {
    let noticeDays = 0;
    if (!cancel) {
      try {
        noticeDays = (await firstValueFrom(this.api.getSettings())).terminationNoticeDays;
      } catch (e) {
        this.snack.open(errorMessage(e), 'Close');
        return;
      }
    }
    const updated = await firstValueFrom(
      this.dialog
        .open<TerminateDialog, TerminateData, Lease>(TerminateDialog, { data: { lease: l, cancel, noticeDays } })
        .afterClosed(),
    );
    if (updated) this.lease.set(updated);
  }

  protected async download(l: Lease, d: LeaseDocument): Promise<void> {
    try {
      const blob = await firstValueFrom(this.api.leaseDocument(l.id, d.id));
      saveBlob(blob, `${l.leaseNumber}-v${l.version}-${d.kind === 'Signed' ? 'signed' : 'for-signing'}.pdf`);
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }
}
