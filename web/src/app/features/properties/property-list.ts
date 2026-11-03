import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { PagedResult, Property, PropertySummary } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/problem';
import { PropertyForm } from './property-form';

@Component({
  selector: 'app-property-list',
  imports: [
    RouterLink, MatTableModule, MatPaginatorModule, MatSortModule, MatButtonModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatProgressBarModule,
  ],
  template: `
    <header class="head">
      <h1>Properties</h1>
      <button mat-flat-button type="button" (click)="add()"><mat-icon aria-hidden="true">add</mat-icon> Add property</button>
    </header>

    <mat-form-field class="search" subscriptSizing="dynamic">
      <mat-label>Search by name or city</mat-label>
      <input matInput type="search" (input)="onSearch($any($event.target).value)" />
    </mat-form-field>

    @if (loading()) { <mat-progress-bar mode="indeterminate" aria-label="Loading properties" /> }
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

    <table mat-table [dataSource]="page().items" matSort (matSortChange)="onSort($event)" class="table">
      <caption class="visually-hidden">Your properties</caption>
      <ng-container matColumnDef="name">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>Name</th>
        <td mat-cell *matCellDef="let p"><a [routerLink]="['/properties', p.id]">{{ p.name }}</a></td>
      </ng-container>
      <ng-container matColumnDef="city">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>City</th>
        <td mat-cell *matCellDef="let p">{{ p.city }}, {{ p.province }}</td>
      </ng-container>
      <ng-container matColumnDef="units">
        <th mat-header-cell *matHeaderCellDef>Units</th>
        <td mat-cell *matCellDef="let p">{{ p.unitCount }}</td>
      </ng-container>
      <ng-container matColumnDef="occupied">
        <th mat-header-cell *matHeaderCellDef>Occupied</th>
        <td mat-cell *matCellDef="let p">{{ p.occupiedCount }} of {{ p.unitCount }}</td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns"></tr>
      <tr mat-row *matRowDef="let row; columns: columns"></tr>
      <tr class="mat-row" *matNoDataRow>
        <td class="mat-cell empty" [attr.colspan]="columns.length">
          {{ loading() ? 'Loading…' : 'No properties yet. Add your first one.' }}
        </td>
      </tr>
    </table>
    <mat-paginator [length]="page().totalCount" [pageSize]="pageSize" [pageSizeOptions]="[10, 20, 50]"
                   (page)="onPage($event)" aria-label="Choose page of properties" />
  `,
  styleUrl: '../../shared/list-page.scss',
})
export class PropertyList {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);

  protected readonly columns = ['name', 'city', 'units', 'occupied'];
  protected readonly page = signal<PagedResult<PropertySummary>>({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected pageSize = 20;
  private pageIndex = 0;
  private sort = 'name';
  private search = '';
  private searchTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    void this.load();
  }

  protected onSearch(value: string): void {
    clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.search = value;
      this.pageIndex = 0;
      void this.load();
    }, 300);
  }

  protected onSort(sort: Sort): void {
    this.sort = sort.direction === 'desc' ? `-${sort.active}` : sort.direction ? sort.active : 'name';
    void this.load();
  }

  protected onPage(event: PageEvent): void {
    this.pageIndex = event.pageIndex;
    this.pageSize = event.pageSize;
    void this.load();
  }

  protected async add(): Promise<void> {
    const created = await firstValueFrom(
      this.dialog.open<PropertyForm, null, Property>(PropertyForm, { width: '560px' }).afterClosed(),
    );
    if (created) await this.router.navigate(['/properties', created.id]);
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.page.set(await firstValueFrom(this.api.listProperties({
        page: this.pageIndex + 1, pageSize: this.pageSize, sort: this.sort, search: this.search,
      })));
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
