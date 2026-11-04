import { Component, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { firstValueFrom } from 'rxjs';
import { ConsentStatus, Tenant } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';
import { ConsentPanel } from '../messages/messages';

/** The tenant's own page: their details and WhatsApp consent. */
@Component({
  selector: 'app-my-details',
  imports: [MatCardModule, ConsentPanel],
  template: `
    <h1>My details</h1>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    @if (tenant(); as t) {
      <mat-card appearance="outlined" class="card">
        <mat-card-content>
          <dl>
            <dt>Name</dt><dd>{{ t.firstName }} {{ t.lastName }}</dd>
            <dt>Email</dt><dd>{{ t.email }}</dd>
            <dt>Mobile</dt><dd>{{ t.phone }}</dd>
          </dl>
          <p class="note">To change these details, contact your landlord.</p>
        </mat-card-content>
      </mat-card>
      @if (consent(); as c) {
        <div class="card consent"><app-consent-panel [tenantId]="t.id" [consent]="c" (changed)="consent.set($event)" /></div>
      }
    }
  `,
  styles: `
    h1 { font: var(--mat-sys-headline-medium); margin: 0 0 16px; }
    .card { max-width: 480px; }
    .consent { margin-top: 16px; }
    dt { font-weight: 500; margin-top: 12px; }
    dd { margin: 0; }
    .note { margin-top: 16px; color: var(--mat-sys-on-surface-variant); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class MyDetails {
  private readonly api = inject(ApiService);
  protected readonly tenant = signal<Tenant | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly consent = signal<ConsentStatus | null>(null);

  constructor() {
    const tenantId = inject(AuthService).user()?.tenantId;
    if (tenantId) {
      firstValueFrom(this.api.getTenant(tenantId))
        .then((t) => this.tenant.set(t))
        .catch((e: unknown) => this.error.set(errorMessage(e)));
      firstValueFrom(this.api.getConsent(tenantId))
        .then((c) => this.consent.set(c))
        .catch((e: unknown) => this.error.set(errorMessage(e)));
    }
  }
}
