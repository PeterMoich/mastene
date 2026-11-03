import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Role } from './api.models';
import { AuthService } from './auth.service';

/**
 * Lets a route through only for the given roles. Signed-out users go to /login; signed-in
 * users with the wrong role go to their own home page. This is a convenience for the UI —
 * the API enforces the same rules on every request.
 */
export function roleGuard(...roles: Role[]): CanActivateFn {
  return (_route, state) => {
    const auth = inject(AuthService);
    const router = inject(Router);
    if (!auth.isSignedIn()) return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
    if (auth.hasRole(...roles)) return true;
    return router.parseUrl(auth.homeFor(auth.role()));
  };
}

/** Sends a signed-in user from /login (or /) to their home page. */
export const signedOutOnly: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isSignedIn() ? inject(Router).parseUrl(auth.homeFor(auth.role())) : true;
};

/** The bare "/" URL: always redirects, to the user's home page or to /login. */
export const homeRedirect: CanActivateFn = () => {
  const auth = inject(AuthService);
  return inject(Router).parseUrl(auth.isSignedIn() ? auth.homeFor(auth.role()) : '/login');
};
