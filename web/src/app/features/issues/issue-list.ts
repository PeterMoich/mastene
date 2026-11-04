import { DatePipe, DecimalPipe, NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  ISSUE_PRIORITIES,
  ISSUE_STATUSES,
  IssuePriority,
  IssueStatus,
  IssueSummary,
  PagedResult,
  PropertySummary,
  SlaReport,
} from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';
import { NewIssueDialog, NewIssueResult } from './issue-dialogs';
import { EscalatedBadge, IssuePriorityLabel, IssueStatusChip, issueStatusLabel, targetText } from './issue-shared';

/** The next target an issue is working towards, in words. */
export function nextTarget(i: IssueSummary, now: Date = new Date()): { text: string; late: boolean } | null {
  if (i.status === 'Open') {
    const t = targetText(i.responseDueAt, null, now);
    return { text: `Respond: ${t.text}`, late: t.late };
  }
  if (i.status === 'Acknowledged' || i.status === 'InProgress') {
    const t = targetText(i.resolutionDueAt, null, now);
    return { text: `Resolve: ${t.text}`, late: t.late };
  }
  return null;
}

/** BR15: how each priority did against its targets. */
@Component({
  selector: 'app-sla-report',
  imports: [DecimalPipe, DatePipe, MatTableModule, MatFormFieldModule, MatSelectModule, FormsModule, IssuePriorityLabel],
  template: `
    <div class="bar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Issues opened in the last</mat-label>
        <mat-select [(ngModel)]="days" (selectionChange)="load()">
          <mat-option [value]="30">30 days</mat-option>
          <mat-option [value]="90">90 days</mat-option>
          <mat-option [value]="365">12 months</mat-option>
        </mat-select>
      </mat-form-field>
      @if (report(); as r) { <span class="muted">{{ r.from | date: 'd MMM y' }} to {{ r.to | date: 'd MMM y' }}</span> }
    </div>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    <table mat-table [dataSource]="report()?.priorities ?? []" class="table">
      <caption class="visually-hidden">Targets met per priority</caption>
      <ng-container matColumnDef="priority">
        <th mat-header-cell *matHeaderCellDef>Priority</th>
        <td mat-cell *matCellDef="let p"><app-issue-priority [priority]="p.priority" /></td>
      </ng-container>
      <ng-container matColumnDef="targets">
        <th mat-header-cell *matHeaderCellDef>Targets (respond / resolve)</th>
        <td mat-cell *matCellDef="let p">{{ p.responseTargetHours }} h / {{ p.resolutionTargetHours }} h</td>
      </ng-container>
      <ng-container matColumnDef="opened">
        <th mat-header-cell *matHeaderCellDef>Opened</th>
        <td mat-cell *matCellDef="let p">{{ p.opened }}</td>
      </ng-container>
      <ng-container matColumnDef="response">
        <th mat-header-cell *matHeaderCellDef>Responded on time</th>
        <td mat-cell *matCellDef="let p">{{ rate(p.acknowledgedOnTime, p.acknowledged) }}
          @if (p.averageResponseHours !== null) { <span class="muted"> · avg {{ p.averageResponseHours | number: '1.0-1' }} h</span> }</td>
      </ng-container>
      <ng-container matColumnDef="resolution">
        <th mat-header-cell *matHeaderCellDef>Resolved on time</th>
        <td mat-cell *matCellDef="let p">{{ rate(p.resolvedOnTime, p.resolved) }}
          @if (p.averageResolutionHours !== null) { <span class="muted"> · avg {{ p.averageResolutionHours | number: '1.0-1' }} h</span> }</td>
      </ng-container>
      <ng-container matColumnDef="escalated">
        <th mat-header-cell *matHeaderCellDef>Escalated</th>
        <td mat-cell *matCellDef="let p">{{ p.escalated }}</td>
      </ng-container>
      <ng-container matColumnDef="open">
        <th mat-header-cell *matHeaderCellDef>Still being worked on</th>
        <td mat-cell *matCellDef="let p">{{ p.stillOpen }}</td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns"></tr>
      <tr mat-row *matRowDef="let row; columns: columns"></tr>
    </table>
    <p class="muted note">Targets are counted in calendar hours from when the issue was logged. Change them under Settings (Admin).</p>
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .bar { display: flex; align-items: center; gap: 16px; margin: 12px 0; flex-wrap: wrap; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .note { margin-top: 12px; }
  `,
})
export class SlaReportView {
  private readonly api = inject(ApiService);
  protected readonly report = signal<SlaReport | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly columns = ['priority', 'targets', 'opened', 'response', 'resolution', 'escalated', 'open'];
  protected days = 90;

  constructor() {
    void this.load();
  }

  protected rate(onTime: number, total: number): string {
    return total === 0 ? '–' : `${onTime} of ${total} (${Math.round((onTime / total) * 100)}%)`;
  }

  async load(): Promise<void> {
    try {
      this.report.set(await firstValueFrom(this.api.slaReport(this.days)));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }
}

/** Landlord: every issue, with filters and the SLA report. Tenant: the issues they logged or that concern their home. */
@Component({
  selector: 'app-issue-list',
  imports: [
    RouterLink, DatePipe, NgTemplateOutlet, FormsModule, MatTableModule, MatPaginatorModule, MatSortModule, MatButtonModule, MatIconModule,
    MatFormFieldModule, MatSelectModule, MatCheckboxModule, MatProgressBarModule, MatTabsModule, IssueStatusChip,
    IssuePriorityLabel, EscalatedBadge, SlaReportView,
  ],
  template: `
    <header class="head">
      <h1>{{ isLandlord() ? 'Issues' : 'My issues' }}</h1>
      <button mat-flat-button type="button" (click)="report()"><mat-icon aria-hidden="true">add</mat-icon> Report an issue</button>
    </header>

    <ng-template #list>
      <div class="filters">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Status</mat-label>
          <mat-select [(ngModel)]="status" (selectionChange)="reload()">
            <mat-option value="">Any status</mat-option>
            @for (s of statuses; track s) { <mat-option [value]="s">{{ statusLabel(s) }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Priority</mat-label>
          <mat-select [(ngModel)]="priority" (selectionChange)="reload()">
            <mat-option value="">Any priority</mat-option>
            @for (p of priorities; track p) { <mat-option [value]="p">{{ p }}</mat-option> }
          </mat-select>
        </mat-form-field>
        @if (isLandlord()) {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Property</mat-label>
            <mat-select [(ngModel)]="propertyId" (selectionChange)="reload()">
              <mat-option value="">All properties</mat-option>
              @for (p of properties(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-checkbox [(ngModel)]="escalatedOnly" (change)="reload()">Escalated only</mat-checkbox>
        }
        <mat-checkbox [(ngModel)]="hideClosed" (change)="reload()">Hide closed</mat-checkbox>
      </div>

      @if (loading()) { <mat-progress-bar mode="indeterminate" aria-label="Loading issues" /> }
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

      <table mat-table [dataSource]="page().items" matSort (matSortChange)="onSort($event)" class="table">
        <caption class="visually-hidden">Issues</caption>
        <ng-container matColumnDef="title">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Issue</th>
          <td mat-cell *matCellDef="let i">
            <a [routerLink]="['/issues', i.id]">{{ i.title }}</a>
            @if (i.isEscalated) { <app-escalated /> }
            <div class="muted small">{{ i.category }} · logged {{ i.openedAt | date: 'd MMM, HH:mm' }}</div>
          </td>
        </ng-container>
        <ng-container matColumnDef="unit">
          <th mat-header-cell *matHeaderCellDef>Where</th>
          <td mat-cell *matCellDef="let i">{{ i.unitLabel }}, {{ i.propertyName }}
            @if (isLandlord()) { <div class="muted small">{{ i.tenantName ?? 'No tenant' }}</div> }</td>
        </ng-container>
        <ng-container matColumnDef="priority">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Priority</th>
          <td mat-cell *matCellDef="let i"><app-issue-priority [priority]="i.priority" /></td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Status</th>
          <td mat-cell *matCellDef="let i"><app-issue-status [status]="i.status" /></td>
        </ng-container>
        <ng-container matColumnDef="target">
          <th mat-header-cell *matHeaderCellDef mat-sort-header="resolutionDueAt">Next target</th>
          <td mat-cell *matCellDef="let i">
            @if (target(i); as t) { <span [class.late]="t.late">{{ t.text }}</span> } @else { <span class="muted">–</span> }
          </td>
        </ng-container>
        <ng-container matColumnDef="contractor">
          <th mat-header-cell *matHeaderCellDef>Contractor</th>
          <td mat-cell *matCellDef="let i">{{ i.contractorName ?? '–' }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns()"></tr>
        <tr mat-row *matRowDef="let row; columns: columns()"></tr>
        <tr class="mat-row" *matNoDataRow>
          <td class="mat-cell empty" [attr.colspan]="columns().length">
            {{ loading() ? 'Loading…' : isLandlord() ? 'No issues match these filters.' : 'You have not reported any issues.' }}
          </td>
        </tr>
      </table>
      <mat-paginator [length]="page().totalCount" [pageSize]="pageSize" [pageSizeOptions]="[10, 20, 50]"
                     (page)="onPage($event)" aria-label="Choose page of issues" />
    </ng-template>

    @if (isLandlord()) {
      <mat-tab-group mat-stretch-tabs="false" animationDuration="0ms">
        <mat-tab label="Issues"><ng-container [ngTemplateOutlet]="list" /></mat-tab>
        <mat-tab label="SLA report">
          <ng-template matTabContent><app-sla-report /></ng-template>
        </mat-tab>
      </mat-tab-group>
    } @else {
      <ng-container [ngTemplateOutlet]="list" />
    }
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .filters { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; margin: 12px 0 8px; }
    .filters mat-form-field { min-width: 180px; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .small { font: var(--mat-sys-body-small); }
    .late { color: var(--mat-sys-error); font-weight: 500; }
    app-escalated { margin-left: 6px; }
  `,
})
export class IssueList {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);

  protected readonly isLandlord = computed(() => this.auth.role() === 'Landlord');
  protected readonly columns = computed(() =>
    this.isLandlord()
      ? ['title', 'unit', 'priority', 'status', 'target', 'contractor']
      : ['title', 'unit', 'priority', 'status', 'contractor'],
  );
  protected readonly statuses = ISSUE_STATUSES;
  protected readonly priorities = ISSUE_PRIORITIES;
  protected readonly page = signal<PagedResult<IssueSummary>>({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  protected readonly properties = signal<PropertySummary[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected status: IssueStatus | '' = '';
  protected priority: IssuePriority | '' = '';
  protected propertyId = '';
  protected escalatedOnly = false;
  protected hideClosed = true;
  protected pageSize = 20;
  private pageIndex = 0;
  private sort = '-openedAt';

  constructor() {
    void this.load();
    if (this.isLandlord())
      void firstValueFrom(this.api.listProperties({ pageSize: 100, sort: 'name' })).then((p) => this.properties.set(p.items));
  }

  protected statusLabel(s: IssueStatus): string {
    return issueStatusLabel(s);
  }

  protected target(i: IssueSummary) {
    return nextTarget(i);
  }

  protected reload(): void {
    this.pageIndex = 0;
    void this.load();
  }

  protected onSort(sort: Sort): void {
    this.sort = sort.direction === 'desc' ? `-${sort.active}` : sort.direction ? sort.active : '-openedAt';
    void this.load();
  }

  protected onPage(event: PageEvent): void {
    this.pageIndex = event.pageIndex;
    this.pageSize = event.pageSize;
    void this.load();
  }

  protected async report(): Promise<void> {
    const result = await firstValueFrom(
      this.dialog.open<NewIssueDialog, null, NewIssueResult>(NewIssueDialog, { data: null, width: '640px' }).afterClosed(),
    );
    if (!result) return;
    if (result.photoProblems.length)
      this.snack.open(`Issue logged, but some photos weren't added: ${result.photoProblems.join('; ')}`, 'Close');
    await this.router.navigate(['/issues', result.issue.id]);
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.page.set(
        await firstValueFrom(
          this.api.listIssues({
            page: this.pageIndex + 1,
            pageSize: this.pageSize,
            sort: this.sort,
            status: this.status,
            priority: this.priority,
            propertyId: this.propertyId,
            escalated: this.escalatedOnly ? true : '',
            openOnly: this.hideClosed && this.status === '' ? true : '',
          }),
        ),
      );
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
