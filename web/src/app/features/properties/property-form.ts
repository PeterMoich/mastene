import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { Property, PROVINCES } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { applyServerErrors, errorMessage } from '../../core/problem';

/** Create or edit a property. Pass the existing property as dialog data to edit it. */
@Component({
  selector: 'app-property-form',
  imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ existing ? 'Edit property' : 'Add property' }}</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="grid">
        <mat-form-field class="wide">
          <mat-label>Name</mat-label>
          <input matInput formControlName="name" required maxlength="120" />
          <mat-error>{{ fieldError('name') }}</mat-error>
        </mat-form-field>
        <mat-form-field class="wide">
          <mat-label>Address line 1</mat-label>
          <input matInput formControlName="addressLine1" required autocomplete="address-line1" />
          <mat-error>{{ fieldError('addressLine1') }}</mat-error>
        </mat-form-field>
        <mat-form-field class="wide">
          <mat-label>Address line 2 (optional)</mat-label>
          <input matInput formControlName="addressLine2" autocomplete="address-line2" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>City or town</mat-label>
          <input matInput formControlName="city" required autocomplete="address-level2" />
          <mat-error>{{ fieldError('city') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Postal code</mat-label>
          <input matInput formControlName="postalCode" required inputmode="numeric" maxlength="4" />
          <mat-error>{{ fieldError('postalCode') }}</mat-error>
        </mat-form-field>
        <mat-form-field class="wide">
          <mat-label>Province</mat-label>
          <mat-select formControlName="province" required>
            @for (p of provinces; track p) {
              <mat-option [value]="p">{{ p }}</mat-option>
            }
          </mat-select>
          <mat-error>{{ fieldError('province') }}</mat-error>
        </mat-form-field>
        @if (error()) {
          <p class="error wide" role="alert">{{ error() }}</p>
        }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy()">Save</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .grid { display: grid; grid-template-columns: 1fr 1fr; column-gap: 12px; }
    .wide { grid-column: 1 / -1; }
    .error { color: var(--mat-sys-error); }
    @media (max-width: 520px) { .grid { grid-template-columns: 1fr; } }
  `,
})
export class PropertyForm {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<PropertyForm, Property>);
  protected readonly existing = inject<Property | null>(MAT_DIALOG_DATA, { optional: true });
  protected readonly provinces = PROVINCES;
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: [this.existing?.name ?? '', [Validators.required, Validators.maxLength(120)]],
    addressLine1: [this.existing?.addressLine1 ?? '', Validators.required],
    addressLine2: [this.existing?.addressLine2 ?? ''],
    city: [this.existing?.city ?? '', Validators.required],
    province: [this.existing?.province ?? 'Gauteng', Validators.required],
    postalCode: [this.existing?.postalCode ?? '', [Validators.required, Validators.pattern(/^\d{4}$/)]],
  });

  protected fieldError(name: keyof typeof this.form.controls): string {
    const errors = this.form.controls[name].errors;
    if (!errors) return '';
    if (errors['server']) return errors['server'] as string;
    if (errors['required']) return 'This field is required.';
    if (errors['pattern']) return 'Postal code must be four digits.';
    if (errors['maxlength']) return 'Too long.';
    return 'Invalid value.';
  }

  async save(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const value = this.form.getRawValue();
    const body = { ...value, addressLine2: value.addressLine2 || null };
    try {
      const saved = await firstValueFrom(
        this.existing ? this.api.updateProperty(this.existing.id, body) : this.api.createProperty(body),
      );
      this.ref.close(saved);
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
