import { Component, input } from '@angular/core';

/**
 * The Mastene mark (a roof over an "M") and name. Colours come from the theme's --app-logo-* tokens,
 * so it reads on the navy bar and on the sign-in card in both light and dark mode.
 */
@Component({
  selector: 'app-brand',
  template: `
    <svg class="mark" [attr.width]="size()" [attr.height]="size()" viewBox="0 0 32 32" aria-hidden="true" focusable="false">
      <rect width="32" height="32" rx="8" fill="var(--app-logo-bg)" />
      <path d="M8 23V12.5l8-5 8 5V23h-4.5v-7.5L16 18l-3.5-2.5V23z" fill="var(--app-logo-ink)" />
    </svg>
    <span class="name">Mastene</span>
  `,
  styles: `
    :host { display: inline-flex; align-items: center; gap: 10px; }
    .mark { flex: none; }
    .name { font: var(--mat-sys-title-large); font-weight: 600; letter-spacing: 0.01em; }
  `,
})
export class Brand {
  readonly size = input(30);
}
