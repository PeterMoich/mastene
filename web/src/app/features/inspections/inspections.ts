import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import {
  Inspection,
  InspectionItem,
  InspectionKind,
  ITEM_CONDITIONS,
  ItemCondition,
  LeaseInspections,
  MAX_PHOTOS,
} from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/problem';
import { confirm } from '../../shared/dialogs';
import { checkPhotoFiles, PhotoRef, PhotoStrip } from '../../shared/photos';
import { TextDialog, TextDialogData } from '../issues/issue-dialogs';
import { todayIso } from '../leases/lease-shared';

export function inspectionTitle(kind: InspectionKind): string {
  return kind === 'MoveIn' ? 'Move-in inspection' : 'Move-out inspection';
}

/** Areas whose condition got worse between move-in and move-out (for the landlord's deposit decisions). */
export function worseThanMoveIn(item: InspectionItem): boolean {
  if (!item.condition || !item.moveInCondition) return false;
  return ITEM_CONDITIONS.indexOf(item.condition) > ITEM_CONDITIONS.indexOf(item.moveInCondition);
}

/** The inspections section on a lease page. */
@Component({
  selector: 'app-lease-inspections',
  imports: [RouterLink, DatePipe, MatButtonModule, MatIconModule],
  template: `
    <section aria-labelledby="insp-heading">
      <div class="head">
        <h2 id="insp-heading">Inspections</h2>
        <div class="buttons">
          @if (can('startMoveIn')) {
            <button mat-stroked-button type="button" (click)="start('MoveIn')" [disabled]="busy()">
              <mat-icon aria-hidden="true">checklist</mat-icon> Start move-in inspection
            </button>
          }
          @if (can('startMoveOut')) {
            <button mat-stroked-button type="button" (click)="start('MoveOut')" [disabled]="busy()">
              <mat-icon aria-hidden="true">checklist</mat-icon> Start move-out inspection
            </button>
          }
        </div>
      </div>
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      @if (data(); as d) {
        @if (d.items.length === 0) {
          <p class="muted">No inspections yet.</p>
        } @else {
          <ul class="plain">
            @for (i of d.items; track i.id) {
              <li>
                <a [routerLink]="['/inspections', i.id]">{{ title(i.kind) }}</a>
                · {{ i.inspectedOn | date: 'd MMM y' }} ·
                @if (i.status === 'Draft') {
                  draft, {{ i.areasChecked }} of {{ i.areas }} areas checked
                } @else if (i.tenantAcknowledgedAt) {
                  completed, acknowledged by the tenant {{ i.tenantAcknowledgedAt | date: 'd MMM y' }}
                } @else {
                  completed, waiting for the tenant to acknowledge
                }
                <span class="muted">({{ i.photos }} photos)</span>
              </li>
            }
          </ul>
        }
      }
    </section>
  `,
  styles: `
    .head { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
    h2 { font: var(--mat-sys-title-large); margin: 24px 0 8px; }
    .buttons { display: flex; gap: 8px; flex-wrap: wrap; }
    .plain { list-style: none; padding: 0; margin: 0; }
    .plain li { padding: 4px 0; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class LeaseInspectionsSection {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  readonly leaseId = input.required<string>();
  protected readonly data = signal<LeaseInspections | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  constructor() {
    effect(() => void this.load(this.leaseId()));
  }

  protected can(action: 'startMoveIn' | 'startMoveOut'): boolean {
    return this.data()?.allowedActions.includes(action) ?? false;
  }

  protected title(kind: InspectionKind): string {
    return inspectionTitle(kind);
  }

  private async load(leaseId: string): Promise<void> {
    try {
      this.data.set(await firstValueFrom(this.api.leaseInspections(leaseId)));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected async start(kind: InspectionKind): Promise<void> {
    this.busy.set(true);
    try {
      const created = await firstValueFrom(this.api.startInspection(this.leaseId(), kind));
      await this.router.navigate(['/inspections', created.id]);
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/**
 * One inspection. As a draft the landlord checks each area (condition, notes, photos) and completes
 * it; then it is read-only and the tenant acknowledges it. A move-out shows what each area was like
 * at move-in next to it.
 */
@Component({
  selector: 'app-inspection-detail',
  imports: [RouterLink, DatePipe, FormsModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, PhotoStrip],
  template: `
    @if (inspection(); as i) {
      <a mat-button [routerLink]="['/leases', i.leaseId]"><mat-icon aria-hidden="true">arrow_back</mat-icon> Lease {{ i.leaseNumber }}</a>
      <header class="head">
        <div>
          <h1>{{ title(i) }}</h1>
          <p class="sub">{{ i.unitLabel }}, {{ i.propertyName }} · {{ i.tenantName }}</p>
        </div>
        <div class="buttons">
          @if (can('complete')) {
            <button mat-flat-button type="button" (click)="complete(i)" [disabled]="busy()">
              <mat-icon aria-hidden="true">task_alt</mat-icon> Complete inspection
            </button>
          }
          @if (can('acknowledge')) {
            <button mat-flat-button type="button" (click)="acknowledge()" [disabled]="busy()">Acknowledge</button>
          }
        </div>
      </header>

      <p class="notice" role="status">
        @if (i.status === 'Draft') {
          Draft: {{ checked() }} of {{ i.items.length }} areas checked. The tenant sees it once you complete it; it can't be changed after that.
        } @else {
          Completed {{ i.completedAt | date: 'd MMM y, HH:mm' }} by {{ i.completedByName }}.
          @if (i.tenantAcknowledgedAt) {
            Acknowledged by the tenant {{ i.tenantAcknowledgedAt | date: 'd MMM y' }}.
          } @else {
            Waiting for the tenant to acknowledge it.
          }
        }
      </p>
      @if (i.tenantComment) {
        <p class="comment"><strong>Tenant's comment:</strong> {{ i.tenantComment }}</p>
      }
      @if (i.kind === 'MoveOut' && !i.moveInInspectionId) {
        <p class="muted">There is no completed move-in inspection to compare with.</p>
      }

      @if (editable()) {
        <div class="details">
          <mat-form-field>
            <mat-label>Inspection date</mat-label>
            <input matInput type="date" [max]="today" [(ngModel)]="inspectedOn" name="inspectedOn" />
          </mat-form-field>
          <mat-form-field class="grow">
            <mat-label>General notes</mat-label>
            <input matInput [(ngModel)]="generalNotes" name="generalNotes" maxlength="1000" />
          </mat-form-field>
          <button mat-stroked-button type="button" (click)="saveDetails()" [disabled]="busy()">Save</button>
        </div>
      } @else {
        <p>Inspected {{ i.inspectedOn | date: 'd MMMM y' }}@if (i.generalNotes) { · {{ i.generalNotes }} }</p>
      }

      <ol class="areas">
        @for (item of i.items; track item.id) {
          <li class="area" [class.worse]="worse(item)">
            <div class="area-head">
              <h2>{{ item.area }}</h2>
              @if (editable()) {
                <button mat-icon-button type="button" (click)="removeItem(item)" [attr.aria-label]="'Remove ' + item.area">
                  <mat-icon aria-hidden="true">delete</mat-icon>
                </button>
              }
            </div>
            @if (i.kind === 'MoveOut' && i.moveInInspectionId) {
              <p class="before">At move-in: <strong>{{ item.moveInCondition ?? 'not recorded' }}</strong>@if (item.moveInNotes) { · {{ item.moveInNotes }} }</p>
              @if (item.moveInPhotos.length) { <app-photo-strip [photos]="photosOf(i, item, true)" /> }
            }
            @if (editable()) {
              <div class="row">
                <mat-form-field subscriptSizing="dynamic">
                  <mat-label>Condition</mat-label>
                  <mat-select [value]="item.condition" (selectionChange)="saveItem(item, $event.value, item.notes)">
                    @for (c of conditions; track c) { <mat-option [value]="c">{{ c }}</mat-option> }
                  </mat-select>
                </mat-form-field>
                <mat-form-field class="grow" subscriptSizing="dynamic">
                  <mat-label>Notes</mat-label>
                  <input matInput #notes [value]="item.notes ?? ''" maxlength="1000"
                         (change)="saveItem(item, item.condition, notes.value)" />
                </mat-form-field>
              </div>
            } @else {
              <p>
                <strong>{{ item.condition ?? 'Not checked' }}</strong>@if (item.notes) { · {{ item.notes }} }
                @if (worse(item)) { <span class="flag">Worse than at move-in</span> }
              </p>
            }
            <app-photo-strip [photos]="photosOf(i, item, false)" [empty]="editable() ? 'No photos.' : ''" />
            @if (editable() && item.photos.length < maxPhotos) {
              <label class="upload">
                <input type="file" multiple accept=".jpg,.jpeg,.png,image/jpeg,image/png" class="visually-hidden"
                       (change)="addPhotos(item, $any($event.target))" [disabled]="busy()" />
                <mat-icon aria-hidden="true">add_a_photo</mat-icon> Add photos
              </label>
            }
          </li>
        }
      </ol>
      @for (p of photoProblems(); track p) { <p class="error" role="alert">{{ p }}</p> }

      @if (editable()) {
        <form class="add" (ngSubmit)="addArea()" novalidate>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Add an area</mat-label>
            <input matInput [(ngModel)]="newArea" name="newArea" maxlength="80" placeholder="e.g. Garage" />
          </mat-form-field>
          <button mat-stroked-button type="submit" [disabled]="busy() || !newArea.trim()">Add</button>
        </form>
      }
    } @else if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    }
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .sub { margin: 0 0 8px; color: var(--mat-sys-on-surface-variant); }
    .buttons { display: flex; gap: 8px; flex-wrap: wrap; }
    .notice { background: var(--mat-sys-tertiary-container); color: var(--mat-sys-on-tertiary-container);
              padding: 8px 12px; border-radius: 8px; }
    .comment { background: var(--mat-sys-surface-container); padding: 8px 12px; border-radius: 8px; }
    .details, .row, .add { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
    .grow { flex: 1; min-width: 220px; }
    .areas { list-style: none; padding: 0; margin: 16px 0; display: grid; gap: 12px; }
    .area { border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px; padding: 12px 16px;
            display: flex; flex-direction: column; gap: 8px; }
    .area.worse { border-color: var(--mat-sys-error); }
    .area-head { display: flex; align-items: center; justify-content: space-between; }
    .area h2 { font: var(--mat-sys-title-medium); margin: 0; }
    .area p { margin: 0; }
    .before { color: var(--mat-sys-on-surface-variant); }
    .flag { margin-left: 8px; color: var(--mat-sys-error); font-weight: 500; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .upload { display: inline-flex; align-self: flex-start; align-items: center; gap: 6px; cursor: pointer; padding: 6px 14px;
              border: 1px solid var(--mat-sys-outline); border-radius: 20px; font: var(--mat-sys-label-large); }
    .upload:focus-within { outline: 2px solid var(--mat-sys-primary); outline-offset: 2px; }
  `,
})
export class InspectionDetail {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  /** Route parameter. */
  readonly id = input.required<string>();
  protected readonly conditions = ITEM_CONDITIONS;
  protected readonly maxPhotos = MAX_PHOTOS;
  protected readonly today = todayIso();
  protected readonly inspection = signal<Inspection | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly photoProblems = signal<string[]>([]);
  protected readonly editable = computed(() => this.inspection()?.allowedActions.includes('edit') ?? false);
  protected readonly checked = computed(() => this.inspection()?.items.filter((x) => x.condition).length ?? 0);
  protected inspectedOn = '';
  protected generalNotes = '';
  protected newArea = '';

  constructor() {
    effect(() => void this.load(this.id()));
  }

  protected can(action: 'edit' | 'complete' | 'acknowledge'): boolean {
    return this.inspection()?.allowedActions.includes(action) ?? false;
  }

  protected title(i: Inspection): string {
    return inspectionTitle(i.kind);
  }

  protected worse(item: InspectionItem): boolean {
    return worseThanMoveIn(item);
  }

  protected photosOf(i: Inspection, item: InspectionItem, moveIn: boolean): PhotoRef[] {
    const list = moveIn ? item.moveInPhotos : item.photos;
    return list.map((p, n) => ({
      url: this.api.inspectionPhotoUrl(i.id, p.id),
      alt: `${item.area}${moveIn ? ' at move-in' : ''}, photo ${n + 1}`,
    }));
  }

  private async load(id: string): Promise<void> {
    try {
      this.set(await firstValueFrom(this.api.getInspection(id)));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  private set(i: Inspection): void {
    this.inspection.set(i);
    this.inspectedOn = i.inspectedOn;
    this.generalNotes = i.generalNotes ?? '';
  }

  protected saveDetails(): Promise<void> {
    return this.run(this.api.updateInspection(this.id(), this.inspectedOn, this.generalNotes || null), 'Saved.');
  }

  protected saveItem(item: InspectionItem, condition: ItemCondition | null, notes: string | null): Promise<void> {
    return this.run(this.api.updateInspectionItem(this.id(), item.id, condition, notes || null));
  }

  protected async removeItem(item: InspectionItem): Promise<void> {
    if (await confirm(this.dialog, { title: `Remove ${item.area}?`, message: 'Its notes and photos are removed from this checklist.', confirmLabel: 'Remove' }))
      await this.run(this.api.removeInspectionItem(this.id(), item.id));
  }

  protected async addArea(): Promise<void> {
    const area = this.newArea.trim();
    if (!area) return;
    await this.run(this.api.addInspectionArea(this.id(), area));
    this.newArea = '';
  }

  protected async addPhotos(item: InspectionItem, input: HTMLInputElement): Promise<void> {
    const { ok, problems } = checkPhotoFiles(Array.from(input.files ?? []), item.photos.length);
    input.value = '';
    this.busy.set(true);
    for (const file of ok) {
      try {
        this.set(await firstValueFrom(this.api.addInspectionPhoto(this.id(), item.id, file)));
      } catch (e) {
        problems.push(`${file.name}: ${errorMessage(e)}`);
      }
    }
    this.photoProblems.set(problems);
    this.busy.set(false);
  }

  protected async complete(i: Inspection): Promise<void> {
    const missing = i.items.filter((x) => !x.condition).map((x) => x.area);
    if (missing.length) {
      this.snack.open(`Give a condition for every area first: ${missing.join(', ')}.`, 'Close');
      return;
    }
    const ok = await confirm(this.dialog, {
      title: 'Complete the inspection?',
      message: 'The tenant will see it and be asked to acknowledge it. It cannot be changed afterwards.',
      confirmLabel: 'Complete',
    });
    if (ok) await this.run(this.api.completeInspection(i.id), 'Inspection completed.');
  }

  protected async acknowledge(): Promise<void> {
    const comment = await firstValueFrom(
      this.dialog
        .open<TextDialog, TextDialogData, string>(TextDialog, {
          data: {
            title: 'Acknowledge the inspection',
            label: 'Comment (optional)',
            hint: "Note anything you disagree with, e.g. damage that was already there.",
            confirmLabel: 'Acknowledge',
            required: false,
            maxLength: 1000,
          },
        })
        .afterClosed(),
    );
    if (comment !== undefined) await this.run(this.api.acknowledgeInspection(this.id(), comment || null), 'Thank you.');
  }

  private async run(request: Observable<Inspection>, done?: string): Promise<void> {
    this.busy.set(true);
    try {
      this.set(await firstValueFrom(request));
      if (done) this.snack.open(done, 'Close', { duration: 3000 });
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
      await this.load(this.id());
    } finally {
      this.busy.set(false);
    }
  }
}
