import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ConsentStatus, IssueSummary, Ledger, LeaseSummary, Tenant } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';
import { saveBlob } from '../../shared/download';
import { NewIssueDialog, NewIssueResult } from '../issues/issue-dialogs';
import { IssueStatusChip } from '../issues/issue-shared';
import { LeaseStatusChip } from '../leases/lease-shared';
import { ConsentPanel } from '../messages/messages';
import { currentLease, greeting, leaseProgress, rentSummary } from './tenant-home';

/**
 * The tenant's home: a greeting with the three things tenants do most (report an issue, upload proof of
 * payment, message the landlord), then rent, lease and repairs at a glance, then their details and
 * WhatsApp consent.
 */
@Component({
  selector: 'app-my-details',
  imports: [
    CurrencyPipe, DatePipe, RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule, ConsentPanel,
    IssueStatusChip, LeaseStatusChip,
  ],
  template: `
    <section class="app-hero" aria-labelledby="hello">
      <svg class="app-hero-art" viewBox="0 0 200 200" aria-hidden="true" focusable="false">
        <path d="M30 170V75l70-45 70 45v95h-40v-68l-30 22-30-22v68z" />
      </svg>
      <div class="app-hero-text">
        <p class="app-hero-eyebrow">{{ todayLabel }}</p>
        <h1 id="hello" class="app-hero-title">{{ hello }}{{ firstName() ? ', ' + firstName() : '' }}</h1>
        @if (lease(); as l) {
          <p class="app-hero-sub"><mat-icon aria-hidden="true">location_on</mat-icon>{{ l.unitLabel }} · {{ l.propertyName }}</p>
        } @else {
          <p class="app-hero-sub">Welcome to Mastene.</p>
        }
      </div>
      <div class="app-hero-actions">
        <button mat-flat-button type="button" class="app-on-dark-primary" (click)="report()">
          <mat-icon aria-hidden="true">build</mat-icon> Report an issue
        </button>
        @if (lease(); as l) {
          <a mat-stroked-button class="app-on-dark" [routerLink]="['/leases', l.id]">
            <mat-icon aria-hidden="true">upload_file</mat-icon> Upload proof of payment
          </a>
        }
        <a mat-stroked-button class="app-on-dark" routerLink="/my-messages">
          <mat-icon aria-hidden="true">chat</mat-icon> Message your landlord
        </a>
      </div>
    </section>

    @if (loading()) { <mat-progress-bar mode="indeterminate" aria-label="Loading your home page" /> }
    @if (error()) { <p class="error" role="alert"><mat-icon aria-hidden="true">error</mat-icon>{{ error() }}</p> }

    <div class="overview">
      <section class="app-card stat" aria-labelledby="rent-h">
        <header><span class="icon-badge" aria-hidden="true"><mat-icon>payments</mat-icon></span><h2 id="rent-h">Rent</h2></header>
        @if (rent(); as r) {
          <p class="big">
            @if (r.amount !== null) { {{ r.amount | currency: 'ZAR' : 'symbol-narrow' }} } @else { {{ lease()!.monthlyRent | currency: 'ZAR' : 'symbol-narrow' }}<span class="per"> / month</span> }
          </p>
          <span [class]="'app-chip ' + r.tone"><mat-icon aria-hidden="true">{{ r.icon }}</mat-icon>{{ r.title }}</span>
          <p class="detail">{{ r.detail }}</p>
          <a class="more" [routerLink]="['/leases', lease()!.id]">View rent statement <mat-icon aria-hidden="true">chevron_right</mat-icon></a>
        } @else if (!loading()) {
          <p class="detail">Rent shows here once you have a lease.</p>
        }
      </section>

      <section class="app-card stat" aria-labelledby="lease-h">
        <header><span class="icon-badge" aria-hidden="true"><mat-icon>contract</mat-icon></span><h2 id="lease-h">Your lease</h2></header>
        @if (lease(); as l) {
          <p class="big small-big">{{ l.startDate | date: 'd MMM y' }} – {{ l.endDate | date: 'd MMM y' }}</p>
          <app-lease-status [status]="l.status" [monthToMonth]="l.isMonthToMonth" />
          @if (progress(); as p) {
            <div class="term">
              <mat-progress-bar mode="determinate" [value]="p.percent" [attr.aria-label]="p.percent + '% of the lease term has passed'" />
              <p class="detail">
                @if (l.isMonthToMonth) { Month to month, {{ l.monthlyRent | currency: 'ZAR' : 'symbol-narrow' }} a month }
                @else if (p.daysLeft > 0) { {{ p.daysLeft }} {{ p.daysLeft === 1 ? 'day' : 'days' }} left · {{ l.leaseNumber }} }
                @else { The term has ended · {{ l.leaseNumber }} }
              </p>
            </div>
          }
          <a class="more" [routerLink]="['/leases', l.id]">View lease <mat-icon aria-hidden="true">chevron_right</mat-icon></a>
        } @else if (!loading()) {
          <p class="detail">No lease yet. Your landlord will send it to you to sign.</p>
        }
      </section>

      <section class="app-card stat" aria-labelledby="issues-h">
        <header><span class="icon-badge" aria-hidden="true"><mat-icon>build</mat-icon></span><h2 id="issues-h">Repairs</h2></header>
        <p class="big">{{ openIssues().length }}<span class="per"> open</span></p>
        @if (openIssues().length) {
          <ul class="issues">
            @for (i of openIssues().slice(0, 3); track i.id) {
              <li>
                <a [routerLink]="['/issues', i.id]">{{ i.title }}</a>
                <app-issue-status [status]="i.status" />
              </li>
            }
          </ul>
        } @else if (!loading()) {
          <p class="detail">Nothing reported. If something breaks, report it and track it here.</p>
        }
        <a class="more" routerLink="/issues">All my issues <mat-icon aria-hidden="true">chevron_right</mat-icon></a>
      </section>
    </div>

    <div class="lower">
      @if (tenant(); as t) {
        <section class="app-card" aria-labelledby="details-h">
          <h2 id="details-h" class="section-title">Your details</h2>
          <dl class="details">
            <div><dt><mat-icon aria-hidden="true">person</mat-icon>Name</dt><dd>{{ t.firstName }} {{ t.lastName }}</dd></div>
            <div><dt><mat-icon aria-hidden="true">mail</mat-icon>Email</dt><dd>{{ t.email }}</dd></div>
            <div><dt><mat-icon aria-hidden="true">call</mat-icon>Mobile</dt><dd>{{ t.phone }}</dd></div>
          </dl>
          <p class="detail">To change these details, contact your landlord.</p>
          <div class="data">
            <button mat-stroked-button type="button" (click)="download(t.id)" [disabled]="busy()">
              <mat-icon aria-hidden="true">download</mat-icon> Download all my data
            </button>
            <p class="detail">A zip with everything Mastene holds about you, including your lease and receipts.</p>
          </div>
        </section>
        @if (consent(); as c) {
          <app-consent-panel [tenantId]="t.id" [consent]="c" (changed)="consent.set($event)" />
        }
      }
    </div>
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 20px; }
    .overview { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px; }
    .stat { display: flex; flex-direction: column; align-items: flex-start; gap: 10px; }
    .stat header { display: flex; align-items: center; gap: 10px; }
    .icon-badge { display: inline-grid; place-items: center; width: 36px; height: 36px; border-radius: 10px;
                  background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .icon-badge mat-icon { font-size: 20px; width: 20px; height: 20px; }
    h2 { margin: 0; font: var(--mat-sys-title-medium); font-weight: 600; }
    .big { margin: 4px 0 0; font: var(--mat-sys-headline-small); font-weight: 600; font-variant-numeric: tabular-nums; }
    .small-big { font: var(--mat-sys-title-large); font-weight: 600; }
    .per { font: var(--mat-sys-body-large); color: var(--mat-sys-on-surface-variant); font-weight: 400; }
    .detail { margin: 0; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-medium); }
    .term { width: 100%; display: flex; flex-direction: column; gap: 8px; margin-top: 2px; }
    .term mat-progress-bar { border-radius: 4px; --mat-progress-bar-track-height: 6px; --mat-progress-bar-active-indicator-height: 6px; }
    .more { margin-top: auto; padding-top: 4px; display: inline-flex; align-items: center; gap: 2px; font: var(--mat-sys-label-large);
            color: var(--mat-sys-primary); text-decoration: none; }
    .more:hover { text-decoration: underline; }
    .more mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .issues { list-style: none; margin: 0; padding: 0; width: 100%; display: flex; flex-direction: column; }
    .issues li { display: flex; justify-content: space-between; align-items: center; gap: 8px; padding: 8px 0;
                 border-top: 1px solid var(--mat-sys-outline-variant); }
    .issues a { color: var(--mat-sys-on-surface); font-weight: 500; text-decoration: none; min-width: 0; overflow: hidden;
                text-overflow: ellipsis; white-space: nowrap; }
    .issues a:hover { text-decoration: underline; }

    .lower { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 16px; align-items: start; }
    .section-title { margin-bottom: 12px; }
    .details { margin: 0 0 12px; display: grid; gap: 2px; }
    .details > div { display: grid; grid-template-columns: 120px minmax(0, 1fr); align-items: center; gap: 12px; padding: 10px 0;
                     border-bottom: 1px solid var(--mat-sys-outline-variant); }
    dt { display: flex; align-items: center; gap: 8px; color: var(--mat-sys-on-surface-variant); }
    dt mat-icon { font-size: 20px; width: 20px; height: 20px; }
    dd { margin: 0; font-weight: 500; overflow-wrap: anywhere; }
    .data { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; margin-top: 16px; padding-top: 16px;
            border-top: 1px solid var(--mat-sys-outline-variant); }
    .error { display: flex; gap: 8px; align-items: center; color: var(--mat-sys-on-error-container); background: var(--mat-sys-error-container);
             padding: 10px 14px; border-radius: 10px; margin: 0; }

    @media (max-width: 1000px) { .overview { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); } }
    @media (max-width: 760px) {
      .overview, .lower { grid-template-columns: minmax(0, 1fr); }
      .details > div { grid-template-columns: minmax(0, 1fr); gap: 2px; }
    }
  `,
})
export class MyDetails {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);

  protected readonly tenant = signal<Tenant | null>(null);
  protected readonly consent = signal<ConsentStatus | null>(null);
  protected readonly lease = signal<LeaseSummary | null>(null);
  protected readonly ledger = signal<Ledger | null>(null);
  protected readonly openIssues = signal<IssueSummary[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);

  private readonly now = new Date();
  /** Local date as yyyy-MM-dd. */
  private readonly today = `${this.now.getFullYear()}-${String(this.now.getMonth() + 1).padStart(2, '0')}-${String(this.now.getDate()).padStart(2, '0')}`;
  protected readonly hello = greeting(this.now.getHours());
  protected readonly todayLabel = this.now.toLocaleDateString('en-ZA', { weekday: 'long', day: 'numeric', month: 'long' });

  protected readonly firstName = computed(
    () => this.tenant()?.firstName ?? this.auth.user()?.displayName.split(' ')[0] ?? '',
  );
  protected readonly progress = computed(() => {
    const l = this.lease();
    return l ? leaseProgress(l.startDate, l.terminatesOn ?? l.endDate, this.today) : null;
  });
  protected readonly rent = computed(() => {
    const ledger = this.ledger();
    return ledger && this.lease() ? rentSummary(ledger, this.today) : null;
  });

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    const tenantId = this.auth.user()?.tenantId;
    if (!tenantId) {
      this.loading.set(false);
      return;
    }
    const fail = (e: unknown) => this.error.set(errorMessage(e));
    await Promise.all([
      firstValueFrom(this.api.getTenant(tenantId)).then((t) => this.tenant.set(t), fail),
      firstValueFrom(this.api.getConsent(tenantId)).then((c) => this.consent.set(c), fail),
      firstValueFrom(this.api.listIssues({ openOnly: true, pageSize: 20, sort: '-openedAt' })).then((p) => this.openIssues.set(p.items), fail),
      firstValueFrom(this.api.listLeases({ pageSize: 20, sort: '-startDate' })).then(async (p) => {
        const lease = currentLease(p.items);
        this.lease.set(lease);
        if (lease && lease.status !== 'Draft' && lease.status !== 'PendingSignature') {
          this.ledger.set(await firstValueFrom(this.api.getLedger(lease.id)));
        }
      }).catch(fail),
    ]).finally(() => this.loading.set(false));
  }

  protected async report(): Promise<void> {
    const result = await firstValueFrom(
      this.dialog.open<NewIssueDialog, null, NewIssueResult>(NewIssueDialog, { data: null, width: '640px' }).afterClosed(),
    );
    if (!result) return;
    if (result.photoProblems.length)
      this.snack.open(`Issue logged, but some photos weren't added: ${result.photoProblems.join('; ')}`, 'Close');
    await this.router.navigate(['/issues', result.issue.id]);
  }

  protected async download(tenantId: string): Promise<void> {
    this.busy.set(true);
    try {
      saveBlob(await firstValueFrom(this.api.exportTenantData(tenantId)), 'my-tenant-data.zip');
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
