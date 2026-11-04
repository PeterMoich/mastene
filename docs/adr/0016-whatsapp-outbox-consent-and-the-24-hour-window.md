# ADR-0016: WhatsApp through an outbox, with consent and the 24-hour window checked twice

Status: Accepted (4 November 2026)

## Context

Phase 5 connects tenants on WhatsApp (ADR-0006): reminders and updates sent automatically, free-form
conversation, and a webhook for replies and delivery statuses. WhatsApp's rules: a business may send
free-form messages only within 24 hours of the customer's last message, and pre-approved templates
otherwise. POPIA requires consent for these messages. The brief asks for consent to be recorded,
the window respected and webhook signatures verified (BR16 to BR18).

## Decision

- **Consent is append-only** (`Consents`): every grant and withdrawal is a row with the wording
  version, the phone number, who recorded it and how. The latest row decides, and only for the number
  it was given for. Tenants agree in the app; the landlord may record consent given elsewhere only with
  a note. Replying STOP (or UNSUBSCRIBE) withdraws it.
- **One conversation per tenant** holds `LastWhatsAppInboundAt`; the window is open for 24 hours after
  it. Messages a tenant writes in the app are kept but don't open the window.
- **A landlord's free-form reply outside the window is kept in the app** rather than refused, and the
  screen says it didn't go to WhatsApp. Templates reach the tenant at any time, with consent.
- **Outbox.** Every outbound WhatsApp message is first a `Queued` row, written in the same transaction
  as the change that caused it (a verified payment, an issue step, a reminder), so a message is queued
  if and only if the change is saved. `MessageDispatcher` sends queued messages straight after the
  request and every minute in Hangfire. Before sending it checks consent and, for free-form text, the
  window again, because either can change while a message waits. Each attempt is claimed first (a row
  version check), so two dispatchers never send the same message; failures are retried up to five times.
- **Each automatic message once.** A dedupe key per cause (`payment-received:{id}`,
  `rent-overdue:{chargeId}`, ...) with a unique index. Reminders older than a week are not sent late.
- **Reminders run at 08:50**, not in the midnight lease job, so nobody is messaged at 00:15.
- **Webhook.** The signature (`X-Hub-Signature-256`, HMAC-SHA256 of the raw body with the app secret)
  is checked in constant time before the body is parsed. Inbound messages are stored once per WhatsApp
  message id, statuses only move forward (sent, delivered, read) and failures are recorded with
  their reason. Messages from numbers that aren't a tenant's are not stored.
- **Providers.** `IWhatsAppProvider` has a fake implementation (the default) and the Cloud API one,
  chosen by `WhatsApp:Provider`. The fake's "simulate a reply" builds and signs a Meta-format payload
  and passes it through the real webhook handler, so the signature path is exercised in development.
- **Templates** are defined in code (`MessageTemplates`), mirroring what must be approved in Meta's
  WhatsApp Manager. The stored message keeps the filled-in text, so the thread shows what was sent.
- **Hangfire dashboard** (left open in ADR-0009): an Admin-only, two-minute ticket from the API is
  exchanged for an HttpOnly, SameSite=Strict cookie scoped to `/hangfire`, valid 30 minutes.

## Consequences

The whole flow runs and is tested without a Meta account. Sending is at-least-once: if the process
dies after WhatsApp accepted a message but before it was marked sent, it is sent again. Not handled:
media messages (stored as a placeholder), template approval status from Meta, the landlord being
told on WhatsApp about escalated issues, and paging threads beyond the latest 200 messages.
LEGAL REVIEW NEEDED: the consent wording, the opt-out keywords, and how long messages are kept.
