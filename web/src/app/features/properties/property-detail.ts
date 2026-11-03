import { CurrencyPipe } from '@angular/common';
import { Component, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Property, Unit } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { applyServerErrors, errorMessage } from '../../core/problem';
import { confirm } from '../../shared/dialogs';
import { PropertyForm } from './property-form';

interface UnitFormData {
  propertyId: string;
  unit: Unit | null;
}

@Component({
  selector: 'app-unit-form',
  imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ data.unit ? 'Edit unit' : 'Add unit' }}</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        <mat-form-field>
          <mat-label>Label</mat-label>
          <input matInput formControlName="label" required maxlength="40" placeholder="e.g. Flat 3" />
          <mat-error>{{ form.controls.label.errors?.['server'] ?? 'Label is required.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Monthly rent</mat-label>
          <span matTextPrefix>R&nbsp;</span>
          <input matInput type="number" formControlName="rentAmount" required min="0.01" step="0.01" />
          <mat-error>{{ form.controls.rentAmount.errors?.['server'] ?? 'Rent must be more than R0.' }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Deposit</mat-label>
          <span matTextPrefix>R&nbsp;</span>
          <input matInput type="number" formControlName="depositAmount" required min="0" step="0.01" />
          <mat-error>{{ form.controls.depositAmount.errors?.['server'] ?? 'Deposit cannot be negative.' }}</mat-error>
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
    .fields { display: flex; flex-direction: column; min-width: min(360px, 80vw); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class UnitForm {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<UnitForm, Unit>);
  protected readonly data = inject<UnitFormData>(MAT_DIALOG_DATA);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    label: [this.data.unit?.label ?? '', [Validators.required, Validators.maxLength(40)]],
    rentAmount: [this.data.unit?.rentAmount ?? 0, [Validators.required, Validators.min(0.01)]],
    depositAmount: [this.data.unit?.depositAmount ?? 0, [Validators.required, Validators.min(0)]],
  });

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const body = this.form.getRawValue();
    try {
      const saved = await firstValueFrom(
        this.data.unit ? this.api.updateUnit(this.data.unit.id, body) : this.api.addUnit(this.data.propertyId, body),
      );
      this.ref.close(saved);
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'app-property-detail',
  imports: [RouterLink, CurrencyPipe, MatTableModule, MatButtonModule, MatIconModule],
  template: `
    <a mat-button routerLink="/properties"><mat-icon aria-hidden="true">arrow_back</mat-icon> All properties</a>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    @if (property(); as p) {
      <header class="head">
        <div>
          <h1>{{ p.name }}</h1>
          <p class="address">
            {{ p.addressLine1 }}@if (p.addressLine2) {, {{ p.addressLine2 }}}, {{ p.city }}, {{ p.province }}, {{ p.postalCode }}
          </p>
        </div>
        <div class="buttons">
          <button mat-stroked-button type="button" (click)="edit(p)"><mat-icon aria-hidden="true">edit</mat-icon> Edit</button>
          <button mat-stroked-button type="button" (click)="remove(p)"><mat-icon aria-hidden="true">delete</mat-icon> Delete</button>
        </div>
      </header>

      <div class="head">
        <h2>Units</h2>
        <button mat-flat-button type="button" (click)="editUnit(p, null)"><mat-icon aria-hidden="true">add</mat-icon> Add unit</button>
      </div>
      <table mat-table [dataSource]="p.units" class="table">
        <caption class="visually-hidden">Units at {{ p.name }}</caption>
        <ng-container matColumnDef="label">
          <th mat-header-cell *matHeaderCellDef>Unit</th>
          <td mat-cell *matCellDef="let u">{{ u.label }}</td>
        </ng-container>
        <ng-container matColumnDef="rent">
          <th mat-header-cell *matHeaderCellDef>Rent</th>
          <td mat-cell *matCellDef="let u">{{ u.rentAmount | currency: 'ZAR' : 'symbol-narrow' }}</td>
        </ng-container>
        <ng-container matColumnDef="deposit">
          <th mat-header-cell *matHeaderCellDef>Deposit</th>
          <td mat-cell *matCellDef="let u">{{ u.depositAmount | currency: 'ZAR' : 'symbol-narrow' }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>Status</th>
          <td mat-cell *matCellDef="let u">{{ u.status }}</td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef><span class="visually-hidden">Actions</span></th>
          <td mat-cell *matCellDef="let u" class="actions">
            <button mat-icon-button type="button" (click)="editUnit(p, u)" [attr.aria-label]="'Edit ' + u.label">
              <mat-icon>edit</mat-icon>
            </button>
            <button mat-icon-button type="button" (click)="removeUnit(u)" [attr.aria-label]="'Delete ' + u.label">
              <mat-icon>delete</mat-icon>
            </button>
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-row" *matNoDataRow>
          <td class="mat-cell empty" [attr.colspan]="columns.length">No units yet.</td>
        </tr>
      </table>
    }
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .address { margin: -8px 0 16px; color: var(--mat-sys-on-surface-variant); }
    .buttons { display: flex; gap: 8px; }
    h2 { font: var(--mat-sys-title-large); margin: 16px 0 8px; }
  `,
})
export class PropertyDetail {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);

  /** Route parameter :id. */
  readonly id = input.required<string>();

  protected readonly columns = ['label', 'rent', 'deposit', 'status', 'actions'];
  protected readonly property = signal<Property | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => void this.load(this.id()));
  }

  private async load(id: string): Promise<void> {
    try {
      this.property.set(await firstValueFrom(this.api.getProperty(id)));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected async edit(p: Property): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog.open<PropertyForm, Property, Property>(PropertyForm, { data: p, width: '560px' }).afterClosed(),
    );
    if (saved) this.property.set(saved);
  }

  protected async remove(p: Property): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: `Delete ${p.name}?`,
      message: 'The property and its units are hidden from lists. Their history is kept.',
      confirmLabel: 'Delete property',
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.deleteProperty(p.id));
      this.snack.open(`${p.name} deleted`, undefined, { duration: 3000 });
      await this.router.navigateByUrl('/properties');
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }

  protected async editUnit(p: Property, unit: Unit | null): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog.open<UnitForm, UnitFormData, Unit>(UnitForm, { data: { propertyId: p.id, unit } }).afterClosed(),
    );
    if (saved) await this.load(p.id);
  }

  protected async removeUnit(unit: Unit): Promise<void> {
    const ok = await confirm(this.dialog, {
      title: `Delete ${unit.label}?`,
      message: 'The unit is hidden from lists. Its history is kept.',
      confirmLabel: 'Delete unit',
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.deleteUnit(unit.id));
      await this.load(this.id());
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    }
  }
}
