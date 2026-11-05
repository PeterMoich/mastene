import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Arrears, IssueSummary, LeaseSummary, Payment, PropertySummary } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';
import { EscalatedBadge, IssuePriorityLabel, IssueStatusChip } from '../issues/issue-shared';
import { greeting } from '../my-details/tenant-home';
import {
  arrearsTotal,
  issuesNeedingAttention,
  leasesEndingSoon,
  localToday,
  occupancy,
  rentCollection,
} from './dashboard-data';

/**
 * The landlord's home: this month's rent, arrears, occupancy and open issues at a glance, then the work
 * waiting for them (payments to check, escalated issues, tenants behind on rent) and leases ending soon.
 * Built from the same endpoints as the list pages, so every figure matches what those pages show.
 */
@Component({
  selector: 'app-dashboard',
  imports: [
    CurrencyPipe, DatePipe, RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule,
    EscalatedBadge, IssuePriorityLabel, IssueStatusChip,
  ],
  template: `
    <section class="app-hero" aria-labelledby="hello">
      <svg class="app-hero-art" viewBox="0 0 200 200" aria-hidden="true" focusable="false">
        <path d="M30 170V75l70-45 70 45v95h-40v-68l-30 22-30-22v68z" />
      </svg>
      <div class="app-hero-text">
        <p class="app-hero-eyebrow">{{ todayLabel }}</p>
        <h1 id="hello" class="app-hero-title">{{ hello }}{{ firstName ? ', ' + firstName : '' }}</h1>
        <p class="app-hero-sub">{{ summary() }}</p>
      </div>
      <div class="app-hero-actions">
        <a mat-flat-button class="app-on-dark-primary" routerLink="/payments">
          <mat-icon aria-hidden="true">fact_check</mat-icon>
          {{ toReview().length ? 'Review ' + toReview().length + ' payment' + (toReview().length === 1 ? '' : 's') : 'Payments' }}
        </a>
        <a mat-stroked-button class="app-on-dark" routerLink="/leases"><mat-icon aria-hidden="true">contract</mat-icon> Leases</a>
        <a mat-stroked-button class="app-on-dark" routerLink="/messages"><mat-icon aria-hidden="true">chat</mat-icon> Messages</a>
      </div>
    </section>

    @if (loading()) { <mat-progress-bar mode="indeterminate" aria-label="Loading the dashboard" /> }
    @if (error()) { <p class="error" role="alert"><mat-icon aria-hidden="true">error</mat-icon>{{ error() }}</p> }

    <div class="kpis">
      <a class="app-card kpi" routerLink="/payments" aria-labelledby="k-rent">
        <div class="app-card-head"><span class="app-icon-tile" aria-hidden="true"><mat-icon>payments</mat-icon></span><h2 id="k-rent">Rent this month</h2></div>
        <p class="figure">{{ rent().collected | currency: 'ZAR' : 'symbol-narrow' : '1.0-0' }}</p>
        <mat-progress-bar mode="determinate" [value]="rent().percent" [attr.aria-label]="rent().percent + '% of expected rent collected'" />
        <p class="note">{{ rent().percent }}% of {{ rent().expected | currency: 'ZAR' : 'symbol-narrow' : '1.0-0' }} collected</p>
      </a>
      <a class="app-card kpi" routerLink="/payments" aria-labelledby="k-arrears">
        <div class="app-card-head">
          <span class="app-icon-tile" [class.danger]="arrears().total > 0" [class.success]="arrears().total === 0" aria-hidden="true">
            <mat-icon>{{ arrears().total > 0 ? 'warning' : 'verified' }}</mat-icon>
          </span>
          <h2 id="k-arrears">Arrears</h2>
        </div>
        <p class="figure" [class.bad]="arrears().total > 0">{{ arrears().total | currency: 'ZAR' : 'symbol-narrow' : '1.0-0' }}</p>
        <p class="note">
          @if (arrears().tenants) { {{ arrears().tenants }} {{ arrears().tenants === 1 ? 'tenant is' : 'tenants are' }} behind } @else { Everyone is up to date }
        </p>
      </a>
      <a class="app-card kpi" routerLink="/properties" aria-labelledby="k-occ">
        <div class="app-card-head"><span class="app-icon-tile info" aria-hidden="true"><mat-icon>apartment</mat-icon></span><h2 id="k-occ">Occupancy</h2></div>
        <p class="figure">{{ occ().percent }}%</p>
        <mat-progress-bar mode="determinate" [value]="occ().percent" [attr.aria-label]="occ().percent + '% of units occupied'" />
        <p class="note">{{ occ().occupied }} of {{ occ().units }} units let{{ occ().vacant ? ' · ' + occ().vacant + ' vacant' : '' }}</p>
      </a>
      <a class="app-card kpi" routerLink="/issues" aria-labelledby="k-issues">
        <div class="app-card-head">
          <span class="app-icon-tile" [class.warning]="escalatedCount() > 0" aria-hidden="true"><mat-icon>build</mat-icon></span>
          <h2 id="k-issues">Open issues</h2>
        </div>
        <p class="figure">{{ issues().length }}</p>
        <p class="note">
          @if (escalatedCount()) { <span class="bad">{{ escalatedCount() }} escalated</span>, past a response or fix target }
          @else if (issues().length) { All within their targets } @else { Nothing open }
        </p>
      </a>
    </div>

    <div class="columns">
      <section class="app-card" aria-labelledby="attention-h">
        <div class="app-card-head"><span class="app-icon-tile warning" aria-hidden="true"><mat-icon>notifications_active</mat-icon></span><h2 id="attention-h">Needs your attention</h2></div>

        <h3>Payments to check <span class="count">{{ toReview().length }}</span></h3>
        @if (toReview().length) {
          <ul class="rows">
            @for (p of toReview().slice(0, 4); track p.id) {
              <li>
                <div class="main"><strong>{{ p.tenantName }}</strong><span class="sub">{{ p.unitLabel }}, {{ p.propertyName }} · paid {{ p.paidOn | date: 'd MMM' }}</span></div>
                <span class="amount">{{ p.amount | currency: 'ZAR' : 'symbol-narrow' : '1.0-0' }}</span>
              </li>
            }
          </ul>
          <a class="app-more" routerLink="/payments">Review payments <mat-icon aria-hidden="true">chevron_right</mat-icon></a>
        } @else {
          <p class="empty"><mat-icon aria-hidden="true">task_alt</mat-icon>No proofs of payment waiting.</p>
        }

        <h3>Issues <span class="count">{{ issues().length }}</span></h3>
        @if (issues().length) {
          <ul class="rows">
            @for (i of issues().slice(0, 4); track i.id) {
              <li>
                <div class="main">
                  <a [routerLink]="['/issues', i.id]">{{ i.title }}</a>
                  <span class="sub">{{ i.unitLabel }}, {{ i.propertyName }} · <app-issue-priority [priority]="i.priority" />
                    @if (i.isEscalated) { <app-escalated /> }</span>
                </div>
                <app-issue-status [status]="i.status" />
              </li>
            }
          </ul>
          <a class="app-more" routerLink="/issues">All issues <mat-icon aria-hidden="true">chevron_right</mat-icon></a>
        } @else {
          <p class="empty"><mat-icon aria-hidden="true">task_alt</mat-icon>No open issues.</p>
        }

        <h3>Behind on rent <span class="count">{{ arrearsList().length }}</span></h3>
        @if (arrearsList().length) {
          <ul class="rows">
            @for (a of arrearsList().slice(0, 4); track a.leaseId) {
              <li>
                <div class="main"><a [routerLink]="['/leases', a.leaseId]">{{ a.tenantName }}</a>
                  <span class="sub">{{ a.unitLabel }}, {{ a.propertyName }} · {{ a.daysOverdue }} {{ a.daysOverdue === 1 ? 'day' : 'days' }} overdue</span></div>
                <span class="amount bad">{{ a.arrears | currency: 'ZAR' : 'symbol-narrow' : '1.0-0' }}</span>
              </li>
            }
          </ul>
        } @else {
          <p class="empty"><mat-icon aria-hidden="true">task_alt</mat-icon>Nobody is behind on rent.</p>
        }
      </section>

      <div class="side">
        <section class="app-card" aria-labelledby="ending-h">
          <div class="app-card-head"><span class="app-icon-tile" aria-hidden="true"><mat-icon>event</mat-icon></span><h2 id="ending-h">Leases ending soon</h2></div>
          @if (ending().length) {
            <ul class="rows">
              @for (e of ending().slice(0, 5); track e.lease.id) {
                <li>
                  <div class="main"><a [routerLink]="['/leases', e.lease.id]">{{ e.lease.tenantName }}</a>
                    <span class="sub">{{ e.lease.unitLabel }}, {{ e.lease.propertyName }} · ends {{ e.endsOn | date: 'd MMM y' }}</span></div>
                  <span [class]="'app-chip sm ' + (e.daysLeft <= 30 ? 'warning' : 'neutral')">
                    {{ e.daysLeft === 0 ? 'Today' : e.daysLeft + (e.daysLeft === 1 ? ' day' : ' days') }}
                  </span>
                </li>
              }
            </ul>
          } @else {
            <p class="empty"><mat-icon aria-hidden="true">event_available</mat-icon>No leases end in the next 60 days.</p>
          }
          <a class="app-more" routerLink="/leases">All leases <mat-icon aria-hidden="true">chevron_right</mat-icon></a>
        </section>

        <section class="app-card" aria-labelledby="props-h">
          <div class="app-card-head"><span class="app-icon-tile info" aria-hidden="true"><mat-icon>home_work</mat-icon></span><h2 id="props-h">Properties</h2></div>
          <ul class="rows">
            @for (p of properties(); track p.id) {
              <li class="prop">
                <div class="main"><a [routerLink]="['/properties', p.id]">{{ p.name }}</a><span class="sub">{{ p.city }}</span></div>
                <div class="occ">
                  <span class="sub">{{ p.occupiedCount }}/{{ p.unitCount }} let</span>
                  <mat-progress-bar mode="determinate" [value]="p.unitCount ? (p.occupiedCount / p.unitCount) * 100 : 0"
                                    [attr.aria-label]="p.name + ': ' + p.occupiedCount + ' of ' + p.unitCount + ' units let'" />
                </div>
              </li>
            } @empty {
              <li class="empty-row">No properties yet. <a routerLink="/properties">Add your first one</a>.</li>
            }
          </ul>
        </section>
      </div>
    </div>
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 20px; }
    .error { display: flex; gap: 8px; align-items: center; color: var(--mat-sys-on-error-container); background: var(--mat-sys-error-container);
             padding: 10px 14px; border-radius: 10px; margin: 0; }

    .kpis { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 16px; }
    .kpi { display: flex; flex-direction: column; gap: 10px; color: inherit; text-decoration: none;
           transition: border-color 120ms ease, box-shadow 120ms ease; }
    .kpi:hover { border-color: var(--mat-sys-outline); box-shadow: 0 4px 16px color-mix(in srgb, var(--mat-sys-on-surface) 8%, transparent); }
    .figure { margin: 4px 0 0; font: var(--mat-sys-headline-medium); font-weight: 600; font-variant-numeric: tabular-nums; }
    .note { margin: 0; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-medium); }
    .bad { color: var(--mat-sys-error); }
    mat-progress-bar { border-radius: 4px; --mat-progress-bar-track-height: 6px; --mat-progress-bar-active-indicator-height: 6px; }

    .columns { display: grid; grid-template-columns: minmax(0, 1.4fr) minmax(0, 1fr); gap: 16px; align-items: start; }
    .side { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
    section.app-card { display: flex; flex-direction: column; gap: 10px; }
    h3 { margin: 14px 0 0; font: var(--mat-sys-title-small); display: flex; align-items: center; gap: 8px; }
    .count { min-width: 22px; height: 22px; padding: 0 6px; border-radius: 11px; display: inline-grid; place-items: center;
             background: var(--mat-sys-surface-container-high); font: var(--mat-sys-label-medium); box-sizing: border-box; }
    .rows { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; }
    .rows li { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 0;
               border-top: 1px solid var(--mat-sys-outline-variant); }
    .main { display: flex; flex-direction: column; min-width: 0; gap: 2px; }
    .main a, .main strong { color: var(--mat-sys-on-surface); font-weight: 500; text-decoration: none; overflow: hidden;
                            text-overflow: ellipsis; white-space: nowrap; }
    .main a:hover { text-decoration: underline; color: var(--mat-sys-primary); }
    .sub { color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-small); display: inline-flex; flex-wrap: wrap;
           align-items: center; gap: 4px; }
    .amount { font-weight: 600; font-variant-numeric: tabular-nums; white-space: nowrap; }
    .empty { display: flex; align-items: center; gap: 8px; margin: 0; padding: 10px 0; color: var(--mat-sys-on-surface-variant);
             border-top: 1px solid var(--mat-sys-outline-variant); }
    .empty mat-icon { color: var(--app-on-success-container); font-size: 20px; width: 20px; height: 20px; }
    .empty-row { color: var(--mat-sys-on-surface-variant); }
    .prop .occ { display: flex; flex-direction: column; align-items: flex-end; gap: 4px; width: 120px; flex: none; }
    .prop .occ mat-progress-bar { width: 100%; }
    .app-more { margin-top: 4px; }

    @media (max-width: 1100px) { .kpis { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
    @media (max-width: 860px) { .columns { grid-template-columns: minmax(0, 1fr); } }
    @media (max-width: 520px) { .kpis { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class Dashboard {
  private readonly api = inject(ApiService);
  private readonly now = new Date();
  private readonly today = localToday(this.now);

  protected readonly hello = greeting(this.now.getHours());
  protected readonly todayLabel = this.now.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' });
  protected readonly firstName = inject(AuthService).user()?.displayName.split(' ')[0] ?? '';

  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly properties = signal<PropertySummary[]>([]);
  private readonly leases = signal<LeaseSummary[]>([]);
  private readonly verified = signal<Payment[]>([]);
  protected readonly toReview = signal<Payment[]>([]);
  protected readonly arrearsList = signal<Arrears[]>([]);
  private readonly openIssues = signal<IssueSummary[]>([]);

  protected readonly rent = computed(() => rentCollection(this.leases(), this.verified(), this.today));
  protected readonly occ = computed(() => occupancy(this.properties()));
  protected readonly arrears = computed(() => arrearsTotal(this.arrearsList()));
  protected readonly issues = computed(() => issuesNeedingAttention(this.openIssues()));
  protected readonly escalatedCount = computed(() => this.openIssues().filter((i) => i.isEscalated).length);
  protected readonly ending = computed(() => leasesEndingSoon(this.leases(), this.today));
  protected readonly summary = computed(() => {
    if (this.loading()) return 'Here is how your rentals are doing.';
    const parts: string[] = [];
    const review = this.toReview().length;
    if (review) parts.push(`${review} payment${review === 1 ? '' : 's'} to check`);
    if (this.escalatedCount()) parts.push(`${this.escalatedCount()} escalated issue${this.escalatedCount() === 1 ? '' : 's'}`);
    if (this.arrears().tenants) parts.push(`${this.arrears().tenants} tenant${this.arrears().tenants === 1 ? '' : 's'} behind on rent`);
    return parts.length ? `Waiting for you: ${parts.join(', ')}.` : 'Nothing is waiting for you. All caught up.';
  });

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    const fail = (e: unknown) => this.error.set(errorMessage(e));
    await Promise.all([
      firstValueFrom(this.api.listProperties({ pageSize: 100, sort: 'name' })).then((p) => this.properties.set(p.items), fail),
      firstValueFrom(this.api.listLeases({ pageSize: 100, sort: '-startDate' })).then((p) => this.leases.set(p.items), fail),
      firstValueFrom(this.api.listPayments({ status: 'Verified', pageSize: 100, sort: '-paidOn' })).then((p) => this.verified.set(p.items), fail),
      firstValueFrom(this.api.listPayments({ status: 'Submitted', pageSize: 100, sort: 'paidOn' })).then((p) => this.toReview.set(p.items), fail),
      firstValueFrom(this.api.listArrears()).then((a) => this.arrearsList.set([...a].sort((x, y) => y.arrears - x.arrears)), fail),
      firstValueFrom(this.api.listIssues({ openOnly: true, pageSize: 100 })).then((p) => this.openIssues.set(p.items), fail),
    ]).finally(() => this.loading.set(false));
  }
}
