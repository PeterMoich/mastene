import { Component, computed, input } from '@angular/core';
import { IssueEventKind, IssuePriority, IssueStatus } from '../../core/api.models';

const STATUS_LABELS: Record<IssueStatus, string> = {
  Open: 'Open',
  Acknowledged: 'Acknowledged',
  InProgress: 'In progress',
  Resolved: 'Resolved',
  Closed: 'Closed',
};

export function issueStatusLabel(status: IssueStatus): string {
  return STATUS_LABELS[status];
}

const EVENT_LABELS: Record<IssueEventKind, string> = {
  Opened: 'Logged the issue',
  Acknowledged: 'Acknowledged',
  Started: 'Started work',
  ContractorAssigned: 'Assigned a contractor',
  ContractorRemoved: 'Removed the contractor',
  PriorityChanged: 'Changed the priority',
  Resolved: 'Marked it resolved',
  Reopened: 'Reopened it',
  Closed: 'Confirmed the fix and closed it',
  AutoClosed: 'Closed it automatically',
  ResponseTargetMissed: 'Response target missed: escalated',
  ResolutionTargetMissed: 'Resolution target missed: escalated',
};

export function issueEventLabel(kind: IssueEventKind): string {
  return EVENT_LABELS[kind];
}

/** "3 h", "2 d 4 h", "25 min". */
export function formatDuration(ms: number): string {
  const minutes = Math.max(0, Math.round(ms / 60000));
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} h`;
  const days = Math.floor(hours / 24);
  const rest = hours % 24;
  return rest === 0 ? `${days} d` : `${days} d ${rest} h`;
}

/**
 * How a target stands, in words (never colour alone):
 * met on time, met late, still open with time left, or overdue.
 */
export function targetText(due: string, doneAt: string | null, now: Date = new Date()): { text: string; late: boolean } {
  const dueMs = new Date(due).getTime();
  if (doneAt) {
    const doneMs = new Date(doneAt).getTime();
    return doneMs <= dueMs
      ? { text: 'Met on time', late: false }
      : { text: `Met ${formatDuration(doneMs - dueMs)} late`, late: true };
  }
  const left = dueMs - now.getTime();
  return left >= 0
    ? { text: `${formatDuration(left)} left`, late: false }
    : { text: `Overdue by ${formatDuration(-left)}`, late: true };
}

/** Coloured status label; the text always says the status. */
@Component({
  selector: 'app-issue-status',
  template: `<span [class]="'chip ' + tone()">{{ label() }}</span>`,
  styles: `
    .chip { display: inline-block; padding: 2px 10px; border-radius: 12px; font: var(--mat-sys-label-medium);
            white-space: nowrap; border: 1px solid transparent; }
    .open { background: var(--mat-sys-tertiary-container); color: var(--mat-sys-on-tertiary-container); }
    .active { background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .resolved { background: var(--mat-sys-secondary-container); color: var(--mat-sys-on-secondary-container); }
    .done { background: transparent; color: var(--mat-sys-on-surface-variant); border-color: var(--mat-sys-outline-variant); }
  `,
})
export class IssueStatusChip {
  readonly status = input.required<IssueStatus>();
  protected readonly label = computed(() => issueStatusLabel(this.status()));
  protected readonly tone = computed(() => {
    switch (this.status()) {
      case 'Open':
        return 'open';
      case 'Acknowledged':
      case 'InProgress':
        return 'active';
      case 'Resolved':
        return 'resolved';
      default:
        return 'done';
    }
  });
}

/** Priority label with an icon for the two most urgent levels. */
@Component({
  selector: 'app-issue-priority',
  template: `<span [class]="'prio ' + priority().toLowerCase()">{{ priority() }}</span>`,
  styles: `
    .prio { font: var(--mat-sys-label-large); white-space: nowrap; }
    .urgent { color: var(--mat-sys-error); font-weight: 700; }
    .urgent::before { content: '▲▲ '; font-size: 0.7em; }
    .high { color: var(--mat-sys-error); }
    .high::before { content: '▲ '; font-size: 0.7em; }
    .medium, .low { color: var(--mat-sys-on-surface-variant); }
  `,
})
export class IssuePriorityLabel {
  readonly priority = input.required<IssuePriority>();
}

/** Escalation badge (BR15). */
@Component({
  selector: 'app-escalated',
  template: `<span class="badge" title="A response or resolution target was missed">Escalated</span>`,
  styles: `
    .badge { display: inline-block; padding: 1px 8px; border-radius: 10px; font: var(--mat-sys-label-small);
             background: var(--mat-sys-error); color: var(--mat-sys-on-error); white-space: nowrap; }
  `,
})
export class EscalatedBadge {}
