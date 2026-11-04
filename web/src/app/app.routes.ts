import { Routes } from '@angular/router';
import { homeRedirect, roleGuard, signedOutOnly } from './core/guards';
import { Shell } from './layout/shell';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [signedOutOnly],
    title: 'Sign in · Tenant Manager',
    loadComponent: () => import('./features/login/login').then((m) => m.Login),
  },
  {
    path: '',
    component: Shell,
    children: [
      {
        path: 'properties',
        canActivate: [roleGuard('Landlord')],
        title: 'Properties · Tenant Manager',
        loadComponent: () => import('./features/properties/property-list').then((m) => m.PropertyList),
      },
      {
        path: 'properties/:id',
        canActivate: [roleGuard('Landlord')],
        title: 'Property · Tenant Manager',
        loadComponent: () => import('./features/properties/property-detail').then((m) => m.PropertyDetail),
      },
      {
        path: 'tenants',
        canActivate: [roleGuard('Landlord')],
        title: 'Tenants · Tenant Manager',
        loadComponent: () => import('./features/tenants/tenant-list').then((m) => m.TenantList),
      },
      {
        path: 'leases',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Leases · Tenant Manager',
        loadComponent: () => import('./features/leases/lease-list').then((m) => m.LeaseList),
      },
      {
        path: 'leases/:id',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Lease · Tenant Manager',
        loadComponent: () => import('./features/leases/lease-detail').then((m) => m.LeaseDetail),
      },
      {
        path: 'payments',
        canActivate: [roleGuard('Landlord')],
        title: 'Payments · Tenant Manager',
        loadComponent: () => import('./features/payments/payments-page').then((m) => m.PaymentsPage),
      },
      {
        path: 'settings',
        canActivate: [roleGuard('Admin')],
        title: 'Settings · Tenant Manager',
        loadComponent: () => import('./features/settings/settings').then((m) => m.SettingsPage),
      },
      {
        path: 'users',
        canActivate: [roleGuard('Admin')],
        title: 'Users · Tenant Manager',
        loadComponent: () => import('./features/users/user-list').then((m) => m.UserList),
      },
      {
        path: 'my-details',
        canActivate: [roleGuard('Tenant')],
        title: 'My details · Tenant Manager',
        loadComponent: () => import('./features/my-details/my-details').then((m) => m.MyDetails),
      },
      { path: '', pathMatch: 'full', canActivate: [homeRedirect], children: [] },
    ],
  },
  { path: '**', redirectTo: '' },
];
