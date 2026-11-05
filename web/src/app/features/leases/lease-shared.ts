import { Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
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

/** Status chip: words, an icon and a colour, so the colour is never the only signal. */
@Component({
  selector: 'app-lease-status',
  imports: [MatIconModule],
  template: `<span [class]="'app-chip ' + look().tone"><mat-icon aria-hidden="true">{{ look().icon }}</mat-icon>{{ label() }}</span>`,
})
export class LeaseStatusChip {
  readonly status = input.required<LeaseStatus>();
  readonly monthToMonth = input(false);

  protected readonly label = computed(() => leaseStatusLabel(this.status(), this.monthToMonth()));
  protected readonly look = computed(() => {
    switch (this.status()) {
      case 'Draft':
        return { tone: 'neutral', icon: 'edit_note' };
      case 'PendingSignature':
        return { tone: 'info', icon: 'draw' };
      case 'Active':
        return { tone: 'success', icon: 'check_circle' };
      case 'Expiring':
        return { tone: 'warning', icon: 'schedule' };
      default:
        return { tone: 'muted', icon: 'archive' };
    }
  });
}
