import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom, Observable, shareReplay, tap, finalize } from 'rxjs';
import { Me, Role, SessionResponse } from './api.models';

/** The API refuses cookie-based auth calls without it (a cross-site form can't send custom headers). */
const CSRF_HEADERS = { 'X-Requested-With': 'TenantManager' };

/**
 * Holds the signed-in user and the access token.
 * - The access token (15 min) lives only in memory.
 * - The refresh token is an HttpOnly, SameSite=Strict cookie set by the API for /api/v1/auth only.
 *   The page never sees it, so a script injected into the page can't steal it (ADR-0019). After a
 *   reload the app asks /auth/refresh for a new access token, and the browser sends the cookie.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly _user = signal<Me | null>(null);
  private accessToken: string | null = null;
  private refreshInFlight: Observable<SessionResponse> | null = null;

  readonly user = this._user.asReadonly();
  readonly isSignedIn = computed(() => this._user() !== null);
  readonly role = computed(() => this._user()?.role ?? null);

  get token(): string | null {
    return this.accessToken;
  }

  async login(email: string, password: string): Promise<Me> {
    const tokens = await firstValueFrom(
      this.http.post<SessionResponse>('/api/v1/auth/login', { email, password }),
    );
    this.store(tokens);
    return tokens.user;
  }

  /** Called at startup: resumes the session if the browser still holds a valid refresh cookie. */
  async restore(): Promise<void> {
    try {
      await firstValueFrom(this.refresh());
    } catch {
      this.clear();
    }
  }

  /** One refresh at a time: concurrent 401s all wait on the same request. */
  refresh(): Observable<SessionResponse> {
    if (!this.refreshInFlight) {
      this.refreshInFlight = this.http
        .post<SessionResponse>('/api/v1/auth/refresh', null, { headers: CSRF_HEADERS })
        .pipe(
          tap((tokens) => this.store(tokens)),
          finalize(() => (this.refreshInFlight = null)),
          shareReplay(1),
        );
    }
    return this.refreshInFlight;
  }

  async logout(): Promise<void> {
    this.clear();
    await firstValueFrom(this.http.post('/api/v1/auth/logout', null, { headers: CSRF_HEADERS })).catch(() => undefined);
    await this.router.navigateByUrl('/login');
  }

  hasRole(...roles: Role[]): boolean {
    const role = this.role();
    return role !== null && roles.includes(role);
  }

  /** Where each role lands after signing in. */
  homeFor(role: Role | null): string {
    switch (role) {
      case 'Admin':
        return '/users';
      case 'Tenant':
        return '/my-details';
      case 'Landlord':
        return '/properties';
      default:
        return '/login';
    }
  }

  clear(): void {
    this.accessToken = null;
    this._user.set(null);
  }

  private store(tokens: SessionResponse): void {
    this.accessToken = tokens.accessToken;
    this._user.set(tokens.user);
  }
}
