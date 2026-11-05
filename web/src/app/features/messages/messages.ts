import { DatePipe } from '@angular/common';
import { Component, computed, effect, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import {
  ChatMessage,
  ConsentStatus,
  ConversationSummary,
  MessageTemplate,
  Tenant,
  Thread,
} from '../../core/api.models';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/problem';
import { TextDialog, TextDialogData } from '../issues/issue-dialogs';

const STATUS_WORDS: Record<ChatMessage['status'], string> = {
  Queued: 'Sending…',
  Sent: 'Sent',
  Delivered: 'Delivered',
  Read: 'Read',
  Failed: 'Not sent',
  Received: '',
};

/** How a message travelled, in words: "WhatsApp · Delivered", "In the app only", "WhatsApp template · Read". */
export function deliveryLabel(m: ChatMessage): string {
  if (m.channel === 'App') return m.direction === 'Inbound' ? 'Written in the app' : 'In the app only';
  const via = m.kind === 'Template' ? 'WhatsApp template' : 'WhatsApp';
  const status = STATUS_WORDS[m.status];
  return status ? `${via} · ${status}` : via;
}

/** Fills the obvious template values so the landlord only types the topic. */
export function templateDefaults(t: MessageTemplate, tenantName: string, landlordName: string): string[] {
  return t.parameterNames.map((p) =>
    p === 'tenant first name' ? tenantName.split(' ')[0] : p === 'landlord name' ? landlordName : '',
  );
}

/** BR16: WhatsApp consent, shown to the tenant (to give or withdraw) or to the landlord (to record). */
@Component({
  selector: 'app-consent-panel',
  imports: [DatePipe, MatButtonModule, MatIconModule],
  template: `
    @let c = consent();
    <section class="consent" [class.on]="c.granted" aria-labelledby="consent-heading">
      <span class="badge" aria-hidden="true"><mat-icon>{{ c.granted ? 'mark_chat_read' : 'chat_bubble_outline' }}</mat-icon></span>
      <div class="content">
      <h2 id="consent-heading">{{ c.granted ? 'WhatsApp messages: on' : 'WhatsApp messages: off' }}</h2>
      @if (c.latest; as l) {
        <p class="muted">
          {{ l.granted ? 'Agreed' : 'Withdrawn' }} {{ l.at | date: 'd MMM y' }} ·
          {{ sourceText(l.source) }}{{ l.note ? ': ' + l.note : '' }}
          @if (l.granted && l.phone !== c.phone) { <br />That was for {{ l.phone }}; the number has changed since, so it no longer applies. }
        </p>
      } @else {
        <p class="muted">No WhatsApp consent recorded.</p>
      }
      @if (isTenant()) {
        @if (!c.granted) {
          <p class="text">{{ c.text }}</p>
          <p class="muted small">Messages go to {{ c.phone }}. Wording version {{ c.textVersion }}.</p>
          <button mat-flat-button type="button" (click)="set(true)" [disabled]="busy()">I agree</button>
        } @else {
          <p class="muted small">You get rent reminders, receipts and issue updates on {{ c.phone }}. You can also reply STOP on WhatsApp.</p>
          <button mat-stroked-button type="button" (click)="set(false)" [disabled]="busy()">Stop WhatsApp messages</button>
        }
      } @else {
        @if (c.granted) {
          <button mat-stroked-button type="button" (click)="set(false)" [disabled]="busy()">Record withdrawal</button>
        } @else {
          <button mat-stroked-button type="button" (click)="set(true)" [disabled]="busy()">Record consent given another way</button>
        }
      }
      </div>
    </section>
  `,
  styles: `
    :host { display: block; }
    .consent { display: grid; grid-template-columns: 40px minmax(0, 1fr); gap: 14px; align-items: start;
               background: var(--mat-sys-surface-container-lowest); border: 1px solid var(--mat-sys-outline-variant);
               border-radius: 16px; padding: 18px 20px; }
    .badge { display: grid; place-items: center; width: 40px; height: 40px; border-radius: 12px;
             background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface-variant); }
    .on .badge { background: var(--app-success-container); color: var(--app-on-success-container); }
    .badge mat-icon { font-size: 22px; width: 22px; height: 22px; }
    .content { min-width: 0; }
    h2 { font: var(--mat-sys-title-medium); font-weight: 600; margin: 0 0 2px; }
    p { margin: 4px 0 10px; }
    .text { max-width: 62ch; padding: 10px 12px; border-radius: 10px; background: var(--mat-sys-surface-container-low);
            border-left: 3px solid var(--mat-sys-primary); }
    button { border-radius: 20px; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .small { font: var(--mat-sys-body-small); }
  `,
})
export class ConsentPanel {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  readonly tenantId = input.required<string>();
  readonly consent = input.required<ConsentStatus>();
  readonly changed = output<ConsentStatus>();
  protected readonly isTenant = computed(() => this.auth.role() === 'Tenant');
  protected readonly busy = signal(false);

  protected sourceText(source: string): string {
    return source === 'Tenant' ? 'by the tenant in the app' : source === 'WhatsAppStop' ? 'replied STOP on WhatsApp' : 'recorded by the landlord';
  }

  protected async set(granted: boolean): Promise<void> {
    let note: string | null = null;
    if (!this.isTenant() && granted) {
      const answer = await firstValueFrom(
        this.dialog
          .open<TextDialog, TextDialogData, string>(TextDialog, {
            data: {
              title: 'Record WhatsApp consent',
              label: 'How did the tenant agree?',
              hint: 'For example: signed consent form, 3 November 2026. Best is for tenants to agree in the app themselves.',
              confirmLabel: 'Record consent',
              required: true,
              maxLength: 300,
            },
          })
          .afterClosed(),
      );
      if (!answer) return;
      note = answer;
    }
    this.busy.set(true);
    try {
      this.changed.emit(await firstValueFrom(this.api.setConsent(this.tenantId(), granted, note)));
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
    } finally {
      this.busy.set(false);
    }
  }
}

/** Picks a manual template and fills its values (the way to reach a tenant outside the 24-hour window). */
@Component({
  selector: 'app-template-dialog',
  imports: [FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Send a WhatsApp template</h2>
    <mat-dialog-content class="fields">
      <p class="muted">Outside the 24-hour window WhatsApp only allows approved templates.</p>
      <mat-form-field>
        <mat-label>Template</mat-label>
        <mat-select [(ngModel)]="chosen" (selectionChange)="pick($event.value)">
          @for (t of templates(); track t.name) { <mat-option [value]="t">{{ t.description }}</mat-option> }
        </mat-select>
      </mat-form-field>
      @if (chosen; as t) {
        @for (p of t.parameterNames; track p; let i = $index) {
          <mat-form-field>
            <mat-label>{{ p }}</mat-label>
            <input matInput [(ngModel)]="values[i]" [name]="'p' + i" maxlength="200" required />
          </mat-form-field>
        }
        <p class="preview" aria-live="polite"><strong>Preview:</strong> {{ preview() }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" (click)="send()" [disabled]="!chosen || !complete()">Send</button>
    </mat-dialog-actions>
  `,
  styles: `
    .fields { display: flex; flex-direction: column; min-width: min(480px, 80vw); }
    .muted { color: var(--mat-sys-on-surface-variant); margin-top: 0; }
    .preview { background: var(--mat-sys-surface-container); padding: 8px 12px; border-radius: 8px; }
  `,
})
export class TemplateDialog {
  private readonly api = inject(ApiService);
  private readonly ref = inject(MatDialogRef<TemplateDialog, { name: string; parameters: string[] }>);
  private readonly data = inject<{ tenantName: string; landlordName: string }>(MAT_DIALOG_DATA);
  protected readonly templates = signal<MessageTemplate[]>([]);
  protected chosen: MessageTemplate | null = null;
  protected values: string[] = [];

  constructor() {
    void firstValueFrom(this.api.messageTemplates()).then((all) => {
      const manual = all.filter((t) => t.manual);
      this.templates.set(manual);
      if (manual.length === 1) this.pick(manual[0]);
    });
  }

  pick(t: MessageTemplate): void {
    this.chosen = t;
    this.values = templateDefaults(t, this.data.tenantName, this.data.landlordName);
  }

  protected complete(): boolean {
    return this.values.every((v) => v.trim().length > 0);
  }

  protected preview(): string {
    return (this.chosen?.body ?? '').replace(/\{\{(\d+)\}\}/g, (_, n) => this.values[Number(n) - 1] || '…');
  }

  send(): void {
    if (this.chosen && this.complete()) this.ref.close({ name: this.chosen.name, parameters: this.values.map((v) => v.trim()) });
  }
}

/** One conversation: messages, the composer, the 24-hour window, consent, and (fake provider) a simulated reply. */
@Component({
  selector: 'app-thread',
  // FormsModule gives the <form> its ngSubmit (via NgForm); the text box itself is a reactive control.
  imports: [DatePipe, FormsModule, ReactiveFormsModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, ConsentPanel],
  template: `
    @if (thread(); as t) {
      <section class="chat" aria-labelledby="thread-title">
        <header class="thread-head">
          <span class="avatar" aria-hidden="true">
            @if (isTenant()) { <mat-icon>home_work</mat-icon> } @else { {{ initials(t.tenantName) }} }
          </span>
          <div class="who">
            <h2 id="thread-title">{{ isTenant() ? 'Messages with your landlord' : t.tenantName }}</h2>
            <span class="muted small">{{ isTenant() ? 'Your landlord reads and answers these in Mastene.' : t.phone }}</span>
          </div>
          <span [class]="'app-chip sm ' + (t.consent.granted ? 'success' : 'muted')">
            <mat-icon aria-hidden="true">{{ t.consent.granted ? 'check' : 'block' }}</mat-icon>
            WhatsApp {{ t.consent.granted ? 'on' : 'off' }}
          </span>
        </header>

        @if (!isTenant() || !t.consent.granted) {
          <div class="consent-wrap">
            <app-consent-panel [tenantId]="t.tenantId" [consent]="t.consent" (changed)="consentChanged($event)" />
          </div>
        }

        <ol class="messages" #list aria-label="Messages" aria-live="polite">
          @for (m of t.messages; track m.id) {
            <li [class.out]="m.direction === 'Outbound'" [class.failed]="m.status === 'Failed'">
              <div class="bubble">
                <p class="body">{{ m.body }}</p>
                <p class="meta">
                  {{ m.byName }} · {{ m.at | date: 'd MMM, HH:mm' }} · {{ label(m) }}
                  @if (m.error) { <br /><span class="error">{{ m.error }}</span> }
                </p>
              </div>
            </li>
          } @empty {
            <li class="empty">
              <span class="empty-icon" aria-hidden="true"><mat-icon>forum</mat-icon></span>
              <strong>No messages yet</strong>
              <span>{{ isTenant() ? 'Ask a question or let your landlord know about something. Replies show up here.' : 'Write the first message below.' }}</span>
            </li>
          }
        </ol>

        @if (!isTenant()) {
          <p class="window" [class.open]="t.whatsAppTextAllowed" role="status">
            <mat-icon aria-hidden="true">{{ t.whatsAppTextAllowed ? 'schedule_send' : 'info' }}</mat-icon>
            <span>
            @if (t.whatsAppTextAllowed) {
              Your reply goes to WhatsApp. The 24-hour window is open until {{ t.serviceWindowEndsAt | date: 'd MMM, HH:mm' }}.
            } @else if (!t.consent.granted) {
              No WhatsApp consent: your reply is kept in the app, where the tenant can read it.
            } @else {
              Outside WhatsApp's 24-hour window: a free-form reply is kept in the app only. Send a template to reach them on WhatsApp.
            }
            </span>
          </p>
        }
        <form class="composer" (ngSubmit)="send()" novalidate>
          <mat-form-field class="grow" subscriptSizing="dynamic">
            <mat-label>{{ isTenant() ? 'Write to your landlord' : 'Reply' }}</mat-label>
            <textarea matInput [formControl]="text" rows="2" maxlength="4096"></textarea>
          </mat-form-field>
          <div class="actions">
            <button mat-flat-button type="submit" class="send" [disabled]="busy() || !text.value.trim()">
              <mat-icon aria-hidden="true">send</mat-icon> Send
            </button>
            @if (!isTenant()) {
              <button mat-stroked-button type="button" (click)="template(t)" [disabled]="busy() || !t.consent.granted">Template…</button>
              @if (t.simulationAvailable) {
                <button mat-button type="button" (click)="simulate(t)" [disabled]="busy()"
                        title="Development only: pretend the tenant replied on WhatsApp">
                  <mat-icon aria-hidden="true">science</mat-icon> Simulate tenant reply
                </button>
              }
            }
          </div>
        </form>
        @if (isTenant()) {
          <p class="muted small foot">Messages you write here stay in the app. To use WhatsApp, message your landlord's business number.</p>
        }
      </section>
    } @else if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; min-width: 0; }
    .chat { display: flex; flex-direction: column; background: var(--mat-sys-surface-container-lowest);
            border: 1px solid var(--mat-sys-outline-variant); border-radius: 16px; overflow: hidden; }
    .thread-head { display: flex; align-items: center; gap: 12px; padding: 14px 18px;
                   border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .avatar { flex: none; display: grid; place-items: center; width: 40px; height: 40px; border-radius: 50%;
              background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container);
              font: var(--mat-sys-label-large); font-weight: 600; }
    .avatar mat-icon { font-size: 22px; width: 22px; height: 22px; }
    .who { display: flex; flex-direction: column; min-width: 0; flex: 1; }
    h2 { font: var(--mat-sys-title-medium); font-weight: 600; margin: 0; }
    .consent-wrap { padding: 16px 18px 0; }
    .messages { list-style: none; padding: 20px 18px; margin: 0; display: flex; flex-direction: column; gap: 10px;
                min-height: 240px; max-height: 55vh; overflow-y: auto; }
    .messages li { display: flex; }
    .messages li.out { justify-content: flex-end; }
    .bubble { max-width: min(75%, 560px); padding: 10px 14px; border-radius: 18px 18px 18px 6px;
              background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface); }
    .out .bubble { border-radius: 18px 18px 6px 18px; background: var(--mat-sys-primary); color: var(--mat-sys-on-primary); }
    .failed .bubble { outline: 2px solid var(--mat-sys-error); outline-offset: 2px; }
    .body { margin: 0; white-space: pre-line; overflow-wrap: anywhere; }
    .meta { margin: 4px 0 0; font: var(--mat-sys-body-small); opacity: 0.8; }
    .out .error { color: inherit; font-weight: 600; }
    .messages li.empty { flex: 1; flex-direction: column; align-items: center; justify-content: center; text-align: center; gap: 6px;
                         color: var(--mat-sys-on-surface-variant); padding: 24px 16px; }
    .empty strong { color: var(--mat-sys-on-surface); font: var(--mat-sys-title-medium); }
    .empty span:last-child { max-width: 42ch; }
    .empty-icon { display: grid; place-items: center; width: 64px; height: 64px; border-radius: 50%; margin-bottom: 6px;
                  background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .empty-icon mat-icon { font-size: 32px; width: 32px; height: 32px; }
    .window { display: flex; gap: 8px; align-items: flex-start; margin: 0 18px; padding: 10px 12px; border-radius: 10px;
              background: var(--mat-sys-tertiary-container); color: var(--mat-sys-on-tertiary-container); font: var(--mat-sys-body-medium); }
    .window mat-icon { flex: none; font-size: 20px; width: 20px; height: 20px; }
    .window.open { background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .composer { display: flex; gap: 12px; align-items: flex-start; flex-wrap: wrap; padding: 14px 18px 16px;
                border-top: 1px solid var(--mat-sys-outline-variant); background: var(--mat-sys-surface-container-low); margin-top: 12px; }
    .grow { flex: 1; min-width: 240px; --app-field-fill: var(--mat-sys-surface-container-lowest); }
    .grow { --mat-form-field-outlined-container-color: var(--mat-sys-surface-container-lowest); }
    .actions { display: flex; gap: 8px; flex-wrap: wrap; padding-top: 6px; }
    .actions button { border-radius: 22px; height: 44px; }
    .foot { margin: 0; padding: 0 18px 14px; background: var(--mat-sys-surface-container-low); }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .small { font: var(--mat-sys-body-small); }
    .error { color: var(--mat-sys-error); }
  `,
})
export class ThreadView {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  readonly tenantId = input.required<string>();
  /** Emitted after anything changes, so a list beside it can refresh. */
  readonly updated = output<void>();
  protected readonly thread = signal<Thread | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly isTenant = computed(() => this.auth.role() === 'Tenant');
  protected readonly text = inject(FormBuilder).nonNullable.control('', [Validators.maxLength(4096)]);
  private readonly list = viewChild<ElementRef<HTMLElement>>('list');

  constructor() {
    effect(() => void this.load(this.tenantId()));
    // Keep the newest message in view.
    effect(() => {
      this.thread();
      const el = this.list()?.nativeElement;
      if (el) queueMicrotask(() => (el.scrollTop = el.scrollHeight));
    });
  }

  protected label(m: ChatMessage): string {
    return deliveryLabel(m);
  }

  protected initials(name: string): string {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map((w) => w[0].toUpperCase()).join('');
  }

  private async load(tenantId: string): Promise<void> {
    try {
      this.thread.set(await firstValueFrom(this.api.getThread(tenantId)));
      this.updated.emit();
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected consentChanged(consent: ConsentStatus): void {
    void this.load(this.tenantId());
    this.snack.open(consent.granted ? 'WhatsApp messages are on.' : 'WhatsApp messages are off.', 'Close', { duration: 3000 });
  }

  async send(): Promise<void> {
    const body = this.text.value.trim();
    if (!body) return;
    if (await this.run(this.api.sendMessage(this.tenantId(), body))) this.text.reset();
  }

  protected async template(t: Thread): Promise<void> {
    const chosen = await firstValueFrom(
      this.dialog
        .open<TemplateDialog, { tenantName: string; landlordName: string }, { name: string; parameters: string[] }>(TemplateDialog, {
          data: { tenantName: t.tenantName, landlordName: this.auth.user()?.displayName ?? '' },
        })
        .afterClosed(),
    );
    if (chosen) await this.run(this.api.sendTemplate(t.tenantId, chosen.name, chosen.parameters));
  }

  protected async simulate(t: Thread): Promise<void> {
    const body = await firstValueFrom(
      this.dialog
        .open<TextDialog, TextDialogData, string>(TextDialog, {
          data: {
            title: `Simulate ${t.tenantName.split(' ')[0]} replying on WhatsApp`,
            label: 'Their message',
            hint: 'Goes through the real signed webhook path. Reply STOP to try withdrawing consent.',
            confirmLabel: 'Simulate',
            required: true,
            maxLength: 1000,
          },
        })
        .afterClosed(),
    );
    if (body) await this.run(this.api.simulateInbound(t.tenantId, body));
  }

  private async run(request: Observable<Thread>): Promise<boolean> {
    this.busy.set(true);
    try {
      this.thread.set(await firstValueFrom(request));
      this.updated.emit();
      return true;
    } catch (e) {
      this.snack.open(errorMessage(e), 'Close');
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}

/** Landlord: every conversation, unread first, and the selected thread beside it. */
@Component({
  selector: 'app-messages-page',
  imports: [RouterLink, DatePipe, FormsModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatSelectModule, ThreadView],
  template: `
    <header class="head">
      <h1>Messages</h1>
      <mat-form-field subscriptSizing="dynamic" class="new">
        <mat-label>Message a tenant</mat-label>
        <mat-select [(ngModel)]="newTenant" (selectionChange)="open($event.value)">
          @for (t of tenants(); track t.id) { <mat-option [value]="t.id">{{ t.firstName }} {{ t.lastName }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </header>
    @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    <div class="layout">
      <nav aria-label="Conversations" class="list">
        <ul>
          @for (c of conversations(); track c.tenantId) {
            <li>
              <a [routerLink]="['/messages', c.tenantId]" [class.active]="c.tenantId === tenantId()"
                 [attr.aria-current]="c.tenantId === tenantId() ? 'page' : null">
                <span class="name">{{ c.tenantName }}
                  @if (c.unread > 0) { <span class="unread">{{ c.unread }} new</span> }
                </span>
                <span class="preview">{{ c.lastMessagePreview }}</span>
                <span class="small">
                  {{ c.lastMessageAt | date: 'd MMM, HH:mm' }}
                  · {{ !c.consentGranted ? 'No WhatsApp consent' : c.inServiceWindow ? 'WhatsApp window open' : 'Templates only' }}
                </span>
              </a>
            </li>
          } @empty {
            <li class="empty">No conversations yet.</li>
          }
        </ul>
      </nav>
      <div class="thread">
        @if (tenantId(); as id) {
          <app-thread [tenantId]="id" (updated)="refresh()" />
        } @else {
          <p class="muted">Choose a conversation.</p>
        }
      </div>
    </div>
  `,
  styleUrl: '../../shared/list-page.scss',
  styles: `
    .new { min-width: 240px; }
    .layout { display: grid; grid-template-columns: minmax(240px, 320px) 1fr; gap: 24px; align-items: start; }
    .list ul { list-style: none; padding: 0; margin: 0; border: 1px solid var(--mat-sys-outline-variant); border-radius: 16px; overflow: hidden;
                background: var(--mat-sys-surface-container-lowest); }
    .list li + li { border-top: 1px solid var(--mat-sys-outline-variant); }
    .list a { display: flex; flex-direction: column; gap: 2px; padding: 12px 16px; color: inherit; text-decoration: none; }
    .list a:hover, .list a:focus-visible { background: var(--mat-sys-surface-container); }
    .list a.active { background: var(--mat-sys-secondary-container); color: var(--mat-sys-on-secondary-container); }
    .name { font: var(--mat-sys-title-small); display: flex; gap: 8px; align-items: center; }
    .unread { background: var(--mat-sys-primary); color: var(--mat-sys-on-primary); border-radius: 10px; padding: 0 8px;
              font: var(--mat-sys-label-small); }
    .preview { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .small { font: var(--mat-sys-body-small); color: var(--mat-sys-on-surface-variant); }
    .active .small { color: inherit; }
    .empty, .muted { color: var(--mat-sys-on-surface-variant); padding: 12px; }
    @media (max-width: 800px) { .layout { grid-template-columns: 1fr; } }
  `,
})
export class MessagesPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  /** Route parameter (optional). */
  readonly tenantId = input<string>();
  protected readonly conversations = signal<ConversationSummary[]>([]);
  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly error = signal<string | null>(null);
  protected newTenant = '';

  constructor() {
    void this.refresh();
    void firstValueFrom(this.api.listTenants({ pageSize: 100, sort: 'lastName' })).then((p) => this.tenants.set(p.items));
  }

  async refresh(): Promise<void> {
    try {
      this.conversations.set(await firstValueFrom(this.api.listConversations()));
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected async open(tenantId: string): Promise<void> {
    this.newTenant = '';
    await this.router.navigate(['/messages', tenantId]);
  }
}

/** Tenant: their conversation with the landlord. */
@Component({
  selector: 'app-my-messages',
  imports: [ThreadView],
  template: `
    <h1>Messages</h1>
    @if (tenantId; as id) { <app-thread [tenantId]="id" /> }
  `,
  styles: `h1 { font: var(--mat-sys-headline-medium); margin: 0 0 16px; }`,
})
export class MyMessages {
  protected readonly tenantId = inject(AuthService).user()?.tenantId ?? null;
}
