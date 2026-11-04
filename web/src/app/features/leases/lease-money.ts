import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { firstValueFrom } from 'rxjs';
import { Deposit, Ledger, Payment } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/problem';
import { monthName } from '../../shared/download';
import { UploadPaymentData, UploadPaymentDialog } from '../payments/payment-dialogs';
import { PaymentTable } from '../payments/payment-table';
import { todayIso } from './lease-shared';

/** Rent for one lease (BR09): balance, monthly charges and the payments made against them. */
@Component({
  selector: 'app-lease-rent',
  imports: [CurrencyPipe, DatePipe, MatTableModule, MatButtonModule, MatIconModule, PaymentTable],
  template: `
    <section aria-labelledby="rent-heading">
      <div class="head">
        <h2 id="rent-heading">Rent</h2>
        <button mat-flat-button type="button" (click)="upload()" [disabled]="!ledger()">
          <mat-icon aria-hidden="true">upload_file</mat-icon> Upload proof of payment
        </button>
      </div>
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      @if (ledger(); as l) {
        <div class="tiles">
          <div class="tile" [class.bad]="l.balance > 0">
            <span class="label">{{ l.balance < 0 ? 'In credit' : 'Balance owing' }}</span>
            <span class="value">{{ abs(l.balance) | currency: 'ZAR' : 'symbol-narrow' }}</span>
          </div>
          <div class="tile" [class.bad]="l.arrears > 0">
            <span class="label">Overdue (arrears)</span>
            <span class="value">{{ l.arrears | currency: 'ZAR' : 'symbol-narrow' }}</span>
          </div>
          <div class="tile">
            <span class="label">Paid to date</span>
            <span class="value">{{ l.totalPaid | currency: 'ZAR' : 'symbol-narrow' }}</span>
          </div>
        </div>

        <table mat-table [dataSource]="lines()" class="table">
          <caption class="visually-hidden">Monthly rent charges</caption>
          <ng-container matColumnDef="period">
            <th mat-header-cell *matHeaderCellDef>Month</th>
            <td mat-cell *matCellDef="let c">{{ month(c.period) }}@if (c.prorated) { <span class="muted"> ({{ c.daysCharged }} days)</span> }</td>
          </ng-container>
          <ng-container matColumnDef="due">
            <th mat-header-cell *matHeaderCellDef>Due</th>
            <td mat-cell *matCellDef="let c">{{ c.dueDate | date: 'd MMM y' }}</td>
          </ng-container>
          <ng-container matColumnDef="amount">
            <th mat-header-cell *matHeaderCellDef>Rent</th>
            <td mat-cell *matCellDef="let c">{{ c.amountDue | currency: 'ZAR' : 'symbol-narrow' }}</td>
          </ng-container>
          <ng-container matColumnDef="paid">
            <th mat-header-cell *matHeaderCellDef>Paid</th>
            <td mat-cell *matCellDef="let c">{{ c.paid | currency: 'ZAR' : 'symbol-narrow' }}</td>
          </ng-container>
          <ng-container matColumnDef="state">
            <th mat-header-cell *matHeaderCellDef>Status</th>
            <td mat-cell *matCellDef="let c">
              <span [class]="'state ' + c.state.toLowerCase()">{{ c.state }}</span>
              @if (c.state !== 'Paid') { · {{ c.outstanding | currency: 'ZAR' : 'symbol-narrow' }} owing }
            </td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
          <tr class="mat-row" *matNoDataRow>
            <td class="mat-cell empty" [attr.colspan]="columns.length">No rent has been charged yet.</td>
          </tr>
        </table>
        <p class="muted note">Rent is due on the day shown and becomes overdue {{ l.graceDays }} days later.
          Payments count once your landlord has verified them.</p>

        <h3>Payments</h3>
        <app-payment-table [payments]="payments()" [columns]="paymentColumns" caption="Payments for this lease"
                           emptyText="No proof of payment uploaded yet." (changed)="reload()" />
      }
    </section>
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    h2 { font: var(--mat-sys-title-large); margin: 24px 0 8px; }
    h3 { font: var(--mat-sys-title-medium); margin: 16px 0 4px; }
    .tiles { display: flex; gap: 12px; flex-wrap: wrap; margin: 8px 0 16px; }
    .tile { display: flex; flex-direction: column; padding: 12px 16px; border-radius: 12px; min-width: 160px;
            background: var(--mat-sys-surface-container); }
    .tile.bad { background: var(--mat-sys-error-container); color: var(--mat-sys-on-error-container); }
    .label { font: var(--mat-sys-label-medium); }
    .value { font: var(--mat-sys-title-large); }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .note { font: var(--mat-sys-body-small); }
    .state.paid { color: var(--mat-sys-primary); }
    .state.overdue { color: var(--mat-sys-error); font-weight: 500; }
  `,
})
export class LeaseRent {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly leaseId = input.required<string>();

  protected readonly columns = ['period', 'due', 'amount', 'paid', 'state'];
  protected readonly ledger = signal<Ledger | null>(null);
  protected readonly payments = signal<Payment[]>([]);
  protected readonly error = signal<string | null>(null);
  /** Newest month first, which is what people look for. */
  protected readonly lines = computed(() => [...(this.ledger()?.lines ?? [])].reverse());
  protected readonly paymentColumns = ['paidOn', 'amount', 'period', 'status', 'actions'];

  constructor() {
    effect(() => void this.load(this.leaseId()));
  }

  protected abs(value: number): number {
    return Math.abs(value);
  }

  protected month(period: string): string {
    return monthName(period);
  }

  reload(): void {
    void this.load(this.leaseId());
  }

  private async load(leaseId: string): Promise<void> {
    try {
      const [ledger, payments] = await Promise.all([
        firstValueFrom(this.api.getLedger(leaseId)),
        firstValueFrom(this.api.listPayments({ leaseId, pageSize: 100 })),
      ]);
      this.ledger.set(ledger);
      this.payments.set(payments.items);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected async upload(): Promise<void> {
    const open = (this.ledger()?.lines ?? []).filter((l) => l.outstanding > 0);
    const created = await firstValueFrom(
      this.dialog
        .open<UploadPaymentDialog, UploadPaymentData, Payment>(UploadPaymentDialog, {
          data: { leaseId: this.leaseId(), openLines: open },
        })
        .afterClosed(),
    );
    if (created) {
      this.snack.open('Proof of payment uploaded. It counts once the landlord verifies it.', undefined, { duration: 5000 });
      this.reload();
    }
  }
}

type DepositAction = 'receive' | 'deduct' | 'refund';

interface DepositDialogData {
  action: DepositAction;
  leaseId: string;
  deposit: Deposit;
}

@Component({
  selector: 'app-deposit-dialog',
  imports: [ReactiveFormsModule, CurrencyPipe, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ title }}</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        @if (data.action === 'refund') {
          <p>Refund {{ data.deposit.remaining | currency: 'ZAR' : 'symbol-narrow' }}
             ({{ data.deposit.amountReceived | currency: 'ZAR' : 'symbol-narrow' }} held less
             {{ data.deposit.totalDeducted | currency: 'ZAR' : 'symbol-narrow' }} in deductions).</p>
        } @else {
          <mat-form-field>
            <mat-label>Amount</mat-label>
            <span matTextPrefix>R&nbsp;</span>
            <input matInput type="number" formControlName="amount" min="0.01" step="0.01" required />
          </mat-form-field>
        }
        @if (data.action === 'deduct') {
          <mat-form-field>
            <mat-label>Reason</mat-label>
            <textarea matInput formControlName="reason" rows="2" maxlength="300" required></textarea>
            <mat-error>A reason is required.</mat-error>
          </mat-form-field>
        } @else {
          <mat-form-field>
            <mat-label>{{ data.action === 'receive' ? 'Date received' : 'Date refunded' }}</mat-label>
            <input matInput type="date" formControlName="date" required />
          </mat-form-field>
        }
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy()">Save</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(380px, 80vw); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class DepositDialog {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<DepositDialog, Deposit>);
  protected readonly data = inject<DepositDialogData>(MAT_DIALOG_DATA);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly title =
    this.data.action === 'receive' ? 'Record deposit received' : this.data.action === 'deduct' ? 'Add a deduction' : 'Record refund';
  protected readonly form = inject(FormBuilder).nonNullable.group({
    amount: [this.data.action === 'receive' ? this.data.deposit.amountAgreed : 0, [Validators.min(0.01)]],
    reason: ['', this.data.action === 'deduct' ? Validators.required : []],
    date: [todayIso()],
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
      const call =
        this.data.action === 'receive'
          ? this.api.receiveDeposit(this.data.leaseId, v.amount, v.date)
          : this.data.action === 'deduct'
            ? this.api.addDepositDeduction(this.data.leaseId, v.amount, v.reason)
            : this.api.refundDeposit(this.data.leaseId, v.date);
      this.ref.close(await firstValueFrom(call));
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/** BR13: the deposit, kept apart from rent and shared by every version of the lease. */
@Component({
  selector: 'app-lease-deposit',
  imports: [CurrencyPipe, DatePipe, MatButtonModule],
  template: `
    <section aria-labelledby="deposit-heading">
      <h2 id="deposit-heading">Deposit</h2>
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      @if (deposit(); as d) {
        <dl class="grid">
          <dt>Status</dt><dd>{{ statusLabel(d.status) }}</dd>
          <dt>Agreed</dt><dd>{{ d.amountAgreed | currency: 'ZAR' : 'symbol-narrow' }}</dd>
          @if (d.receivedOn) {
            <dt>Received</dt><dd>{{ d.amountReceived | currency: 'ZAR' : 'symbol-narrow' }} on {{ d.receivedOn | date: 'd MMM y' }}</dd>
          }
          @for (x of d.deductions; track $index) {
            <dt>Deduction</dt><dd>{{ x.amount | currency: 'ZAR' : 'symbol-narrow' }}: {{ x.reason }}</dd>
          }
          @if (d.refundedOn) {
            <dt>Refunded</dt><dd>{{ d.amountRefunded | currency: 'ZAR' : 'symbol-narrow' }} on {{ d.refundedOn | date: 'd MMM y' }}</dd>
          } @else if (d.refundDueBy && d.status === 'Held') {
            <dt>Refund due by</dt><dd>{{ d.refundDueBy | date: 'd MMM y' }} <span class="muted">(setting: legal review needed)</span></dd>
          }
        </dl>
        @if (!d.leaseHasEnded && d.status === 'Held') {
          <p class="muted">Deductions and the refund are recorded after the lease ends.</p>
        }
        <div class="buttons">
          @if (d.allowedActions.includes('receive')) {
            <button mat-stroked-button type="button" (click)="act('receive', d)">Record deposit received</button>
          }
          @if (d.allowedActions.includes('deduct')) {
            <button mat-stroked-button type="button" (click)="act('deduct', d)">Add deduction</button>
          }
          @if (d.allowedActions.includes('refund')) {
            <button mat-flat-button type="button" (click)="act('refund', d)">Record refund</button>
          }
        </div>
      }
    </section>
  `,
  styles: `
    h2 { font: var(--mat-sys-title-large); margin: 24px 0 8px; }
    .grid { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 0 0 8px; }
    dt { font-weight: 500; }
    dd { margin: 0; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .buttons { display: flex; gap: 8px; flex-wrap: wrap; }
    .error { color: var(--mat-sys-error); }
  `,
})
export class LeaseDeposit {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly leaseId = input.required<string>();
  protected readonly deposit = signal<Deposit | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      firstValueFrom(this.api.getDeposit(this.leaseId()))
        .then((d) => this.deposit.set(d))
        .catch((e: unknown) => this.error.set(errorMessage(e)));
    });
  }

  protected statusLabel(status: Deposit['status']): string {
    switch (status) {
      case 'AwaitingPayment':
        return 'Not received yet';
      case 'Held':
        return 'Held';
      case 'PartiallyRefunded':
        return 'Refunded after deductions';
      default:
        return 'Refunded in full';
    }
  }

  protected async act(action: DepositAction, deposit: Deposit): Promise<void> {
    const updated = await firstValueFrom(
      this.dialog
        .open<DepositDialog, DepositDialogData, Deposit>(DepositDialog, {
          data: { action, leaseId: this.leaseId(), deposit },
        })
        .afterClosed(),
    );
    if (updated) {
      this.deposit.set(updated);
      this.snack.open('Deposit updated.', undefined, { duration: 3000 });
    }
  }
}
