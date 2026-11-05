import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { Login } from './login';

describe('Login', () => {
  const auth = {
    login: vi.fn(),
    demoLogins: vi.fn().mockResolvedValue(null),
    homeFor: vi.fn((role: string) => (role === 'Landlord' ? '/properties' : '/login')),
  };
  const router = { navigateByUrl: vi.fn().mockResolvedValue(true) };

  beforeEach(async () => {
    vi.clearAllMocks();
    auth.demoLogins.mockResolvedValue(null);
    await TestBed.configureTestingModule({
      imports: [Login],
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
      ],
    }).compileComponents();
  });

  function fill(email: string, password: string) {
    const fixture = TestBed.createComponent(Login);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const [emailInput, passwordInput] = Array.from(el.querySelectorAll('input'));
    emailInput.value = email;
    emailInput.dispatchEvent(new Event('input'));
    passwordInput.value = password;
    passwordInput.dispatchEvent(new Event('input'));
    return { fixture, el };
  }

  it('signs in and goes to the landlord home page', async () => {
    auth.login.mockResolvedValue({ role: 'Landlord' });
    const { el } = fill('landlord@mastene.test', 'secret-password');

    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await vi.waitFor(() => expect(router.navigateByUrl).toHaveBeenCalledWith('/properties'));
    expect(auth.login).toHaveBeenCalledWith('landlord@mastene.test', 'secret-password');
  });

  it('shows the API message when the credentials are wrong', async () => {
    auth.login.mockRejectedValue(
      new HttpErrorResponse({ status: 401, error: { title: 'Email or password is incorrect.' } }),
    );
    const { fixture, el } = fill('landlord@mastene.test', 'wrong');

    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await vi.waitFor(async () => {
      await fixture.whenStable();
      expect(el.querySelector('[role="alert"]')?.textContent).toContain('Email or password is incorrect.');
    });
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('does not call the API when the form is invalid', async () => {
    const { fixture, el } = fill('not-an-email', '');

    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(auth.login).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Enter a valid email address.');
  });

  it('offers demo accounts on a demo site and signs in with one click', async () => {
    auth.demoLogins.mockResolvedValue({
      password: 'Demo-Pass-1',
      accounts: [{ role: 'Landlord', name: 'Thandi Nkosi', email: 'landlord@mastene.test' }],
    });
    auth.login.mockResolvedValue({ role: 'Landlord' });
    const fixture = TestBed.createComponent(Login);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Try the demo');
    (el.querySelector('.demo-account') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(auth.login).toHaveBeenCalledWith('landlord@mastene.test', 'Demo-Pass-1'));
  });

  it('shows no demo box on a real site', async () => {
    const fixture = TestBed.createComponent(Login);
    fixture.detectChanges();
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Try the demo');
  });
});
