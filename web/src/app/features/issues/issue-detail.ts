import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { ISSUE_PRIORITIES, Issue, IssueAction, IssuePriority } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';
import { checkPhotoFiles, PhotoRef, PhotoStrip } from '../../shared/photos';
import { AssignContractorDialog, TextDialog, TextDialogData } from './issue-dialogs';
import { EscalatedBadge, IssuePriorityLabel, IssueStatusChip, issueEventLabel, targetText } from './issue-shared';

/** One line in the issue's activity: a step in its history or a comment. */
export interface ActivityEntry {
  at: string;
  who: string;
  text: string;
  detail: string | null;
  isComment: boolean;
  bySystem: boolean;
}

/** History and comments merged, oldest first. */
export function activity(issue: Issue): ActivityEntry[] {
  const events: ActivityEntry[] = issue.events.map((e) => ({
    at: e.at,
    who: e.byName,
    text: issueEventLabel(e.kind),
    detail: e.note,
    isComment: false,
    bySystem: e.byName === 'System',
  }));
  const comments: ActivityEntry[] = issue.comments.map((c) => ({
    at: c.at,
    who: c.authorName,
    text: 'Commented',
    detail: c.body,
    isComment: true,
    bySystem: false,
  }));
  return [...events, ...comments].sort((a, b) => a.at.localeCompare(b.at));
}

@Component({
  selector: 'app-issue-detail',
  imports: [
    RouterLink, DatePipe, FormsModule, ReactiveFormsModule, MatButtonModule, MatIconModule, MatMenuModule, MatFormFieldModule,
    MatInputModule, IssueStatusChip, IssuePriorityLabel, EscalatedBadge, PhotoStrip,
  ],
  template: `
    <a mat-button routerLink="/issues"><mat-icon aria-hidden="true">arrow_back</mat-icon> {{ isTenant() ? 'My issues' : 'All issues' }}</a>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    @if (issue(); as i) {
      <header class="head">
        <div>
          <h1>{{ i.title }}</h1>
          <div class="tags">
            <app-issue-status [status]="i.status" />
            <app-issue-priority [priority]="i.priority" />
            <span class="muted">{{ i.category }}</span>
            @if (i.isEscalated) { <app-escalated /> }
          </div>
        </div>
        <div class="buttons" role="group" aria-label="Issue actions">
          @if (can('acknowledge')) {
            <button mat-stroked-button type="button" (click)="step('acknowledge')" [disabled]="busy()">Acknowledge</button>
          }
          @if (can('start')) {
            <button mat-flat-button type="button" (click)="step('start')" [disabled]="busy()">
              <mat-icon aria-hidden="true">construction</mat-icon> Start work
            </button>
          }
          @if (can('resolve')) {
            <button mat-flat-button type="button" (click)="resolve()" [disabled]="busy()">
              <mat-icon aria-hidden="true">task_alt</mat-icon> Mark resolved
            </button>
          }
          @if (can('close')) {
            <button mat-flat-button type="button" (click)="step('close')" [disabled]="busy()">
              <mat-icon aria-hidden="true">check</mat-icon> {{ isTenant() ? "Yes, it's fixed" : 'Close' }}
            </button>
          }
          @if (can('reopen')) {
            <button mat-stroked-button type="button" (click)="reopen()" [disabled]="busy()">Not fixed: reopen</button>
          }
          @if (can('assignContractor')) {
            <button mat-stroked-button type="button" (click)="assign(i)" [disabled]="busy()">
              <mat-icon aria-hidden="true">engineering</mat-icon> {{ i.contractor ? 'Change contractor' : 'Assign contractor' }}
            </button>
          }
          @if (can('changePriority')) {
            <button mat-stroked-button type="button" [matMenuTriggerFor]="prio" [disabled]="busy()">Priority</button>
            <mat-menu #prio="matMenu">
              @for (p of priorities; track p) {
                <button mat-menu-item type="button" (click)="changePriority(p)" [disabled]="p === i.priority">{{ p }}</button>
              }
            </mat-menu>
          }
        </div>
      </header>

      @if (i.status === 'Resolved') {
        <p class="notice" role="status">
          @if (i.tenantId) {
            Marked resolved {{ i.resolvedAt | date: 'd MMM, HH:mm' }}{{ i.resolutionNote ? ': ' + i.resolutionNote : '.' }}
            {{ isTenant() ? 'Please confirm it is fixed, or reopen it if not.' : 'Waiting for the tenant to confirm.' }}
            It closes by itself after {{ i.autoCloseDays }} days.
          } @else {
            Marked resolved{{ i.resolutionNote ? ': ' + i.resolutionNote : '.' }}
          }
        </p>
      }

      <section class="grid" aria-label="Issue details">
        <dl>
          <dt>Where</dt><dd>{{ i.unitLabel }}, {{ i.propertyName }}@if (i.leaseNumber) { <span class="muted"> · lease {{ i.leaseNumber }}</span> }</dd>
          @if (!isTenant()) { <dt>Tenant</dt><dd>{{ i.tenantName ?? 'None (vacant unit)' }}</dd> }
          <dt>Reported</dt><dd>{{ i.openedAt | date: 'd MMM y, HH:mm' }} by {{ i.reportedByName }}</dd>
          <dt>Contractor</dt>
          <dd>
            @if (i.contractor; as c) {
              {{ c.name }} ({{ c.trade }}) · <a [href]="'tel:' + c.phone">{{ c.phone }}</a>
            } @else { <span class="muted">Not assigned</span> }
          </dd>
        </dl>
        <dl aria-label="Targets">
          <dt>Response target</dt>
          <dd>
            By {{ i.responseDueAt | date: 'd MMM, HH:mm' }} ·
            <span [class.late]="response(i).late">{{ response(i).text }}</span>
            @if (i.acknowledgedAt) { <div class="muted small">Acknowledged {{ i.acknowledgedAt | date: 'd MMM, HH:mm' }}</div> }
          </dd>
          <dt>Resolution target</dt>
          <dd>
            By {{ i.resolutionDueAt | date: 'd MMM, HH:mm' }} ·
            <span [class.late]="resolution(i).late">{{ resolution(i).text }}</span>
            @if (i.reopenCount > 0) { <div class="muted small">Reopened {{ i.reopenCount }} time(s)</div> }
          </dd>
        </dl>
      </section>

      <section aria-labelledby="desc-heading">
        <h2 id="desc-heading">Description</h2>
        <p class="description">{{ i.description }}</p>
      </section>

      <section aria-labelledby="photo-heading">
        <div class="section-head">
          <h2 id="photo-heading">Photos <span class="muted">({{ i.photos.length }} of 5)</span></h2>
          @if (can('addPhoto')) {
            <label class="upload">
              <input type="file" multiple accept=".jpg,.jpeg,.png,image/jpeg,image/png" class="visually-hidden"
                     (change)="addPhotos(i, $any($event.target))" [disabled]="busy()" />
              <mat-icon aria-hidden="true">add_a_photo</mat-icon> Add photos
            </label>
          }
        </div>
        @for (p of photoProblems(); track p) { <p class="error" role="alert">{{ p }}</p> }
        <app-photo-strip [photos]="photos()" empty="No photos yet." />
      </section>

      <section aria-labelledby="act-heading">
        <h2 id="act-heading">Activity</h2>
        <ol class="timeline">
          @for (a of entries(); track $index) {
            <li [class.comment]="a.isComment" [class.system]="a.bySystem">
              <div class="when">{{ a.at | date: 'd MMM, HH:mm' }}</div>
              <div><strong>{{ a.who }}</strong>: {{ a.text }}
                @if (a.detail) { <div [class.body]="a.isComment" [class.muted]="!a.isComment">{{ a.detail }}</div> }
              </div>
            </li>
          }
        </ol>
        @if (can('comment')) {
          <form class="comment" (ngSubmit)="comment(i)" novalidate>
            <mat-form-field class="full">
              <mat-label>Add a comment</mat-label>
              <textarea matInput [formControl]="commentText" rows="2" maxlength="2000"></textarea>
              <mat-hint>{{ isTenant() ? 'The landlord sees this.' : 'The tenant sees this.' }}</mat-hint>
            </mat-form-field>
            <button mat-flat-button type="submit" [disabled]="busy() || !commentText.value.trim()">Post</button>
          </form>
        }
      </section>
    }
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .tags { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
    .buttons { display: flex; gap: 8px; flex-wrap: wrap; }
    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; margin: 16px 0; }
    dt { font-weight: 500; margin-top: 10px; }
    dd { margin: 0; }
    h2 { font: var(--mat-sys-title-large); margin: 24px 0 8px; }
    .section-head { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .small { font: var(--mat-sys-body-small); }
    .late { color: var(--mat-sys-error); font-weight: 500; }
    .description { white-space: pre-line; max-width: 72ch; }
    .notice { background: var(--mat-sys-tertiary-container); color: var(--mat-sys-on-tertiary-container);
              padding: 8px 12px; border-radius: 8px; }
    .upload { display: inline-flex; align-items: center; gap: 6px; cursor: pointer; padding: 6px 14px;
              border: 1px solid var(--mat-sys-outline); border-radius: 20px; font: var(--mat-sys-label-large); }
    .upload:focus-within { outline: 2px solid var(--mat-sys-primary); outline-offset: 2px; }
    .timeline { list-style: none; padding: 0; margin: 0; border-left: 2px solid var(--mat-sys-outline-variant); }
    .timeline li { display: grid; grid-template-columns: 110px 1fr; gap: 12px; padding: 8px 0 8px 12px; }
    .timeline li.system strong { color: var(--mat-sys-error); }
    .when { color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-small); padding-top: 2px; }
    .body { background: var(--mat-sys-surface-container); padding: 8px 12px; border-radius: 8px; margin-top: 4px;
            white-space: pre-line; max-width: 72ch; }
    .comment { display: flex; gap: 12px; align-items: flex-start; margin-top: 16px; flex-wrap: wrap; }
    .full { flex: 1; min-width: 260px; }
    @media (max-width: 720px) { .grid { grid-template-columns: 1fr; gap: 0; } }
  `,
})
export class IssueDetail {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  /** Route parameter. */
  readonly id = input.required<string>();

  protected readonly isTenant = computed(() => this.auth.role() === 'Tenant');
  protected readonly priorities = ISSUE_PRIORITIES;
  protected readonly issue = signal<Issue | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly photoProblems = signal<string[]>([]);
  protected readonly entries = computed(() => {
    const i = this.issue();
    return i ? activity(i) : [];
  });
  protected readonly photos = computed<PhotoRef[]>(() => {
    const i = this.issue();
    return i
      ? i.photos.map((p, n) => ({ url: this.api.issuePhotoUrl(i.id, p.id), alt: `Photo ${n + 1} of ${i.title}` }))
      : [];
  });
  protected readonly commentText = inject(FormBuilder).nonNullable.control('', [Validators.maxLength(2000)]);

  constructor() {
    effect(() => void this.load(this.id()));
  }

  protected can(action: IssueAction): boolean {
    return this.issue()?.allowedActions.includes(action) ?? false;
  }

  protected response(i: Issue) {
    return targetText(i.responseDueAt, i.acknowledgedAt);
  }

  protected resolution(i: Issue) {
    return i.status === 'Closed' && !i.resolvedAt ? { text: 'Closed', late: false } : targetText(i.resolutionDueAt, i.resolvedAt);
  }

  async load(id = this.id()): Promise<void> {
    try {
      this.issue.set(await firstValueFrom(this.api.getIssue(id)));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected async step(step: 'acknowledge' | 'start' | 'close'): Promise<void> {
    await this.run(this.api.issueStep(this.id(), step), step === 'close' ? 'Issue closed.' : undefined);
  }

  protected async resolve(): Promise<void> {
    const note = await this.ask({
      title: 'Mark resolved',
      label: 'What was done? (optional)',
      hint: this.issue()?.tenantId ? 'The tenant is asked to confirm the fix.' : undefined,
      confirmLabel: 'Mark resolved',
      required: false,
      maxLength: 1000,
    });
    if (note !== undefined) await this.run(this.api.resolveIssue(this.id(), note || null));
  }

  protected async reopen(): Promise<void> {
    const reason = await this.ask({
      title: 'Reopen the issue',
      label: "What's still wrong?",
      confirmLabel: 'Reopen',
      required: true,
      maxLength: 1000,
    });
    if (reason) await this.run(this.api.reopenIssue(this.id(), reason));
  }

  protected async assign(i: Issue): Promise<void> {
    const result = await firstValueFrom(
      this.dialog
        .open<AssignContractorDialog, { current: string | null }, { contractorId: string | null }>(AssignContractorDialog, {
          data: { current: i.contractor?.id ?? null },
        })
        .afterClosed(),
    );
    if (result) await this.run(this.api.assignContractor(this.id(), result.contractorId));
  }

  protected async changePriority(priority: IssuePriority): Promise<void> {
    await this.run(this.api.changeIssuePriority(this.id(), priority), `Priority changed to ${priority}; targets updated.`);
  }

  protected async comment(i: Issue): Promise<void> {
    const body = this.commentText.value.trim();
    if (!body) return;
    if (await this.run(this.api.commentOnIssue(i.id, body))) this.commentText.reset();
  }

  protected async addPhotos(i: Issue, input: HTMLInputElement): Promise<void> {
    const { ok, problems } = checkPhotoFiles(Array.from(input.files ?? []), i.photos.length);
    input.value = '';
    this.busy.set(true);
    for (const file of ok) {
      try {
        this.issue.set(await firstValueFrom(this.api.addIssuePhoto(i.id, file)));
      } catch (e) {
        problems.push(`${file.name}: ${errorMessage(e)}`);
      }
    }
    this.photoProblems.set(problems);
    this.busy.set(false);
  }

  private async ask(data: TextDialogData): Promise<string | undefined> {
    return firstValueFrom(this.dialog.open<TextDialog, TextDialogData, string>(TextDialog, { data }).afterClosed());
  }

  /** Runs one change; on failure says why and reloads, since someone else may have moved the issue on. */
  private async run(request: Observable<Issue>, done?: string): Promise<boolean> {
    this.busy.set(true);
    try {
      this.issue.set(await firstValueFrom(request));
      if (done) this.snack.open(done, 'Close', { duration: 4000 });
      return true;
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
      await this.load();
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}
