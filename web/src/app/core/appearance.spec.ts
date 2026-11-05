import { TestBed } from '@angular/core/testing';
import { AppearanceService } from './appearance';

describe('AppearanceService', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.className = '';
  });

  it('follows the device by default', () => {
    const service = TestBed.inject(AppearanceService);
    expect(service.current()).toBe('system');
    expect(document.documentElement.classList.contains('dark-mode')).toBe(false);
    expect(document.documentElement.classList.contains('light-mode')).toBe(false);
  });

  it('applies and remembers an explicit choice, and forgets it when set back to the device', () => {
    const service = TestBed.inject(AppearanceService);
    service.set('dark');
    expect(document.documentElement.classList.contains('dark-mode')).toBe(true);
    expect(localStorage.getItem('mastene.appearance')).toBe('dark');

    service.set('system');
    expect(document.documentElement.classList.contains('dark-mode')).toBe(false);
    expect(localStorage.getItem('mastene.appearance')).toBeNull();
  });
});
