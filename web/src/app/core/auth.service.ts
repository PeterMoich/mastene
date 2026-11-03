import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom, Observable, shareReplay, tap, finalize } from 'rxjs';
import { Me, Role, TokenResponse } from './api.models';

const REFRESH_KEY = 'tm.refreshToken';

/**
 * Holds the signed-in user and tokens.
 * - The access token (15 min) lives only in memory.
 * - The refresh token is kept in sessionStorage so a page reload keeps you signed in,
 *   but closing the tab ends the session. Moving it to an httpOnly cookie is listed as
 *   Phase 6 hardening, since any XSS could read sessionStorage.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly _user = signal<Me | null>(null);
  private accessToken: string | null = null;
  private refreshInFlight: Observable<TokenResponse> | null = null;

  readonly user = this._user.asReadonly();
  readonly isSignedIn = computed(() => this._user() !== null);
  readonly role = computed(() => this._user()?.role ?? null);

  get token(): string | null {
    return this.accessToken;
  }

  async login(email: string, password: string): Promise<Me> {
    const tokens = await firstValueFrom(
      this.http.post<TokenResponse>('/api/v1/auth/login', { email, password }),
    );
    this.store(tokens);
    return tokens.user;
  }

  /** Called at startup: resumes a session from sessionStorage if there is one. */
  async restore(): Promise<void> {
    if (!this.storedRefreshToken()) return;
    try {
      await firstValueFrom(this.refresh());
    } catch {
      this.clear();
    }
  }

  /** One refresh at a time: concurrent 401s all wait on the same request. */
  refresh(): Observable<TokenResponse> {
    if (!this.refreshInFlight) {
      const refreshToken = this.storedRefreshToken() ?? '';
      this.refreshInFlight = this.http
        .post<TokenResponse>('/api/v1/auth/refresh', { refreshToken })
        .pipe(
          tap((tokens) => this.store(tokens)),
          finalize(() => (this.refreshInFlight = null)),
          shareReplay(1),
        );
    }
    return this.refreshInFlight;
  }

  async logout(): Promise<void> {
    const refreshToken = this.storedRefreshToken();
    this.clear();
    if (refreshToken) {
      await firstValueFrom(this.http.post('/api/v1/auth/logout', { refreshToken })).catch(() => undefined);
    }
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
    sessionStorage.removeItem(REFRESH_KEY);
  }

  private store(tokens: TokenResponse): void {
    this.accessToken = tokens.accessToken;
    sessionStorage.setItem(REFRESH_KEY, tokens.refreshToken);
    this._user.set(tokens.user);
  }

  private storedRefreshToken(): string | null {
    return sessionStorage.getItem(REFRESH_KEY);
  }
}
