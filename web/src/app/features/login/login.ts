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
      <mat-card class="card" appearance="outlined">
        <div class="top">
          <app-brand [size]="36" />
          <h1>Sign in to Mastene</h1>
          <p class="sub">Leases, rent, issues and messages in one place.</p>
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
    </main>
  `,
  styles: `
    .page { min-height: 100%; display: grid; place-items: center; padding: 24px 16px; box-sizing: border-box;
            background: var(--mat-sys-surface-container-low); }
    .card { width: 100%; max-width: 400px; padding: 8px 8px 16px; border-radius: 16px;
            background: var(--mat-sys-surface-container-lowest); }
    .top { display: flex; flex-direction: column; gap: 6px; padding: 16px 16px 4px; }
    app-brand { color: light-dark(var(--app-bar), var(--mat-sys-on-surface)); margin-bottom: 12px; }
    h1 { font: var(--mat-sys-headline-small); margin: 0; }
    .sub { margin: 0; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-medium); }
    .full { width: 100%; }
    .submit { height: 48px; border-radius: 24px; margin-top: 4px; }
    .error { display: flex; gap: 8px; align-items: flex-start; color: var(--mat-sys-on-error-container);
             background: var(--mat-sys-error-container); padding: 10px 12px; border-radius: 8px; margin: 0 0 12px; }
    .error mat-icon { flex: none; font-size: 20px; width: 20px; height: 20px; }
    form { display: flex; flex-direction: column; gap: 4px; padding-top: 12px; }
  `,
})})
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
