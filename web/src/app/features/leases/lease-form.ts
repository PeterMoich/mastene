import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { Lease, PropertySummary, Tenant, Unit } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { applyServerErrors, errorMessage } from '../../core/problem';
import { todayIso } from './lease-shared';

/** Create a draft lease, or edit one (pass the draft as dialog data). The end date is set by the API. */
@Component({
  selector: 'app-lease-form',
  imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ existing ? 'Edit draft lease' : 'New lease' }}</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="grid">
        <mat-form-field class="wide">
          <mat-label>Tenant</mat-label>
          <mat-select formControlName="tenantId" required>
            @for (t of tenants(); track t.id) {
              <mat-option [value]="t.id">{{ t.firstName }} {{ t.lastName }} · {{ t.email }}</mat-option>
            }
          </mat-select>
          <mat-error>{{ message('tenantId') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Property</mat-label>
          <mat-select formControlName="propertyId" required>
            @for (p of properties(); track p.id) {
              <mat-option [value]="p.id">{{ p.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Unit</mat-label>
          <mat-select formControlName="unitId" required>
            @for (u of units(); track u.id) {
              <mat-option [value]="u.id">{{ u.label }} ({{ u.status }})</mat-option>
            }
          </mat-select>
          <mat-error>{{ message('unitId') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Start date</mat-label>
          <input matInput type="date" formControlName="startDate" required />
          <mat-hint>The lease runs 12 months from this date.</mat-hint>
          <mat-error>{{ message('startDate') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Escalation at renewal</mat-label>
          <input matInput type="number" formControlName="escalationPct" min="0" [max]="maxEscalation()" step="0.5" />
          <span matTextSuffix>%</span>
          <mat-hint>Maximum {{ maxEscalation() }}% (setting: legal review needed)</mat-hint>
          <mat-error>{{ message('escalationPct') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Monthly rent</mat-label>
          <span matTextPrefix>R&nbsp;</span>
          <input matInput type="number" formControlName="monthlyRent" required min="0.01" step="0.01" />
          <mat-error>{{ message('monthlyRent') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Deposit</mat-label>
          <span matTextPrefix>R&nbsp;</span>
          <input matInput type="number" formControlName="depositAmount" required min="0" step="0.01" />
          <mat-error>{{ message('depositAmount') }}</mat-error>
        </mat-form-field>
        @if (error()) {
          <p class="error wide" role="alert">{{ error() }}</p>
        }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy()">Save draft</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .grid { display: grid; grid-template-columns: 1fr 1fr; column-gap: 12px; min-width: min(560px, 80vw); }
    .wide { grid-column: 1 / -1; }
    .error { color: var(--mat-sys-error); }
    @media (max-width: 560px) { .grid { grid-template-columns: 1fr; } }
  `,
})
export class LeaseForm {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<LeaseForm, Lease>);
  protected readonly existing = inject<Lease | null>(MAT_DIALOG_DATA, { optional: true });

  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly properties = signal<PropertySummary[]>([]);
  protected readonly units = signal<Unit[]>([]);
  protected readonly maxEscalation = signal(10);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    tenantId: [this.existing?.tenantId ?? '', Validators.required],
    propertyId: [this.existing?.propertyId ?? '', Validators.required],
    unitId: [this.existing?.unitId ?? '', Validators.required],
    startDate: [this.existing?.startDate ?? todayIso(), Validators.required],
    escalationPct: [this.existing?.escalationPct ?? 0, [Validators.min(0)]],
    monthlyRent: [this.existing?.monthlyRent ?? 0, [Validators.required, Validators.min(0.01)]],
    depositAmount: [this.existing?.depositAmount ?? 0, [Validators.required, Validators.min(0)]],
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.form.controls.propertyId.valueChanges.pipe(takeUntilDestroyed(destroyRef)).subscribe((id) => {
      this.form.controls.unitId.setValue('');
      void this.loadUnits(id);
    });
    this.form.controls.unitId.valueChanges.pipe(takeUntilDestroyed(destroyRef)).subscribe((id) => {
      const unit = this.units().find((u) => u.id === id);
      if (unit) this.form.patchValue({ monthlyRent: unit.rentAmount, depositAmount: unit.depositAmount });
    });
    void this.loadLists();
  }

  protected message(name: keyof typeof this.form.controls): string {
    const errors = this.form.controls[name].errors;
    if (!errors) return '';
    if (errors['server']) return errors['server'] as string;
    if (errors['required']) return 'This field is required.';
    if (errors['min']) return 'Too low.';
    return 'Invalid value.';
  }

  private async loadLists(): Promise<void> {
    try {
      const [tenants, properties, settings] = await Promise.all([
        firstValueFrom(this.api.listTenants({ pageSize: 100 })),
        firstValueFrom(this.api.listProperties({ pageSize: 100 })),
        firstValueFrom(this.api.getSettings()),
      ]);
      this.tenants.set(tenants.items);
      this.properties.set(properties.items);
      this.maxEscalation.set(settings.maxEscalationPct);
      if (!this.existing) this.form.controls.escalationPct.setValue(settings.defaultEscalationPct);
      if (this.existing) await this.loadUnits(this.existing.propertyId, true);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  private async loadUnits(propertyId: string, keepSelection = false): Promise<void> {
    if (!propertyId) {
      this.units.set([]);
      return;
    }
    const property = await firstValueFrom(this.api.getProperty(propertyId));
    this.units.set(property.units);
    if (keepSelection && this.existing) this.form.controls.unitId.setValue(this.existing.unitId, { emitEvent: false });
  }

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const v = this.form.getRawValue();
    const body = {
      unitId: v.unitId,
      tenantId: v.tenantId,
      startDate: v.startDate,
      monthlyRent: v.monthlyRent,
      depositAmount: v.depositAmount,
      escalationPct: v.escalationPct,
    };
    try {
      const saved = await firstValueFrom(
        this.existing ? this.api.updateLease(this.existing.id, body) : this.api.createLease(body),
      );
      this.ref.close(saved);
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
