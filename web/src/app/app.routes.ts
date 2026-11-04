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
        path: 'issues',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Issues · Tenant Manager',
        loadComponent: () => import('./features/issues/issue-list').then((m) => m.IssueList),
      },
      {
        path: 'issues/:id',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Issue · Tenant Manager',
        loadComponent: () => import('./features/issues/issue-detail').then((m) => m.IssueDetail),
      },
      {
        path: 'contractors',
        canActivate: [roleGuard('Landlord')],
        title: 'Contractors · Tenant Manager',
        loadComponent: () => import('./features/contractors/contractor-list').then((m) => m.ContractorList),
      },
      {
        path: 'inspections/:id',
        canActivate: [roleGuard('Landlord', 'Tenant')],
        title: 'Inspection · Tenant Manager',
        loadComponent: () => import('./features/inspections/inspections').then((m) => m.InspectionDetail),
      },
      {
        path: 'messages',
        canActivate: [roleGuard('Landlord')],
        title: 'Messages · Tenant Manager',
        loadComponent: () => import('./features/messages/messages').then((m) => m.MessagesPage),
      },
      {
        path: 'messages/:tenantId',
        canActivate: [roleGuard('Landlord')],
        title: 'Messages · Tenant Manager',
        loadComponent: () => import('./features/messages/messages').then((m) => m.MessagesPage),
      },
      {
        path: 'my-messages',
        canActivate: [roleGuard('Tenant')],
        title: 'Messages · Tenant Manager',
        loadComponent: () => import('./features/messages/messages').then((m) => m.MyMessages),
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
