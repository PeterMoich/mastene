import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Lease, LEASE_STATUSES, LeaseStatus, LeaseSummary, PagedResult } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';
import { LeaseForm } from './lease-form';
import { leaseStatusLabel, LeaseStatusChip } from './lease-shared';

/** Landlord: all leases with filters. Tenant: their own leases (drafts are never shown to tenants). */
@Component({
  selector: 'app-lease-list',
  imports: [
    RouterLink, CurrencyPipe, DatePipe, FormsModule, MatTableModule, MatPaginatorModule, MatSortModule,
    MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule,
    LeaseStatusChip,
  ],
  template: `
    <header class="head">
      <h1>{{ isLandlord() ? 'Leases' : 'My leases' }}</h1>
      @if (isLandlord()) {
        <div class="buttons">
          <button mat-stroked-button type="button" (click)="runChecks()" [disabled]="loading()">
            <mat-icon aria-hidden="true">event_repeat</mat-icon> Run daily checks now
          </button>
          <button mat-flat-button type="button" (click)="add()"><mat-icon aria-hidden="true">add</mat-icon> New lease</button>
        </div>
      }
    </header>

    @if (isLandlord()) {
      <div class="filters">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Search lease number or tenant</mat-label>
          <input matInput type="search" (input)="onSearch($any($event.target).value)" />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Status</mat-label>
          <mat-select [(ngModel)]="status" (selectionChange)="reload()">
            <mat-option value="">All statuses</mat-option>
            @for (s of statuses; track s) {
              <mat-option [value]="s">{{ label(s) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>
    }

    @if (loading()) { <mat-progress-bar mode="indeterminate" aria-label="Loading leases" /> }
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

    <table mat-table [dataSource]="page().items" matSort (matSortChange)="onSort($event)" class="table">
      <caption class="visually-hidden">Leases</caption>
      <ng-container matColumnDef="leaseNumber">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>Lease</th>
        <td mat-cell *matCellDef="let l"><a [routerLink]="['/leases', l.id]">{{ l.leaseNumber }} v{{ l.version }}</a></td>
      </ng-container>
      <ng-container matColumnDef="tenantName">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>Tenant</th>
        <td mat-cell *matCellDef="let l">{{ l.tenantName }}</td>
      </ng-container>
      <ng-container matColumnDef="unit">
        <th mat-header-cell *matHeaderCellDef>Property · unit</th>
        <td mat-cell *matCellDef="let l">{{ l.propertyName }} · {{ l.unitLabel }}</td>
      </ng-container>
      <ng-container matColumnDef="startDate">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>Term</th>
        <td mat-cell *matCellDef="let l">{{ l.startDate | date: 'd MMM y' }} to {{ l.endDate | date: 'd MMM y' }}</td>
      </ng-container>
      <ng-container matColumnDef="monthlyRent">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>Rent</th>
        <td mat-cell *matCellDef="let l">{{ l.monthlyRent | currency: 'ZAR' : 'symbol-narrow' }}</td>
      </ng-container>
      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>Status</th>
        <td mat-cell *matCellDef="let l"><app-lease-status [status]="l.status" [monthToMonth]="l.isMonthToMonth" /></td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns()"></tr>
      <tr mat-row *matRowDef="let row; columns: columns()"></tr>
      <tr class="mat-row" *matNoDataRow>
        <td class="mat-cell empty" [attr.colspan]="columns().length">
          {{ loading() ? 'Loading…' : isLandlord() ? 'No leases found.' : 'You have no leases yet.' }}
        </td>
      </tr>
    </table>
    <mat-paginator [length]="page().totalCount" [pageSize]="pageSize" [pageSizeOptions]="[10, 20, 50]"
                   (page)="onPage($event)" aria-label="Choose page of leases" />
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .buttons { display: flex; gap: 8px; flex-wrap: wrap; }
    .filters { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 8px; }
    .filters mat-form-field { min-width: 240px; }
  `,
})
export class LeaseList {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);

  protected readonly isLandlord = computed(() => this.auth.role() === 'Landlord');
  protected readonly columns = computed(() =>
    this.isLandlord()
      ? ['leaseNumber', 'tenantName', 'unit', 'startDate', 'monthlyRent', 'status']
      : ['leaseNumber', 'unit', 'startDate', 'monthlyRent', 'status'],
  );
  protected readonly statuses = LEASE_STATUSES;
  protected readonly page = signal<PagedResult<LeaseSummary>>({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected status: LeaseStatus | '' = '';
  protected pageSize = 20;
  private pageIndex = 0;
  private sort = '-startDate';
  private search = '';
  private searchTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    void this.load();
  }

  protected label(status: LeaseStatus): string {
    return leaseStatusLabel(status);
  }

  protected reload(): void {
    this.pageIndex = 0;
    void this.load();
  }

  protected onSearch(value: string): void {
    clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.search = value;
      this.reload();
    }, 300);
  }

  protected onSort(sort: Sort): void {
    this.sort = sort.direction === 'desc' ? `-${sort.active}` : sort.direction ? sort.active : '-startDate';
    void this.load();
  }

  protected onPage(event: PageEvent): void {
    this.pageIndex = event.pageIndex;
    this.pageSize = event.pageSize;
    void this.load();
  }

  protected async add(): Promise<void> {
    const created = await firstValueFrom(
      this.dialog.open<LeaseForm, null, Lease>(LeaseForm, { data: null, width: '640px' }).afterClosed(),
    );
    if (created) await this.router.navigate(['/leases', created.id]);
  }

  protected async runChecks(): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.runLeaseChecks());
      const changes =
        r.activated + r.markedExpiring + r.noticesRecorded + r.movedToMonthToMonth + r.ended + r.terminated +
        r.chargesRaised + r.overdueNoticesRecorded + r.issuesEscalated + r.issuesAutoClosed + r.remindersQueued;
      this.snack.open(
        changes === 0
          ? 'Daily checks done: nothing needed changing.'
          : `Daily checks done: ${r.activated} started (${r.renewed} renewals), ${r.markedExpiring} expiring, ` +
              `${r.noticesRecorded} expiry notices, ${r.movedToMonthToMonth} month to month, ${r.ended} ended, ` +
              `${r.terminated} terminated, ${r.chargesRaised} rent charges, ${r.overdueNoticesRecorded} overdue reminders, ` +
              `${r.issuesEscalated} issues escalated, ${r.issuesAutoClosed} issues closed automatically, ` +
              `${r.remindersQueued} WhatsApp reminders queued, ${r.messagesSent} messages sent.`,
        'Close',
        { duration: 8000 },
      );
      await this.load();
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.page.set(
        await firstValueFrom(
          this.api.listLeases({
            page: this.pageIndex + 1,
            pageSize: this.pageSize,
            sort: this.sort,
            search: this.search,
            status: this.status,
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
