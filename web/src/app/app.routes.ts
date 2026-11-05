import { Routes } from '@angular/router';
import { homeRedirect, roleGuard, signedOutOnly } from './core/guards';
import { Shell } from './layout/shell';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [signedOutOnly],
    title: 'Sign in · Mastene',
    loadComponent: () => import('./features/login/login').then((m) => m.Login),
  },
  {
    path: '',
    component: Shell,
    children: [
      {
        path: 'properties',
        canActivate: [roleGuard('Landlord')],
        title: 'Properties · Mastene',
        loadComponent: () => import('./features/properties/property-list').then((m) => m.PropertyList),
      },
      {
        path: 'properties/:id',
        canActivate: [roleGuard('Landlord')],
        title: 'Property · Mastene',
        loadComponent: () => import('./features/properties/property-detail').then((m) => m.PropertyDetail),
      },
      {
        path: 'tenants',
        canActivate: [roleGuard('Landlord')],
        title: 'Tenants · Mastene',
        loadComponent: () => import('./features/tenants/tenant-list').then((m) => m.TenantList),
      },
      {
        path: 'leases',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Leases · Mastene',
        loadComponent: () => import('./features/leases/lease-list').then((m) => m.LeaseList),
      },
      {
        path: 'leases/:id',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Lease · Mastene',
        loadComponent: () => import('./features/leases/lease-detail').then((m) => m.LeaseDetail),
      },
      {
        path: 'payments',
        canActivate: [roleGuard('Landlord')],
        title: 'Payments · Mastene',
        loadComponent: () => import('./features/payments/payments-page').then((m) => m.PaymentsPage),
      },
      {
        path: 'issues',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Issues · Mastene',
        loadComponent: () => import('./features/issues/issue-list').then((m) => m.IssueList),
      },
      {
        path: 'issues/:id',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Issue · Mastene',
        loadComponent: () => import('./features/issues/issue-detail').then((m) => m.IssueDetail),
      },
      {
        path: 'contractors',
        canActivate: [roleGuard('Landlord')],
        title: 'Contractors · Mastene',
        loadComponent: () => import('./features/contractors/contractor-list').then((m) => m.ContractorList),
      },
      {
        path: 'inspections/:id',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Inspection · Mastene',
        loadComponent: () => import('./features/inspections/inspections').then((m) => m.InspectionDetail),
      },
      {
        path: 'messages',
        canActivate: [roleGuard('Landlord')],
        title: 'Messages · Mastene',
        loadComponent: () => import('./features/messages/messages').then((m) => m.MessagesPage),
      },
      {
        path: 'messages/:tenantId',
        canActivate: [roleGuard('Landlord')],
        title: 'Messages · Mastene',
        loadComponent: () => import('./features/messages/messages').then((m) => m.MessagesPage),
      },
      {
        path: 'my-messages',
        canActivate: [roleGuard('Tenant')],
        title: 'Messages · Mastene',
        loadComponent: () => import('./features/messages/messages').then((m) => m.MyMessages),
      },
      {
        path: 'privacy',
        canActivate: [roleGuard('Admin')],
        title: 'Privacy requests · Mastene',
        loadComponent: () => import('./features/privacy/privacy').then((m) => m.PrivacyPage),
      },
      {
        path: 'settings',
        canActivate: [roleGuard('Admin')],
        title: 'Settings · Mastene',
        loadComponent: () => import('./features/settings/settings').then((m) => m.SettingsPage),
      },
      {
        path: 'users',
        canActivate: [roleGuard('Admin')],
        title: 'Users · Mastene',
        loadComponent: () => import('./features/users/user-list').then((m) => m.UserList),
      },
      {
        path: 'my-details',
        canActivate: [roleGuard('Tenant')],
        title: 'My details · Mastene',
        loadComponent: () => import('./features/my-details/my-details').then((m) => m.MyDetails),
      },
      { path: '', pathMatch: 'full', canActivate: [homeRedirect], children: [] },
    ],
  },
  { path: '**', redirectTo: '' },
];
