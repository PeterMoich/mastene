import { DOCUMENT, Injectable, inject, signal } from '@angular/core';

export type Appearance = 'system' | 'light' | 'dark';

const KEY = 'mastene.appearance';

/**
 * Light, dark, or follow the device (the default, as Apple's guidelines ask). The choice is a class on
 * <html> that sets color-scheme; every theme colour is a light-dark() pair, so nothing else changes.
 * Remembered in this browser only.
 */
@Injectable({ providedIn: 'root' })
export class AppearanceService {
  private readonly document = inject(DOCUMENT);
  readonly current = signal<Appearance>(this.read());

  constructor() {
    this.apply(this.current());
  }

  set(appearance: Appearance): void {
    this.current.set(appearance);
    this.apply(appearance);
    try {
      if (appearance === 'system') localStorage.removeItem(KEY);
      else localStorage.setItem(KEY, appearance);
    } catch {
      // Storage blocked (private mode): the choice lasts for this visit only.
    }
  }

  private read(): Appearance {
    try {
      const saved = localStorage.getItem(KEY);
      return saved === 'light' || saved === 'dark' ? saved : 'system';
    } catch {
      return 'system';
    }
  }

  private apply(appearance: Appearance): void {
    const root = this.document.documentElement;
    root.classList.toggle('light-mode', appearance === 'light');
    root.classList.toggle('dark-mode', appearance === 'dark');
  }
}
