import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { firstValueFrom } from 'rxjs';
import { PagedResult, User } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { applyServerErrors, errorMessage } from '../../core/problem';

@Component({
  selector: 'app-user-form',
  imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Add staff user</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        <mat-form-field>
          <mat-label>Name</mat-label>
          <input matInput formControlName="displayName" required />
          <mat-error>{{ form.controls.displayName.errors?.['server'] ?? 'Name is required.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Email</mat-label>
          <input matInput type="email" formControlName="email" required autocomplete="off" />
          <mat-error>{{ form.controls.email.errors?.['server'] ?? 'Enter a valid email address.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Role</mat-label>
          <mat-select formControlName="role">
            <mat-option value="Landlord">Landlord</mat-option>
            <mat-option value="Admin">Admin</mat-option>
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Initial password</mat-label>
          <input matInput type="password" formControlName="password" required autocomplete="new-password" />
          <mat-hint>At least 12 characters, with upper and lower case letters and a number.</mat-hint>
          <mat-error>{{ form.controls.password.errors?.['server'] ?? 'At least 12 characters.' }}</mat-error>
        </mat-form-field>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy()">Create</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(400px, 80vw); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class UserForm {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<UserForm, User>);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    displayName: ['', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    role: ['Landlord' as 'Admin' | 'Landlord'],
    password: ['', [Validators.required, Validators.minLength(12)]],
  });

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      this.ref.close(await firstValueFrom(this.api.createUser(this.form.getRawValue())));
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'app-user-list',
  imports: [DatePipe, MatTableModule, MatPaginatorModule, MatButtonModule, MatIconModule],
  template: `
    <header class="head">
      <h1>Users</h1>
      <button mat-flat-button type="button" (click)="add()"><mat-icon aria-hidden="true">person_add</mat-icon> Add staff user</button>
    </header>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    <table mat-table [dataSource]="page().items" class="table">
      <caption class="visually-hidden">User accounts</caption>
      <ng-container matColumnDef="name">
        <th mat-header-cell *matHeaderCellDef>Name</th>
        <td mat-cell *matCellDef="let u">{{ u.displayName }}</td>
      </ng-container>
      <ng-container matColumnDef="email">
        <th mat-header-cell *matHeaderCellDef>Email</th>
        <td mat-cell *matCellDef="let u">{{ u.email }}</td>
      </ng-container>
      <ng-container matColumnDef="role">
        <th mat-header-cell *matHeaderCellDef>Role</th>
        <td mat-cell *matCellDef="let u">{{ u.role }}</td>
      </ng-container>
      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef>Status</th>
        <td mat-cell *matCellDef="let u">{{ u.isLocked ? 'Locked' : 'Active' }}</td>
      </ng-container>
      <ng-container matColumnDef="created">
        <th mat-header-cell *matHeaderCellDef>Created</th>
        <td mat-cell *matCellDef="let u">{{ u.createdAt | date: 'd MMM y' }}</td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef><span class="visually-hidden">Actions</span></th>
        <td mat-cell *matCellDef="let u" class="actions">
          @if (u.id !== myId()) {
            <button mat-button type="button" (click)="toggleLock(u)">{{ u.isLocked ? 'Unlock' : 'Lock' }}</button>
          }
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns"></tr>
      <tr mat-row *matRowDef="let row; columns: columns"></tr>
    </table>
    <mat-paginator [length]="page().totalCount" [pageSize]="20" [pageSizeOptions]="[20, 50]"
                   (page)="onPage($event)" aria-label="Choose page of users" />
  `,
  styleUrl: '../../shared/list-page.scss',
})
export class UserList {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly auth = inject(AuthService);
  protected readonly myId = computed(() => this.auth.user()?.id);

  protected readonly columns = ['name', 'email', 'role', 'status', 'created', 'actions'];
  protected readonly page = signal<PagedResult<User>>({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  protected readonly error = signal<string | null>(null);
  private query = { page: 1, pageSize: 20 };

  constructor() {
    void this.load();
  }

  protected onPage(event: PageEvent): void {
    this.query = { page: event.pageIndex + 1, pageSize: event.pageSize };
    void this.load();
  }

  protected async add(): Promise<void> {
    const created = await firstValueFrom(this.dialog.open<UserForm, void, User>(UserForm).afterClosed());
    if (created) await this.load();
  }

  protected async toggleLock(user: User): Promise<void> {
    try {
      await firstValueFrom(this.api.setUserLocked(user.id, !user.isLocked));
      await this.load();
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }

  private async load(): Promise<void> {
    try {
      this.page.set(await firstValueFrom(this.api.listUsers(this.query)));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }
}
