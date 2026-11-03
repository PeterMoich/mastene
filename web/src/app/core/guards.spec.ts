import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { Role } from './api.models';
import { AuthService } from './auth.service';
import { roleGuard } from './guards';

describe('roleGuard', () => {
  const role = signal<Role | null>(null);
  const auth = {
    isSignedIn: () => role() !== null,
    role,
    hasRole: (...roles: Role[]) => role() !== null && roles.includes(role()!),
    homeFor: (r: Role | null) => (r === 'Tenant' ? '/my-details' : r === 'Admin' ? '/users' : '/properties'),
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: auth }],
    });
  });

  const run = (url: string, ...roles: Role[]) =>
    TestBed.runInInjectionContext(() =>
      roleGuard(...roles)({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    );
  const serialize = (result: unknown) => TestBed.inject(Router).serializeUrl(result as UrlTree);

  it('sends signed-out users to login and remembers where they were going', () => {
    role.set(null);
    expect(serialize(run('/tenants', 'Landlord'))).toBe('/login?returnUrl=%2Ftenants');
  });

  it('lets a landlord into landlord pages', () => {
    role.set('Landlord');
    expect(run('/tenants', 'Landlord')).toBe(true);
  });

  it('sends a tenant who opens a landlord page back to their own page', () => {
    role.set('Tenant');
    expect(serialize(run('/properties', 'Landlord'))).toBe('/my-details');
  });
});
