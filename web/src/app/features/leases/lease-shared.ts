import { Component, computed, input } from '@angular/core';
import { LeaseStatus } from '../../core/api.models';

const LABELS: Record<LeaseStatus, string> = {
  Draft: 'Draft',
  PendingSignature: 'Waiting for signatures',
  Active: 'Active',
  Expiring: 'Expiring',
  Renewed: 'Renewed',
  Ended: 'Ended',
  Terminated: 'Terminated',
};

export function leaseStatusLabel(status: LeaseStatus, isMonthToMonth = false): string {
  return status === 'Active' && isMonthToMonth ? 'Active (month to month)' : LABELS[status];
}

/** Today's date in South Africa as YYYY-MM-DD, matching the API's business dates. */
export function todayIso(): string {
  return new Date().toLocaleDateString('en-CA', { timeZone: 'Africa/Johannesburg' });
}

/** Adds days to a YYYY-MM-DD date without time-zone drift. */
export function addDaysIso(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(Date.UTC(y, m - 1, d + days));
  return date.toISOString().slice(0, 10);
}

/** Same rule as the API (BR06): escalate and round to the nearest rand. */
export function escalatedRent(rent: number, pct: number): number {
  return Math.round(rent * (1 + pct / 100));
}

/** Coloured status label. Colour is never the only signal: the text always says the status. */
@Component({
  selector: 'app-lease-status',
  template: `<span class="chip" [class]="'chip ' + tone()">{{ label() }}</span>`,
  styles: `
    .chip { display: inline-block; padding: 2px 10px; border-radius: 12px; font: var(--mat-sys-label-medium);
            white-space: nowrap; border: 1px solid transparent; }
    .neutral { background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface); }
    .waiting { background: var(--mat-sys-tertiary-container); color: var(--mat-sys-on-tertiary-container); }
    .good { background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .warn { background: var(--mat-sys-error-container); color: var(--mat-sys-on-error-container); }
    .done { background: transparent; color: var(--mat-sys-on-surface-variant); border-color: var(--mat-sys-outline-variant); }
  `,
})
export class LeaseStatusChip {
  readonly status = input.required<LeaseStatus>();
  readonly monthToMonth = input(false);

  protected readonly label = computed(() => leaseStatusLabel(this.status(), this.monthToMonth()));
  protected readonly tone = computed(() => {
    switch (this.status()) {
      case 'Draft':
        return 'neutral';
      case 'PendingSignature':
        return 'waiting';
      case 'Active':
        return 'good';
      case 'Expiring':
        return 'warn';
      default:
        return 'done';
    }
  });
}
