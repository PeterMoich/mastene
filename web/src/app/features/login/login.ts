import { Component, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';
import { Brand } from '../../shared/brand';

@Component({
  selector: 'app-login',
  imports: [
    ReactiveFormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule,
    MatProgressBarModule, Brand,
  ],
  template: `
    <main class="page">
      <section class="visual" aria-label="About Mastene">
        <picture>
          <source srcset="images/login-bg.webp" type="image/webp" />
          <img src="images/login-bg.jpg" alt="" class="photo" />
        </picture>
        <div class="shade" aria-hidden="true"></div>
        <app-brand class="visual-brand" [size]="36" />
        <div class="pitch">
          <p class="headline">Renting, sorted.</p>
          <p class="lede">Leases, rent, repairs and messages in one place, for South African landlords and their tenants.</p>
          <p class="credit">
            Photo by <a href="https://unsplash.com/@watershoes" target="_blank" rel="noopener">Cristian Pineda</a>
            on <a href="https://unsplash.com/photos/KkYihWTf6Tw" target="_blank" rel="noopener">Unsplash</a>
          </p>
        </div>
      </section>
      <section class="panel">
        <mat-card class="card" appearance="outlined">
          <div class="top">
            <app-brand class="card-brand" [size]="36" />
            <h1>Sign in to Mastene</h1>
            <p class="sub">Welcome back. Sign in with the email your landlord or administrator gave you.</p>
          </div>
          @if (busy()) {
            <mat-progress-bar mode="indeterminate" aria-label="Signing in" />
          }
          <mat-card-content>
            <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
              <mat-form-field class="full">
                <mat-label>Email</mat-label>
                <input matInput type="email" formControlName="email" autocomplete="username" required />
                @if (form.controls.email.hasError('email')) {
                  <mat-error>Enter a valid email address.</mat-error>
                } @else if (form.controls.email.hasError('required')) {
                  <mat-error>Email is required.</mat-error>
                }
              </mat-form-field>
              <mat-form-field class="full">
                <mat-label>Password</mat-label>
                <input matInput [type]="showPassword() ? 'text' : 'password'" formControlName="password"
                       autocomplete="current-password" required />
                <button mat-icon-button matSuffix type="button" (click)="showPassword.set(!showPassword())"
                        [attr.aria-label]="showPassword() ? 'Hide password' : 'Show password'"
                        [attr.aria-pressed]="showPassword()">
                  <mat-icon>{{ showPassword() ? 'visibility_off' : 'visibility' }}</mat-icon>
                </button>
                @if (form.controls.password.hasError('required')) {
                  <mat-error>Password is required.</mat-error>
                }
              </mat-form-field>
              @if (error()) {
                <p class="error" role="alert"><mat-icon aria-hidden="true">error</mat-icon>{{ error() }}</p>
              }
              <button mat-flat-button type="submit" class="full submit" [disabled]="busy()">
                {{ busy() ? 'Signing in…' : 'Sign in' }}
              </button>
            </form>
          </mat-card-content>
        </mat-card>
      </section>
    </main>
  `,
  styles: `
    :host { display: block; height: 100%; }
    .page { min-height: 100vh; min-height: 100dvh; display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(420px, 1fr);
            background: var(--mat-sys-surface-container-low); }
    .visual { position: relative; overflow: hidden; display: flex; flex-direction: column; justify-content: space-between;
              padding: 32px 40px; color: #ffffff; min-height: 100%; box-sizing: border-box;
              --app-logo-bg: #2bb3b1; --app-logo-ink: #0b1c3a; background: #13294b; }
    picture, .photo { position: absolute; inset: 0; width: 100%; height: 100%; }
    .photo { object-fit: cover; object-position: center 35%; }
    .shade { position: absolute; inset: 0;
             background: linear-gradient(180deg, rgba(11, 28, 58, 0.55) 0%, rgba(11, 28, 58, 0.15) 35%, rgba(11, 28, 58, 0.85) 100%); }
    .visual-brand, .pitch { position: relative; }
    .pitch { max-width: 34rem; display: flex; flex-direction: column; gap: 10px; }
    .headline { margin: 0; font: var(--mat-sys-display-small); font-weight: 600; letter-spacing: -0.02em; }
    .lede { margin: 0; font: var(--mat-sys-title-medium); font-weight: 400; color: rgba(255, 255, 255, 0.88); }
    .credit { margin: 12px 0 0; font: var(--mat-sys-body-small); color: rgba(255, 255, 255, 0.7); }
    .credit a { color: inherit; }
    .panel { display: grid; place-items: center; padding: 32px 24px; box-sizing: border-box; }
    .card { width: 100%; max-width: 420px; padding: 12px 12px 20px; border-radius: 20px;
            background: var(--mat-sys-surface-container-lowest); --app-field-fill: var(--mat-sys-surface-container-lowest);
            box-shadow: 0 12px 40px rgba(11, 28, 58, 0.08); }
    .top { display: flex; flex-direction: column; gap: 6px; padding: 16px 16px 4px; }
    .card-brand { display: none; color: light-dark(var(--app-bar), var(--mat-sys-on-surface)); margin-bottom: 12px; }
    h1 { font: var(--mat-sys-headline-small); font-weight: 600; margin: 0; }
    .sub { margin: 0; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-medium); }
    .full { width: 100%; }
    .submit { height: 48px; border-radius: 24px; margin-top: 4px; }
    .error { display: flex; gap: 8px; align-items: flex-start; color: var(--mat-sys-on-error-container);
             background: var(--mat-sys-error-container); padding: 10px 12px; border-radius: 8px; margin: 0 0 12px; }
    .error mat-icon { flex: none; font-size: 20px; width: 20px; height: 20px; }
    form { display: flex; flex-direction: column; gap: 4px; padding-top: 12px; }

    @media (max-width: 900px) {
      .page { grid-template-columns: minmax(0, 1fr); grid-template-rows: auto 1fr; }
      .visual { min-height: 240px; padding: 20px; }
      .headline { font: var(--mat-sys-headline-medium); font-weight: 600; }
      .lede { font: var(--mat-sys-body-large); }
      .credit { margin-top: 4px; }
      .panel { padding: 0 16px 32px; align-items: start; }
      .card { margin-top: -28px; position: relative; }
    }
  `,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /** Bound from the ?returnUrl= query parameter set by the route guard. */
  readonly returnUrl = input<string>();

  protected readonly busy = signal(false);
  protected readonly showPassword = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const { email, password } = this.form.getRawValue();
      const user = await this.auth.login(email, password);
      // Only same-app paths are accepted, so a crafted link can't bounce the user elsewhere.
      const returnUrl = this.returnUrl();
      const target =
        returnUrl?.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : this.auth.homeFor(user.role);
      await this.router.navigateByUrl(target);
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
