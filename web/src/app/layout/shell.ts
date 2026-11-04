import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Role } from '../core/api.models';
import { AuthService } from '../core/auth.service';

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
  { path: '/my-details', label: 'My details', icon: 'person', roles: ['Tenant'] },
  { path: '/leases', label: 'My leases', icon: 'contract', roles: ['Tenant'] },
  { path: '/issues', label: 'My issues', icon: 'build', roles: ['Tenant'] },
  { path: '/users', label: 'Users', icon: 'manage_accounts', roles: ['Admin'] },
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
  ],
  template: `
    <a class="skip-link" href="#main">Skip to content</a>
    <mat-toolbar class="toolbar">
      <span class="brand">Tenant Manager</span>
      <span class="spacer"></span>
      @if (auth.user(); as user) {
        <span class="who">{{ user.displayName }} · {{ user.role }}</span>
        <button mat-button type="button" (click)="auth.logout()">
          <mat-icon aria-hidden="true">logout</mat-icon> Sign out
        </button>
      }
    </mat-toolbar>
    <mat-sidenav-container class="container">
      <mat-sidenav mode="side" opened class="nav">
        <nav aria-label="Main">
          <mat-nav-list>
            @for (item of items(); track item.label) {
              <a mat-list-item [routerLink]="item.path" routerLinkActive #rla="routerLinkActive"
                 [activated]="rla.isActive" [attr.aria-current]="rla.isActive ? 'page' : null">
                <mat-icon matListItemIcon aria-hidden="true">{{ item.icon }}</mat-icon>
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
    .toolbar { background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .brand { font: var(--mat-sys-title-large); }
    .spacer { flex: 1; }
    .who { margin-right: 12px; font: var(--mat-sys-body-medium); }
    .container { flex: 1; }
    .nav { width: 220px; }
    .content { padding: 24px; max-width: 1200px; outline: none; }
    .skip-link { position: absolute; left: -9999px; }
    .skip-link:focus { left: 8px; top: 8px; z-index: 10; background: var(--mat-sys-surface); padding: 8px; }
    @media (max-width: 720px) {
      .nav { width: 72px; }
      .nav [matListItemTitle], .who { display: none; }
      .content { padding: 16px; }
    }
  `,
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly items = computed(() => NAV.filter((i) => this.auth.hasRole(...i.roles)));
}
