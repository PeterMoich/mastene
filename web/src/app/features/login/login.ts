import { Component, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatProgressBarModule],
  template: `
    <main class="page">
      <mat-card class="card" appearance="outlined">
        <mat-card-header>
          <mat-card-title><h1>Sign in to Tenant Manager</h1></mat-card-title>
        </mat-card-header>
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
              <input matInput type="password" formControlName="password" autocomplete="current-password" required />
              @if (form.controls.password.hasError('required')) {
                <mat-error>Password is required.</mat-error>
              }
            </mat-form-field>
            @if (error()) {
              <p class="error" role="alert">{{ error() }}</p>
            }
            <button mat-flat-button type="submit" class="full" [disabled]="busy()">Sign in</button>
          </form>
        </mat-card-content>
      </mat-card>
    </main>
  `,
  styles: `
    .page { min-height: 100%; display: grid; place-items: center; padding: 16px; box-sizing: border-box; }
    .card { width: 100%; max-width: 400px; }
    h1 { font: var(--mat-sys-headline-small); margin: 0 0 8px; }
    .full { width: 100%; }
    .error { color: var(--mat-sys-error); margin: 0 0 16px; }
    form { display: flex; flex-direction: column; gap: 4px; padding-top: 8px; }
  `,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /** Bound from the ?returnUrl= query parameter set by the route guard. */
  readonly returnUrl = input<string>();

  protected readonly busy = signal(false);
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
