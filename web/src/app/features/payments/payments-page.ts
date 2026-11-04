import { CurrencyPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Arrears, PagedResult, Payment } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/problem';
import { monthName } from '../../shared/download';
import { PaymentTable } from './payment-table';

/** Landlord: proofs waiting for review, all payments, and who is in arrears. */
@Component({
  selector: 'app-payments-page',
  imports: [RouterLink, CurrencyPipe, MatTabsModule, MatTableModule, MatPaginatorModule, MatProgressBarModule, PaymentTable],
  template: `
    <h1>Payments</h1>
    @if (loading()) { <mat-progress-bar mode="indeterminate" aria-label="Loading payments" /> }
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    <mat-tab-group (selectedIndexChange)="tab.set($event)" animationDuration="0ms">
      <mat-tab [label]="'To review (' + review().totalCount + ')'">
        <app-payment-table [payments]="review().items" caption="Proof of payment waiting for review"
                           emptyText="Nothing to review." (changed)="reload()" />
      </mat-tab>
      <mat-tab label="All payments">
        <app-payment-table [payments]="all().items" caption="All payments" (changed)="reload()" />
        <mat-paginator [length]="all().totalCount" [pageSize]="20" [pageSizeOptions]="[20, 50, 100]"
                       (page)="onPage($event)" aria-label="Choose page of payments" />
      </mat-tab>
      <mat-tab [label]="'Arrears (' + arrears().length + ')'">
        <table mat-table [dataSource]="arrears()" class="table">
          <caption class="visually-hidden">Leases with overdue rent</caption>
          <ng-container matColumnDef="tenant">
            <th mat-header-cell *matHeaderCellDef>Tenant</th>
            <td mat-cell *matCellDef="let a">{{ a.tenantName }}</td>
          </ng-container>
          <ng-container matColumnDef="lease">
            <th mat-header-cell *matHeaderCellDef>Lease · unit</th>
            <td mat-cell *matCellDef="let a">
              <a [routerLink]="['/leases', a.leaseId]">{{ a.leaseNumber }} v{{ a.version }}</a> · {{ a.propertyName }}, {{ a.unitLabel }}
            </td>
          </ng-container>
          <ng-container matColumnDef="arrears">
            <th mat-header-cell *matHeaderCellDef>Overdue</th>
            <td mat-cell *matCellDef="let a">{{ a.arrears | currency: 'ZAR' : 'symbol-narrow' }}</td>
          </ng-container>
          <ng-container matColumnDef="oldest">
            <th mat-header-cell *matHeaderCellDef>Oldest unpaid</th>
            <td mat-cell *matCellDef="let a">{{ month(a.oldestOverduePeriod) }} ({{ a.daysOverdue }} days)</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="arrearsColumns"></tr>
          <tr mat-row *matRowDef="let row; columns: arrearsColumns"></tr>
          <tr class="mat-row" *matNoDataRow>
            <td class="mat-cell empty" [attr.colspan]="arrearsColumns.length">No one is in arrears.</td>
          </tr>
        </table>
      </mat-tab>
    </mat-tab-group>
  `,
  styleUrl: '../../shared/list-page.scss',
})
export class PaymentsPage {
  private readonly api = inject(ApiService);

  protected readonly arrearsColumns = ['tenant', 'lease', 'arrears', 'oldest'];
  protected readonly tab = signal(0);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly review = signal<PagedResult<Payment>>({ items: [], page: 1, pageSize: 100, totalCount: 0 });
  protected readonly all = signal<PagedResult<Payment>>({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  protected readonly arrears = signal<Arrears[]>([]);
  private page = { page: 1, pageSize: 20 };

  constructor() {
    this.reload();
  }

  protected month(period: string): string {
    return monthName(period);
  }

  protected onPage(event: PageEvent): void {
    this.page = { page: event.pageIndex + 1, pageSize: event.pageSize };
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    Promise.all([
      firstValueFrom(this.api.listPayments({ status: 'Submitted', pageSize: 100, sort: 'createdAt' })),
      firstValueFrom(this.api.listPayments({ ...this.page, sort: '-createdAt' })),
      firstValueFrom(this.api.listArrears()),
    ])
      .then(([review, all, arrears]) => {
        this.review.set(review);
        this.all.set(all);
        this.arrears.set(arrears);
      })
      .catch((e: unknown) => this.error.set(errorMessage(e)))
      .finally(() => this.loading.set(false));
  }
}
