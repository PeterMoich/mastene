import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { firstValueFrom } from 'rxjs';
import { AnonymiseResult, PrivacyTenant } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/problem';
import { confirm } from '../../shared/dialogs';
import { saveBlob } from '../../shared/download';

/** What a row says about erasure, in words. */
export function erasureText(t: PrivacyTenant, today = new Date().toISOString().slice(0, 10)): string {
  if (t.anonymisedAt) return 'Details erased';
  if (t.allowedActions.includes('anonymise')) return 'Can be erased';
  if (!t.lastLeaseEndedOn) return 'Lease running, or money owed';
  if (t.erasableFrom && t.erasableFrom > today) return `Keep until ${t.erasableFrom}`;
  return 'Lease in force or money owed';
}

/** Admin: POPIA requests. Export a tenant's data, or erase a former tenant's details when the law allows. */
@Component({
  selector: 'app-privacy',
  imports: [DatePipe, MatTableModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h1>Privacy requests</h1>
    <p class="intro">
      A tenant can ask for a copy of their data or, once they've left, for their details to be erased (POPIA).
      Erasure keeps the lease, payment and maintenance records, without the person's details, and is only
      possible after the retention period set in Settings. <strong>Legal review needed:</strong> what must be kept,
      and for how long.
    </p>
    <mat-form-field class="search" subscriptSizing="dynamic">
      <mat-label>Search name or email</mat-label>
      <input matInput type="search" (input)="onSearch($any($event.target).value)" />
    </mat-form-field>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    <table mat-table [dataSource]="rows()" class="table">
      <caption class="visually-hidden">Tenants and their privacy status</caption>
      <ng-container matColumnDef="name">
        <th mat-header-cell *matHeaderCellDef>Tenant</th>
        <td mat-cell *matCellDef="let t">{{ t.name }}<div class="muted small">{{ t.email }}{{ t.isDeleted ? ' · deleted' : '' }}</div></td>
      </ng-container>
      <ng-container matColumnDef="lease">
        <th mat-header-cell *matHeaderCellDef>Last lease ended</th>
        <td mat-cell *matCellDef="let t">{{ t.lastLeaseEndedOn ? (t.lastLeaseEndedOn | date: 'd MMM y') : '–' }}</td>
      </ng-container>
      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef>Erasure</th>
        <td mat-cell *matCellDef="let t">{{ status(t) }}</td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef><span class="visually-hidden">Actions</span></th>
        <td mat-cell *matCellDef="let t" class="actions">
          <button mat-button type="button" (click)="export(t)" [disabled]="busy()">Export data</button>
          @if (t.allowedActions.includes('anonymise')) {
            <button mat-stroked-button type="button" (click)="erase(t)" [disabled]="busy()">Erase details</button>
          }
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns"></tr>
      <tr mat-row *matRowDef="let row; columns: columns"></tr>
      <tr class="mat-row" *matNoDataRow><td class="mat-cell empty" [attr.colspan]="columns.length">No tenants found.</td></tr>
    </table>
    @if (result(); as r) {
      <section class="kept" role="status" aria-labelledby="kept-heading">
        <h2 id="kept-heading">Erased {{ r.anonymisedAt | date: 'd MMM y, HH:mm' }}. Still kept:</h2>
        <ul>@for (k of r.kept; track k) { <li>{{ k }}</li> }</ul>
      </section>
    }
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .intro { max-width: 760px; color: var(--mat-sys-on-surface-variant); }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .small { font: var(--mat-sys-body-small); }
    .kept { margin-top: 16px; padding: 8px 16px; border-radius: 8px; background: var(--mat-sys-surface-container); }
    .kept h2 { font: var(--mat-sys-title-small); }
  `,
})
export class PrivacyPage {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly columns = ['name', 'lease', 'status', 'actions'];
  protected readonly rows = signal<PrivacyTenant[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly result = signal<AnonymiseResult | null>(null);
  private search = '';
  private timer?: ReturnType<typeof setTimeout>;

  constructor() {
    void this.load();
  }

  protected status(t: PrivacyTenant): string {
    return erasureText(t);
  }

  protected onSearch(value: string): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.search = value;
      void this.load();
    }, 300);
  }

  protected async export(t: PrivacyTenant): Promise<void> {
    this.busy.set(true);
    try {
      saveBlob(await firstValueFrom(this.api.exportTenantData(t.id)), `tenant-data-${t.id}.zip`);
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    } finally {
      this.busy.set(false);
    }
  }

  protected async erase(t: PrivacyTenant): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: `Erase ${t.name}'s details?`,
      message:
        'Their name, email, phone, login, WhatsApp messages and comments are replaced for good. ' +
        'Lease, payment and maintenance records stay, without their details. This cannot be undone.',
      confirmLabel: 'Erase',
    });
    if (!ok) return;
    this.busy.set(true);
    try {
      this.result.set(await firstValueFrom(this.api.anonymiseTenant(t.id)));
      await this.load();
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      this.rows.set(await firstValueFrom(this.api.privacyTenants(this.search)));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }
}
