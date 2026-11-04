import { Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';
import { MAX_PHOTOS, PHOTO_MAX_BYTES, PHOTO_TYPES } from '../core/api.models';
import { ApiService } from '../core/api.service';

/** Checks a chosen photo the same way the API will (BR12), so the user hears about it before uploading. */
export function checkPhotoFile(file: File | null): string | null {
  if (!file) return 'Choose a photo.';
  if (file.size === 0) return `${file.name} is empty.`;
  if (file.size > PHOTO_MAX_BYTES) return `${file.name} is larger than 5 MB.`;
  const byName = /\.(jpe?g|png)$/i.test(file.name);
  if (!PHOTO_TYPES.includes(file.type) && !byName) return `${file.name}: only JPG or PNG photos are accepted.`;
  return null;
}

/** Splits chosen files into those that can be uploaded and messages for the rest. */
export function checkPhotoFiles(files: File[], alreadyAttached: number): { ok: File[]; problems: string[] } {
  const ok: File[] = [];
  const problems: string[] = [];
  for (const file of files) {
    const problem = checkPhotoFile(file);
    if (problem) problems.push(problem);
    else if (alreadyAttached + ok.length >= MAX_PHOTOS) problems.push(`${file.name}: at most ${MAX_PHOTOS} photos.`);
    else ok.push(file);
  }
  return { ok, problems };
}

/**
 * An image the API serves only to signed-in users. Fetched with the user's token and shown from a
 * blob: URL (allowed by the CSP's img-src), released again when the component goes away.
 */
@Component({
  selector: 'app-auth-image',
  template: `
    @if (src(); as s) {
      <img [src]="s" [alt]="alt()" [class.full]="full()" />
    } @else if (failed()) {
      <span class="missing">Photo unavailable</span>
    } @else {
      <span class="missing" aria-hidden="true"></span>
    }
  `,
  styles: `
    :host { display: inline-block; }
    img { display: block; width: 120px; height: 90px; object-fit: cover; border-radius: 6px; }
    img.full { width: auto; height: auto; max-width: min(90vw, 1000px); max-height: 75vh; object-fit: contain; }
    .missing { display: flex; align-items: center; justify-content: center; width: 120px; height: 90px;
               border-radius: 6px; background: var(--mat-sys-surface-container-high);
               color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-label-small); text-align: center; }
  `,
})
export class AuthImage {
  private readonly api = inject(ApiService);
  readonly url = input.required<string>();
  readonly alt = input.required<string>();
  readonly full = input(false);
  protected readonly src = signal<string | null>(null);
  protected readonly failed = signal(false);
  private objectUrl: string | null = null;

  constructor() {
    effect(() => {
      const url = this.url();
      void this.load(url);
    });
    inject(DestroyRef).onDestroy(() => this.release());
  }

  private async load(url: string): Promise<void> {
    try {
      const blob = await firstValueFrom(this.api.image(url));
      this.release();
      this.objectUrl = URL.createObjectURL(blob);
      this.src.set(this.objectUrl);
    } catch {
      this.failed.set(true);
    }
  }

  private release(): void {
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
  }
}

export interface PhotoRef {
  url: string;
  alt: string;
}

@Component({
  selector: 'app-photo-viewer',
  imports: [MatDialogModule, MatButtonModule, AuthImage],
  template: `
    <h2 mat-dialog-title>{{ data.alt }}</h2>
    <mat-dialog-content><app-auth-image [url]="data.url" [alt]="data.alt" [full]="true" /></mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button type="button" mat-dialog-close cdkFocusInitial>Close</button></mat-dialog-actions>
  `,
})
export class PhotoViewer {
  protected readonly data = inject<PhotoRef>(MAT_DIALOG_DATA);
}

/** A row of thumbnails; each opens the full photo. */
@Component({
  selector: 'app-photo-strip',
  imports: [AuthImage, MatIconModule],
  template: `
    @if (photos().length === 0) {
      <p class="muted">{{ empty() }}</p>
    } @else {
      <ul class="strip">
        @for (p of photos(); track p.url) {
          <li>
            <button type="button" class="thumb" (click)="open(p)" [attr.aria-label]="'View ' + p.alt">
              <app-auth-image [url]="p.url" [alt]="p.alt" />
            </button>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    .strip { list-style: none; display: flex; flex-wrap: wrap; gap: 8px; padding: 0; margin: 0; }
    .thumb { padding: 0; border: 2px solid transparent; border-radius: 8px; background: none; cursor: zoom-in; }
    .thumb:focus-visible { border-color: var(--mat-sys-primary); outline: none; }
    .muted { color: var(--mat-sys-on-surface-variant); margin: 0; }
  `,
})
export class PhotoStrip {
  private readonly dialog = inject(MatDialog);
  readonly photos = input.required<PhotoRef[]>();
  readonly empty = input('No photos.');

  protected open(photo: PhotoRef): void {
    this.dialog.open(PhotoViewer, { data: photo, maxWidth: '95vw' });
  }
}
