import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Payment } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/problem';
import { confirm } from '../../shared/dialogs';
import { monthName, saveBlob } from '../../shared/download';
import { RejectPaymentDialog } from './payment-dialogs';

/** A list of payments with the actions the API allows on each (view, receipt, and for landlords verify/reject). */
@Component({
  selector: 'app-payment-table',
  imports: [CurrencyPipe, DatePipe, RouterLink, MatTableModule, MatButtonModule, MatIconModule],
  template: `
    <table mat-table [dataSource]="payments()" class="table">
      <caption class="visually-hidden">{{ caption() }}</caption>
      <ng-container matColumnDef="paidOn">
        <th mat-header-cell *matHeaderCellDef>Paid on</th>
        <td mat-cell *matCellDef="let p">{{ p.paidOn | date: 'd MMM y' }}</td>
      </ng-container>
      <ng-container matColumnDef="tenant">
        <th mat-header-cell *matHeaderCellDef>Tenant · lease</th>
        <td mat-cell *matCellDef="let p">
          {{ p.tenantName }}<br />
          <a class="muted" [routerLink]="['/leases', p.leaseId]">{{ p.leaseNumber }} v{{ p.leaseVersion }} · {{ p.unitLabel }}</a>
        </td>
      </ng-container>
      <ng-container matColumnDef="amount">
        <th mat-header-cell *matHeaderCellDef>Amount</th>
        <td mat-cell *matCellDef="let p">{{ p.amount | currency: 'ZAR' : 'symbol-narrow' }}</td>
      </ng-container>
      <ng-container matColumnDef="period">
        <th mat-header-cell *matHeaderCellDef>For</th>
        <td mat-cell *matCellDef="let p">{{ p.period ? month(p.period) : 'Oldest unpaid' }}</td>
      </ng-container>
      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef>Status</th>
        <td mat-cell *matCellDef="let p">
          <span [class]="'app-chip ' + statusLook(p.status).tone"><mat-icon aria-hidden="true">{{ statusLook(p.status).icon }}</mat-icon>{{ statusLabel(p.status) }}</span>
          @if (p.rejectionReason) { <br /><span class="muted">{{ p.rejectionReason }}</span> }
          @if (p.receiptNumber) { <br /><span class="muted">Receipt {{ p.receiptNumber }}</span> }
        </td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef><span class="visually-hidden">Actions</span></th>
        <td mat-cell *matCellDef="let p" class="actions">
          <button mat-icon-button type="button" (click)="viewFile(p)" [attr.aria-label]="'Download proof ' + p.fileName">
            <mat-icon>attach_file</mat-icon>
          </button>
          @if (p.allowedActions.includes('downloadReceipt')) {
            <button mat-icon-button type="button" (click)="receipt(p)" [attr.aria-label]="'Download receipt ' + p.receiptNumber">
              <mat-icon>receipt_long</mat-icon>
            </button>
          }
          @if (p.allowedActions.includes('verify')) {
            <button mat-stroked-button type="button" (click)="verify(p)" [disabled]="busy()">Verify</button>
          }
          @if (p.allowedActions.includes('reject')) {
            <button mat-button type="button" (click)="reject(p)" [disabled]="busy()">Reject</button>
          }
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns()"></tr>
      <tr mat-row *matRowDef="let row; columns: columns()"></tr>
      <tr class="mat-row" *matNoDataRow>
        <td class="mat-cell empty" [attr.colspan]="columns().length">{{ emptyText() }}</td>
      </tr>
    </table>
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .muted { color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-small); }
    .actions button { margin-left: 4px; }
  `,
})
export class PaymentTable {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly payments = input.required<Payment[]>();
  readonly columns = input<string[]>(['paidOn', 'tenant', 'amount', 'period', 'status', 'actions']);
  readonly caption = input('Payments');
  readonly emptyText = input('No payments yet.');
  /** Emitted after a payment was verified or rejected, so the parent can reload. */
  readonly changed = output<Payment>();

  protected readonly busy = signal(false);

  protected month(period: string): string {
    return monthName(period);
  }

  protected statusLook(status: Payment['status']): { tone: string; icon: string } {
    switch (status) {
      case 'Verified':
        return { tone: 'success', icon: 'check_circle' };
      case 'Rejected':
        return { tone: 'danger', icon: 'cancel' };
      default:
        return { tone: 'warning', icon: 'hourglass_top' };
    }
  }

  protected statusLabel(status: Payment['status']): string {
    return status === 'Submitted' ? 'Waiting for review' : status;
  }

  protected async viewFile(p: Payment): Promise<void> {
    try {
      saveBlob(await firstValueFrom(this.api.paymentFile(p.id)), p.fileName);
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }

  protected async receipt(p: Payment): Promise<void> {
    try {
      saveBlob(await firstValueFrom(this.api.paymentReceipt(p.id)), `${p.receiptNumber}.pdf`);
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }

  protected async verify(p: Payment): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: 'Verify this payment?',
      message: 'Check the proof against your bank statement first. Verifying applies the payment to the rent and issues a receipt.',
      confirmLabel: 'Verify payment',
    });
    if (!ok) return;
    this.busy.set(true);
    try {
      const verified = await firstValueFrom(this.api.verifyPayment(p.id));
      this.snack.open(`Verified. Receipt ${verified.receiptNumber} issued.`, undefined, { duration: 4000 });
      this.changed.emit(verified);
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    } finally {
      this.busy.set(false);
    }
  }

  protected async reject(p: Payment): Promise<void> {
    const rejected = await firstValueFrom(
      this.dialog.open<RejectPaymentDialog, Payment, Payment>(RejectPaymentDialog, { data: p }).afterClosed(),
    );
    if (rejected) this.changed.emit(rejected);
  }
}
