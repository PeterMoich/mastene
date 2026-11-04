import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ChatMessage, Role, Thread } from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { deliveryLabel, templateDefaults, ThreadView } from './messages';

const msg = (o: Partial<ChatMessage>): ChatMessage => ({
  id: 'm', direction: 'Outbound', channel: 'WhatsApp', kind: 'Text', templateName: null, body: 'Hi', status: 'Sent',
  error: null, at: '2026-11-04T10:00:00Z', sentAt: null, byName: 'Thandi Nkosi', ...o,
});

describe('deliveryLabel', () => {
  it('says how each message travelled', () => {
    expect(deliveryLabel(msg({ status: 'Delivered' }))).toBe('WhatsApp · Delivered');
    expect(deliveryLabel(msg({ kind: 'Template', status: 'Read' }))).toBe('WhatsApp template · Read');
    expect(deliveryLabel(msg({ channel: 'App', status: 'Received' }))).toBe('In the app only');
    expect(deliveryLabel(msg({ direction: 'Inbound', channel: 'App', status: 'Received' }))).toBe('Written in the app');
    expect(deliveryLabel(msg({ direction: 'Inbound', status: 'Received' }))).toBe('WhatsApp');
    expect(deliveryLabel(msg({ status: 'Failed' }))).toBe('WhatsApp · Not sent');
  });
});

describe('templateDefaults', () => {
  it('fills the tenant and landlord names and leaves the topic to type', () => {
    const t = { name: 'message_waiting', description: '', body: '', parameterNames: ['tenant first name', 'landlord name', 'topic'], manual: true };
    expect(templateDefaults(t, 'Sipho Dlamini', 'Thandi Nkosi')).toEqual(['Sipho', 'Thandi Nkosi', '']);
  });
});

function thread(o: Partial<Thread> = {}): Thread {
  return {
    tenantId: 't1', tenantName: 'Sipho Dlamini', phone: '+27825550101',
    consent: { granted: true, phone: '+27825550101', textVersion: 'wa-consent-v1', text: 'I agree…', latest: null, history: [] },
    inServiceWindow: false, serviceWindowEndsAt: null, whatsAppTextAllowed: false, simulationAvailable: true,
    messages: [msg({ direction: 'Inbound', status: 'Received', body: 'The plumber did not come', byName: 'Sipho Dlamini' })],
    ...o,
  };
}

describe('ThreadView', () => {
  const role = signal<Role>('Landlord');
  const api = { getThread: vi.fn(), sendMessage: vi.fn() };

  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({
      imports: [ThreadView],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: api },
        { provide: AuthService, useValue: { role, user: () => ({ displayName: 'Thandi Nkosi' }) } },
        { provide: MatDialog, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();
  });

  async function render(t: Thread) {
    api.getThread.mockReturnValue(of(t));
    const fixture = TestBed.createComponent(ThreadView);
    fixture.componentRef.setInput('tenantId', t.tenantId);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('tells the landlord, outside the 24-hour window, that a reply stays in the app (BR17)', async () => {
    role.set('Landlord');
    const { el } = await render(thread());
    expect(el.querySelector('.window')?.textContent).toContain("Outside WhatsApp's 24-hour window");
    expect(el.textContent).toContain('Template…');
    expect(el.textContent).toContain('Simulate tenant reply');
  });

  it('says when the reply will go to WhatsApp', async () => {
    role.set('Landlord');
    const { el } = await render(thread({ inServiceWindow: true, whatsAppTextAllowed: true, serviceWindowEndsAt: '2026-11-05T10:00:00Z' }));
    expect(el.querySelector('.window')?.textContent).toContain('Your reply goes to WhatsApp');
  });

  it('shows the tenant their thread without landlord tools, and the consent wording when not agreed (BR16)', async () => {
    role.set('Tenant');
    const { el } = await render(thread({ consent: { ...thread().consent, granted: false }, simulationAvailable: false }));
    expect(el.textContent).toContain('Messages with your landlord');
    expect(el.textContent).not.toContain('Template…');
    expect(el.textContent).toContain('I agree…');
    expect(el.querySelector('.window')).toBeNull();
  });

  it('sends a reply and shows the updated thread', async () => {
    role.set('Landlord');
    api.sendMessage.mockReturnValue(of(thread({ messages: [msg({ body: 'Tomorrow at 9', channel: 'App', status: 'Received' })] })));
    const { fixture, el } = await render(thread());
    const box = el.querySelector('textarea') as HTMLTextAreaElement;
    box.value = 'Tomorrow at 9';
    box.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.sendMessage).toHaveBeenCalledWith('t1', 'Tomorrow at 9');
    expect(el.textContent).toContain('In the app only');
  });
});
