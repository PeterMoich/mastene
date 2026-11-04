import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import {
  Contractor,
  ISSUE_CATEGORIES,
  ISSUE_PRIORITIES,
  Issue,
  IssueCategory,
  IssuePriority,
  LeaseSummary,
  PropertySummary,
  Unit,
} from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { applyServerErrors, errorMessage } from '../../core/problem';
import { checkPhotoFiles } from '../../shared/photos';

export const PRIORITY_HELP: Record<IssuePriority, string> = {
  Urgent: 'Danger or no water, power or security (e.g. burst pipe, sparking wiring, broken lock)',
  High: 'Something important not working (e.g. no hot water, fridge or stove broken)',
  Medium: 'Needs fixing soon but you can manage (e.g. dripping tap, blocked drain)',
  Low: 'Cosmetic or minor (e.g. loose handle, scuffed paint)',
};

/** Result of logging an issue: the issue and any photos that couldn't be added. */
export interface NewIssueResult {
  issue: Issue;
  photoProblems: string[];
}

/**
 * Logs an issue. A tenant picks one of their running leases (usually there is only one); the landlord
 * picks a property and unit. Photos are checked here first, then uploaded one by one after the issue
 * is created.
 */
@Component({
  selector: 'app-new-issue-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Report an issue</h2>
    <form [formGroup]="form" (ngSubmit)="save()" novalidate>
      <mat-dialog-content class="fields">
        @if (isTenant()) {
          @if (leases().length > 1) {
            <mat-form-field>
              <mat-label>Which home?</mat-label>
              <mat-select formControlName="leaseId" required>
                @for (l of leases(); track l.id) {
                  <mat-option [value]="l.id">{{ l.unitLabel }}, {{ l.propertyName }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          } @else if (leases().length === 1) {
            <p class="where">For {{ leases()[0].unitLabel }}, {{ leases()[0].propertyName }}</p>
          } @else if (loaded()) {
            <p class="error" role="alert">You don't have a running lease, so you can't report an issue here.</p>
          }
        } @else {
          <div class="row">
            <mat-form-field>
              <mat-label>Property</mat-label>
              <mat-select formControlName="propertyId" required (selectionChange)="loadUnits($event.value)">
                @for (p of properties(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field>
              <mat-label>Unit</mat-label>
              <mat-select formControlName="unitId" required>
                @for (u of units(); track u.id) { <mat-option [value]="u.id">{{ u.label }}</mat-option> }
              </mat-select>
              <mat-hint>A let unit's issue is shared with its tenant.</mat-hint>
            </mat-form-field>
          </div>
        }
        <mat-form-field>
          <mat-label>What's wrong? (short title)</mat-label>
          <input matInput formControlName="title" required maxlength="120" />
          <mat-error>{{ form.controls.title.errors?.['server'] ?? 'Give the issue a short title.' }}</mat-error>
        </mat-form-field>
        <div class="row">
          <mat-form-field>
            <mat-label>Category</mat-label>
            <mat-select formControlName="category" required>
              @for (c of categories; track c) { <mat-option [value]="c">{{ c }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>How urgent?</mat-label>
            <mat-select formControlName="priority" required>
              @for (p of priorities; track p) { <mat-option [value]="p">{{ p }}</mat-option> }
            </mat-select>
            <mat-hint>{{ help() }}</mat-hint>
          </mat-form-field>
        </div>
        <mat-form-field>
          <mat-label>Describe the problem</mat-label>
          <textarea matInput formControlName="description" required rows="4" maxlength="4000"></textarea>
          <mat-hint>Where is it, when did it start, is anything damaged?</mat-hint>
          <mat-error>{{ form.controls.description.errors?.['server'] ?? 'Describe the problem.' }}</mat-error>
        </mat-form-field>
        <div class="file">
          <label for="issue-photos">Photos (optional; up to 5 JPG or PNG files, 5 MB each)</label>
          <input id="issue-photos" type="file" multiple accept=".jpg,.jpeg,.png,image/jpeg,image/png"
                 (change)="onFiles($any($event.target).files)" />
          @if (files().length) { <p class="muted">{{ files().length }} photo(s) ready to upload.</p> }
          @for (p of fileProblems(); track p) { <p class="error" role="alert">{{ p }}</p> }
        </div>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy() || (isTenant() && leases().length === 0)">
          {{ busy() ? 'Sending…' : 'Report issue' }}
        </button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(560px, 80vw); gap: 4px; }
    .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .file { display: flex; flex-direction: column; gap: 6px; margin: 8px 0; }
    .where { margin: 0 0 12px; font: var(--mat-sys-title-small); }
    .muted { color: var(--mat-sys-on-surface-variant); margin: 0; }
    .error { color: var(--mat-sys-error); margin: 0; }
    @media (max-width: 600px) { .row { grid-template-columns: 1fr; } }
  `,
})
export class NewIssueDialog {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly ref = inject(MatDialogRef<NewIssueDialog, NewIssueResult>);
  /** Pre-selected lease (e.g. when opened from a lease page). */
  private readonly data = inject<{ leaseId?: string } | null>(MAT_DIALOG_DATA, { optional: true });

  protected readonly categories = ISSUE_CATEGORIES;
  protected readonly priorities = ISSUE_PRIORITIES;
  protected readonly isTenant = computed(() => this.auth.role() === 'Tenant');
  protected readonly leases = signal<LeaseSummary[]>([]);
  protected readonly properties = signal<PropertySummary[]>([]);
  protected readonly units = signal<Unit[]>([]);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly files = signal<File[]>([]);
  protected readonly fileProblems = signal<string[]>([]);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    leaseId: [this.data?.leaseId ?? ''],
    propertyId: [''],
    unitId: [''],
    title: ['', [Validators.required, Validators.maxLength(120)]],
    category: ['Plumbing' as IssueCategory, Validators.required],
    priority: ['Medium' as IssuePriority, Validators.required],
    description: ['', [Validators.required, Validators.maxLength(4000)]],
  });

  private readonly priority = signal<IssuePriority>('Medium');
  protected readonly help = computed(() => PRIORITY_HELP[this.priority()]);

  constructor() {
    this.form.controls.priority.valueChanges.subscribe((p) => this.priority.set(p));
    void this.loadChoices();
  }

  private async loadChoices(): Promise<void> {
    try {
      if (this.isTenant()) {
        const page = await firstValueFrom(this.api.listLeases({ pageSize: 50 }));
        const running = page.items.filter((l) => l.status === 'Active' || l.status === 'Expiring');
        this.leases.set(running);
        if (!this.form.controls.leaseId.value && running.length > 0) this.form.controls.leaseId.setValue(running[0].id);
      } else {
        this.properties.set((await firstValueFrom(this.api.listProperties({ pageSize: 100, sort: 'name' }))).items);
      }
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.loaded.set(true);
    }
  }

  async loadUnits(propertyId: string): Promise<void> {
    this.form.controls.unitId.setValue('');
    this.units.set((await firstValueFrom(this.api.getProperty(propertyId))).units);
  }

  onFiles(list: FileList | null): void {
    const { ok, problems } = checkPhotoFiles(Array.from(list ?? []), 0);
    this.files.set(ok);
    this.fileProblems.set(problems);
  }

  async save(): Promise<void> {
    const v = this.form.getRawValue();
    const target = this.isTenant() ? v.leaseId : v.unitId;
    if (this.form.invalid || !target) {
      this.form.markAllAsTouched();
      if (!target) this.error.set(this.isTenant() ? 'Choose the home this is about.' : 'Choose the property and unit.');
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      let issue = await firstValueFrom(
        this.api.createIssue({
          leaseId: this.isTenant() ? v.leaseId : null,
          unitId: this.isTenant() ? null : v.unitId,
          category: v.category,
          priority: v.priority,
          title: v.title,
          description: v.description,
        }),
      );
      const photoProblems: string[] = [];
      for (const file of this.files()) {
        try {
          issue = await firstValueFrom(this.api.addIssuePhoto(issue.id, file));
        } catch (e) {
          photoProblems.push(`${file.name}: ${errorMessage(e)}`);
        }
      }
      this.ref.close({ issue, photoProblems });
    } catch (e) {
      if (!applyServerErrors(this.form, e)) this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

export interface TextDialogData {
  title: string;
  label: string;
  hint?: string;
  confirmLabel: string;
  required: boolean;
  maxLength: number;
}

/** Asks for one piece of text: a resolution note, a reason for reopening, a comment on an inspection. */
@Component({
  selector: 'app-text-dialog',
  // FormsModule gives the <form> its ngSubmit (via NgForm); the text box itself is a reactive control.
  imports: [FormsModule, ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <form (ngSubmit)="save()" novalidate>
      <mat-dialog-content>
        <mat-form-field class="full">
          <mat-label>{{ data.label }}</mat-label>
          <textarea matInput [formControl]="text" rows="3" [attr.maxlength]="data.maxLength" [required]="data.required"></textarea>
          @if (data.hint) { <mat-hint>{{ data.hint }}</mat-hint> }
          <mat-error>This is required.</mat-error>
        </mat-form-field>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit">{{ data.confirmLabel }}</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `.full { width: min(480px, 80vw); }`,
})
export class TextDialog {
  protected readonly data = inject<TextDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<TextDialog, string>);
  protected readonly text = inject(FormBuilder).nonNullable.control('', [
    ...(this.data.required ? [Validators.required] : []),
    Validators.maxLength(this.data.maxLength),
  ]);

  save(): void {
    if (this.text.invalid) {
      this.text.markAsTouched();
      return;
    }
    this.ref.close(this.text.value.trim());
  }
}

/** Picks a contractor (or none) for an issue. */
@Component({
  selector: 'app-assign-contractor-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatSelectModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Assign a contractor</h2>
    <mat-dialog-content>
      @if (contractors().length === 0 && loaded()) {
        <p>No contractors yet. Add them on the Contractors page.</p>
      }
      <mat-form-field class="full">
        <mat-label>Contractor</mat-label>
        <mat-select [formControl]="choice">
          <mat-option value="">No contractor</mat-option>
          @for (c of contractors(); track c.id) {
            <mat-option [value]="c.id">{{ c.name }} ({{ c.trade }})</mat-option>
          }
        </mat-select>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" (click)="save()">Save</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: min(420px, 80vw); }`,
})
export class AssignContractorDialog {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<AssignContractorDialog, { contractorId: string | null }>);
  protected readonly data = inject<{ current: string | null }>(MAT_DIALOG_DATA);
  protected readonly contractors = signal<Contractor[]>([]);
  protected readonly loaded = signal(false);
  protected readonly choice = inject(FormBuilder).nonNullable.control(this.data.current ?? '');

  constructor() {
    void firstValueFrom(this.api.listContractors({ pageSize: 100 })).then((page) => {
      this.contractors.set(page.items);
      this.loaded.set(true);
    });
  }

  save(): void {
    this.ref.close({ contractorId: this.choice.value || null });
  }
}
