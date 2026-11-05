import { Component, computed, inject } from '@angular/core';
import { MatDividerModule } from '@angular/material/divider';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Role } from '../core/api.models';
import { Appearance, AppearanceService } from '../core/appearance';
import { AuthService } from '../core/auth.service';
import { Brand } from '../shared/brand';

interface NavItem {
  path: string;
  label: string;
  icon: string;
  roles: Role[];
}

const NAV: NavItem[] = [
  { path: '/properties', label: 'Properties', icon: 'apartment', roles: ['Landlord'] },
  { path: '/tenants', label: 'Tenants', icon: 'group', roles: ['Landlord'] },
  { path: '/leases', label: 'Leases', icon: 'contract', roles: ['Landlord'] },
  { path: '/payments', label: 'Payments', icon: 'payments', roles: ['Landlord'] },
  { path: '/issues', label: 'Issues', icon: 'build', roles: ['Landlord'] },
  { path: '/contractors', label: 'Contractors', icon: 'engineering', roles: ['Landlord'] },
  { path: '/messages', label: 'Messages', icon: 'chat', roles: ['Landlord'] },
  { path: '/my-details', label: 'Home', icon: 'home', roles: ['Tenant'] },
  { path: '/leases', label: 'My leases', icon: 'contract', roles: ['Tenant'] },
  { path: '/issues', label: 'My issues', icon: 'build', roles: ['Tenant'] },
  { path: '/my-messages', label: 'Messages', icon: 'chat', roles: ['Tenant'] },
  { path: '/users', label: 'Users', icon: 'manage_accounts', roles: ['Admin'] },
  { path: '/privacy', label: 'Privacy requests', icon: 'shield_person', roles: ['Admin'] },
  { path: '/settings', label: 'Settings', icon: 'settings', roles: ['Admin'] },
];

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatToolbarModule,
    MatSidenavModule,
    MatListModule,
    MatIconModule,
    MatButtonModule,
    MatMenuModule,
    MatDividerModule,
    MatTooltipModule,
    Brand,
  ],
  template: `
    <a class="skip-link" href="#main">Skip to content</a>
    <mat-toolbar class="toolbar app-bar" role="banner">
      <a class="home" [routerLink]="home()" aria-label="Mastene home"><app-brand /></a>
      <span class="spacer"></span>
      @if (auth.user(); as user) {
        <button mat-button type="button" class="account" [matMenuTriggerFor]="account"
                [attr.aria-label]="'Account: ' + user.displayName">
          <span class="avatar" aria-hidden="true">{{ initials() }}</span>
          <span class="who">{{ user.displayName }}</span>
          <mat-icon iconPositionEnd aria-hidden="true">expand_more</mat-icon>
        </button>
        <mat-menu #account="matMenu" xPosition="before">
          <div class="menu-head" role="presentation">
            <strong>{{ user.displayName }}</strong>
            <span>{{ roleLabel() }}</span>
          </div>
          <mat-divider />
          <div class="menu-label" role="presentation">Appearance</div>
          @for (option of appearances; track option.value) {
            <button mat-menu-item type="button" role="menuitemradio"
                    [attr.aria-checked]="appearance.current() === option.value" (click)="appearance.set(option.value)">
              <mat-icon aria-hidden="true">{{ appearance.current() === option.value ? 'radio_button_checked' : option.icon }}</mat-icon>
              <span>{{ option.label }}</span>
            </button>
          }
          <mat-divider />
          <button mat-menu-item type="button" (click)="auth.logout()">
            <mat-icon aria-hidden="true">logout</mat-icon>
            <span>Sign out</span>
          </button>
        </mat-menu>
      }
    </mat-toolbar>
    <mat-sidenav-container class="container">
      <mat-sidenav mode="side" opened class="nav">
        <nav aria-label="Main">
          <mat-nav-list>
            @for (item of items(); track item.label) {
              <a mat-list-item [routerLink]="item.path" routerLinkActive #rla="routerLinkActive"
                 [activated]="rla.isActive" [attr.aria-current]="rla.isActive ? 'page' : null"
                 [matTooltip]="item.label" matTooltipPosition="right" [matTooltipDisabled]="!compact">
                <mat-icon matListItemIcon aria-hidden="true" [class.filled]="rla.isActive">{{ item.icon }}</mat-icon>
                <span matListItemTitle>{{ item.label }}</span>
              </a>
            }
          </mat-nav-list>
        </nav>
      </mat-sidenav>
      <mat-sidenav-content>
        <main id="main" tabindex="-1" class="content">
          <router-outlet />
        </main>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: `
    :host { display: flex; flex-direction: column; height: 100%; }
    .toolbar { height: 64px; padding: 0 12px 0 16px; gap: 8px; position: relative; z-index: 2; }
    .home { color: inherit; text-decoration: none; display: inline-flex; border-radius: 8px; padding: 4px; }
    .spacer { flex: 1; }
    .account { height: 44px; border-radius: 22px; padding: 0 8px 0 4px; }
    .avatar { display: inline-grid; place-items: center; width: 32px; height: 32px; border-radius: 50%;
              background: var(--app-logo-bg); color: var(--app-logo-ink); font: var(--mat-sys-label-large);
              font-weight: 600; margin-right: 8px; }
    .who { font: var(--mat-sys-body-large); }
    .menu-head { display: flex; flex-direction: column; padding: 12px 16px; gap: 2px; min-width: 220px; }
    .menu-head span, .menu-label { color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-small); }
    .menu-label { padding: 12px 16px 4px; font: var(--mat-sys-label-medium); text-transform: uppercase; letter-spacing: 0.06em; }
    .container { flex: 1; }
    .nav { width: 232px; padding: 12px 0; overflow-x: hidden; }
    mat-nav-list a { margin: 2px 12px; width: auto; border-radius: 28px; }
    .filled { font-variation-settings: 'FILL' 1; }
    .content { padding: 28px 32px 56px; max-width: 1280px; margin: 0 auto; outline: none; box-sizing: border-box; }
    .skip-link { position: absolute; left: -9999px; }
    .skip-link:focus { left: 8px; top: 8px; z-index: 10; background: var(--mat-sys-surface); color: var(--mat-sys-primary); padding: 8px 12px; border-radius: 8px; }
    @media (max-width: 720px) {
      .nav { width: 72px; }
      mat-nav-list a { margin: 2px 8px; }
      .nav [matListItemTitle], .who { display: none; }
      .content { padding: 16px; }
    }
  `,
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly appearance = inject(AppearanceService);
  protected readonly items = computed(() => NAV.filter((i) => this.auth.hasRole(...i.roles)));
  protected readonly home = computed(() => this.auth.homeFor(this.auth.user()?.role ?? null));
  protected readonly initials = computed(() =>
    (this.auth.user()?.displayName ?? '')
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((w) => w[0].toUpperCase())
      .join(''),
  );
  protected readonly roleLabel = computed(() => {
    const role = this.auth.user()?.role;
    return role === 'Admin' ? 'Administrator' : (role ?? '');
  });
  protected readonly appearances: { value: Appearance; label: string; icon: string }[] = [
    { value: 'system', label: 'Same as device', icon: 'brightness_auto' },
    { value: 'light', label: 'Light', icon: 'light_mode' },
    { value: 'dark', label: 'Dark', icon: 'dark_mode' },
  ];
  /** Labels are hidden on narrow screens, so the icons get tooltips there. */
  protected readonly compact = typeof matchMedia === 'function' && matchMedia('(max-width: 720px)').matches;
}
