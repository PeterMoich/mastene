import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Contractor, PagedResult } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { applyServerErrors, errorMessage } from '../../core/problem';
import { confirm } from '../../shared/dialogs';

@Component({
  selector: 'app-contractor-form',
  imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit contractor' : 'Add contractor' }}</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        <mat-form-field>
          <mat-label>Name or business</mat-label>
          <input matInput formControlName="name" required maxlength="120" />
          <mat-error>{{ form.controls.name.errors?.['server'] ?? 'Enter a name.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Trade</mat-label>
          <input matInput formControlName="trade" required maxlength="60" placeholder="e.g. Plumber" />
          <mat-error>{{ form.controls.trade.errors?.['server'] ?? 'Enter their trade.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Phone</mat-label>
          <input matInput formControlName="phone" type="tel" required autocomplete="off" />
          <mat-error>{{ form.controls.phone.errors?.['server'] ?? 'Enter a phone number, e.g. 082 123 4567.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Email (optional)</mat-label>
          <input matInput formControlName="email" type="email" autocomplete="off" />
          <mat-error>{{ form.controls.email.errors?.['server'] ?? 'Enter a valid email address.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Notes (optional)</mat-label>
          <textarea matInput formControlName="notes" rows="2" maxlength="500"></textarea>
        </mat-form-field>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy()">Save</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(440px, 80vw); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class ContractorForm {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<ContractorForm, Contractor>);
  protected readonly data = inject<Contractor | null>(MAT_DIALOG_DATA);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: [this.data?.name ?? '', [Validators.required, Validators.maxLength(120)]],
    trade: [this.data?.trade ?? '', [Validators.required, Validators.maxLength(60)]],
    phone: [this.data?.phone ?? '', Validators.required],
    email: [this.data?.email ?? '', Validators.email],
    notes: [this.data?.notes ?? ''],
  });

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body = { ...v, email: v.email || null, notes: v.notes || null };
    this.busy.set(true);
    this.error.set(null);
    try {
      this.ref.close(
        await firstValueFrom(this.data ? this.api.updateContractor(this.data.id, body) : this.api.createContractor(body)),
      );
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/** The landlord's contractors: plumbers, electricians and so on, to assign to issues. */
@Component({
  selector: 'app-contractor-list',
  imports: [RouterLink, MatTableModule, MatPaginatorModule, MatButtonModule, MatIconModule, MatFormFieldModule,
    MatInputModule, MatProgressBarModule],
  template: `
    <header class="head">
      <h1>Contractors</h1>
      <button mat-flat-button type="button" (click)="edit(null)"><mat-icon aria-hidden="true">add</mat-icon> Add contractor</button>
    </header>
    <mat-form-field class="search" subscriptSizing="dynamic">
      <mat-label>Search name, trade or phone</mat-label>
      <input matInput type="search" (input)="onSearch($any($event.target).value)" />
    </mat-form-field>
    @if (loading()) { <mat-progress-bar mode="indeterminate" aria-label="Loading contractors" /> }
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    <table mat-table [dataSource]="page().items" class="table">
      <caption class="visually-hidden">Contractors</caption>
      <ng-container matColumnDef="name">
        <th mat-header-cell *matHeaderCellDef>Name</th>
        <td mat-cell *matCellDef="let c">{{ c.name }}@if (c.notes) { <div class="muted small">{{ c.notes }}</div> }</td>
      </ng-container>
      <ng-container matColumnDef="trade">
        <th mat-header-cell *matHeaderCellDef>Trade</th>
        <td mat-cell *matCellDef="let c">{{ c.trade }}</td>
      </ng-container>
      <ng-container matColumnDef="contact">
        <th mat-header-cell *matHeaderCellDef>Contact</th>
        <td mat-cell *matCellDef="let c"><a [href]="'tel:' + c.phone">{{ c.phone }}</a>
          @if (c.email) { <div class="small"><a [href]="'mailto:' + c.email">{{ c.email }}</a></div> }</td>
      </ng-container>
      <ng-container matColumnDef="work">
        <th mat-header-cell *matHeaderCellDef>Current work</th>
        <td mat-cell *matCellDef="let c">
          @if (c.unfinishedIssues > 0) {
            <a routerLink="/issues">{{ c.unfinishedIssues }} issue(s) in progress</a>
          } @else { <span class="muted">None</span> }
        </td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef><span class="visually-hidden">Actions</span></th>
        <td mat-cell *matCellDef="let c" class="actions">
          <button mat-icon-button type="button" (click)="edit(c)" [attr.aria-label]="'Edit ' + c.name"><mat-icon>edit</mat-icon></button>
          <button mat-icon-button type="button" (click)="remove(c)" [attr.aria-label]="'Delete ' + c.name"><mat-icon>delete</mat-icon></button>
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns"></tr>
      <tr mat-row *matRowDef="let row; columns: columns"></tr>
      <tr class="mat-row" *matNoDataRow>
        <td class="mat-cell empty" [attr.colspan]="columns.length">{{ loading() ? 'Loading…' : 'No contractors yet.' }}</td>
      </tr>
    </table>
    <mat-paginator [length]="page().totalCount" [pageSize]="pageSize" [pageSizeOptions]="[10, 20, 50]"
                   (page)="onPage($event)" aria-label="Choose page of contractors" />
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .muted { color: var(--mat-sys-on-surface-variant); }
    .small { font: var(--mat-sys-body-small); }
  `,
})
export class ContractorList {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly columns = ['name', 'trade', 'contact', 'work', 'actions'];
  protected readonly page = signal<PagedResult<Contractor>>({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected pageSize = 20;
  private pageIndex = 0;
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

  protected onPage(event: PageEvent): void {
    this.pageIndex = event.pageIndex;
    this.pageSize = event.pageSize;
    void this.load();
  }

  protected async edit(c: Contractor | null): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog.open<ContractorForm, Contractor | null, Contractor>(ContractorForm, { data: c, width: '520px' }).afterClosed(),
    );
    if (saved) await this.load();
  }

  protected async remove(c: Contractor): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: 'Delete contractor?',
      message: `${c.name} will be removed from your list. Issues they worked on keep their name.`,
      confirmLabel: 'Delete',
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.deleteContractor(c.id));
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
        await firstValueFrom(this.api.listContractors({ page: this.pageIndex + 1, pageSize: this.pageSize, search: this.search })),
      );
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
