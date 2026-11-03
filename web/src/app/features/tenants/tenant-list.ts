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
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { PagedResult, Tenant, TenantLoginCreated } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { applyServerErrors, errorMessage } from '../../core/problem';
import { confirm } from '../../shared/dialogs';

@Component({
  selector: 'app-tenant-form',
  imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ existing ? 'Edit tenant' : 'Add tenant' }}</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        <mat-form-field>
          <mat-label>First name</mat-label>
          <input matInput formControlName="firstName" required autocomplete="off" />
          <mat-error>{{ message('firstName') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Last name</mat-label>
          <input matInput formControlName="lastName" required autocomplete="off" />
          <mat-error>{{ message('lastName') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Email</mat-label>
          <input matInput type="email" formControlName="email" required autocomplete="off" />
          <mat-error>{{ message('email') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Mobile number</mat-label>
          <input matInput type="tel" formControlName="phone" required placeholder="082 123 4567" autocomplete="off" />
          <mat-hint>Used for WhatsApp later. South African numbers can start with 0.</mat-hint>
          <mat-error>{{ message('phone') }}</mat-error>
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
    .fields { display: flex; flex-direction: column; min-width: min(400px, 80vw); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class TenantForm {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<TenantForm, Tenant>);
  protected readonly existing = inject<Tenant | null>(MAT_DIALOG_DATA, { optional: true });
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    firstName: [this.existing?.firstName ?? '', Validators.required],
    lastName: [this.existing?.lastName ?? '', Validators.required],
    email: [this.existing?.email ?? '', [Validators.required, Validators.email]],
    phone: [this.existing?.phone ?? '', Validators.required],
  });

  protected message(name: keyof typeof this.form.controls): string {
    const errors = this.form.controls[name].errors;
    if (!errors) return '';
    if (errors['server']) return errors['server'] as string;
    if (errors['email']) return 'Enter a valid email address.';
    return 'This field is required.';
  }

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const body = this.form.getRawValue();
      const saved = await firstValueFrom(
        this.existing ? this.api.updateTenant(this.existing.id, body) : this.api.createTenant(body),
      );
      this.ref.close(saved);
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/** Shows the one-time password once. It cannot be retrieved again. */
@Component({
  selector: 'app-login-created',
  imports: [MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Login created</h2>
    <mat-dialog-content>
      <p>Give these details to the tenant. They can sign in straight away. This password is shown only once.</p>
      <dl>
        <dt>Email</dt><dd>{{ data.email }}</dd>
        <dt>Temporary password</dt><dd><code>{{ data.temporaryPassword }}</code></dd>
      </dl>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="copy()">Copy password</button>
      <button mat-flat-button type="button" mat-dialog-close cdkFocusInitial>Done</button>
    </mat-dialog-actions>
  `,
  styles: `
    dt { font-weight: 500; margin-top: 8px; }
    dd { margin: 0; }
    code { font-size: 1.1em; }
  `,
})
export class LoginCreatedDialog {
  protected readonly data = inject<TenantLoginCreated>(MAT_DIALOG_DATA);
  protected copy(): void {
    void navigator.clipboard?.writeText(this.data.temporaryPassword);
  }
}

@Component({
  selector: 'app-tenant-list',
  imports: [
    MatTableModule, MatPaginatorModule, MatSortModule, MatButtonModule, MatIconModule, MatFormFieldModule,
    MatInputModule, MatProgressBarModule, MatTooltipModule,
  ],
  template: `
    <header class="head">
      <h1>Tenants</h1>
      <button mat-flat-button type="button" (click)="edit(null)"><mat-icon aria-hidden="true">person_add</mat-icon> Add tenant</button>
    </header>

    <mat-form-field class="search" subscriptSizing="dynamic">
      <mat-label>Search name, email or phone</mat-label>
      <input matInput type="search" (input)="onSearch($any($event.target).value)" />
    </mat-form-field>

    @if (loading()) { <mat-progress-bar mode="indeterminate" aria-label="Loading tenants" /> }
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

    <table mat-table [dataSource]="page().items" matSort (matSortChange)="onSort($event)" class="table">
      <caption class="visually-hidden">Tenants</caption>
      <ng-container matColumnDef="lastName">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>Name</th>
        <td mat-cell *matCellDef="let t">{{ t.firstName }} {{ t.lastName }}</td>
      </ng-container>
      <ng-container matColumnDef="email">
        <th mat-header-cell *matHeaderCellDef mat-sort-header>Email</th>
        <td mat-cell *matCellDef="let t">{{ t.email }}</td>
      </ng-container>
      <ng-container matColumnDef="phone">
        <th mat-header-cell *matHeaderCellDef>Phone</th>
        <td mat-cell *matCellDef="let t">{{ t.phone }}</td>
      </ng-container>
      <ng-container matColumnDef="login">
        <th mat-header-cell *matHeaderCellDef>Login</th>
        <td mat-cell *matCellDef="let t">{{ t.hasLogin ? 'Yes' : 'No' }}</td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef><span class="visually-hidden">Actions</span></th>
        <td mat-cell *matCellDef="let t" class="actions">
          @if (!t.hasLogin) {
            <button mat-icon-button type="button" (click)="createLogin(t)" matTooltip="Create login"
                    [attr.aria-label]="'Create login for ' + t.firstName + ' ' + t.lastName">
              <mat-icon>key</mat-icon>
            </button>
          }
          <button mat-icon-button type="button" (click)="edit(t)" [attr.aria-label]="'Edit ' + t.firstName + ' ' + t.lastName">
            <mat-icon>edit</mat-icon>
          </button>
          <button mat-icon-button type="button" (click)="remove(t)" [attr.aria-label]="'Delete ' + t.firstName + ' ' + t.lastName">
            <mat-icon>delete</mat-icon>
          </button>
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns"></tr>
      <tr mat-row *matRowDef="let row; columns: columns"></tr>
      <tr class="mat-row" *matNoDataRow>
        <td class="mat-cell empty" [attr.colspan]="columns.length">{{ loading() ? 'Loading…' : 'No tenants found.' }}</td>
      </tr>
    </table>
    <mat-paginator [length]="page().totalCount" [pageSize]="pageSize" [pageSizeOptions]="[10, 20, 50]"
                   (page)="onPage($event)" aria-label="Choose page of tenants" />
  `,
  styleUrl: '../../shared/list-page.scss',
})
export class TenantList {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  protected readonly columns = ['lastName', 'email', 'phone', 'login', 'actions'];
  protected readonly page = signal<PagedResult<Tenant>>({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected pageSize = 20;
  private pageIndex = 0;
  private sort = 'lastName';
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
    this.sort = sort.direction === 'desc' ? `-${sort.active}` : sort.direction ? sort.active : 'lastName';
    void this.load();
  }

  protected onPage(event: PageEvent): void {
    this.pageIndex = event.pageIndex;
    this.pageSize = event.pageSize;
    void this.load();
  }

  protected async edit(tenant: Tenant | null): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog.open<TenantForm, Tenant | null, Tenant>(TenantForm, { data: tenant }).afterClosed(),
    );
    if (saved) await this.load();
  }

  protected async createLogin(tenant: Tenant): Promise<void> {
    try {
      const login = await firstValueFrom(this.api.createTenantLogin(tenant.id));
      this.dialog.open(LoginCreatedDialog, { data: login, width: '440px' });
      await this.load();
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }

  protected async remove(tenant: Tenant): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: `Delete ${tenant.firstName} ${tenant.lastName}?`,
      message: 'The tenant is hidden from lists and their login is disabled. Their records are kept. ' +
        'Tenants with an active lease or money owing cannot be deleted.',
      confirmLabel: 'Delete tenant',
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.deleteTenant(tenant.id));
      await this.load();
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.page.set(await firstValueFrom(this.api.listTenants({
        page: this.pageIndex + 1, pageSize: this.pageSize, sort: this.sort, search: this.search,
      })));
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
