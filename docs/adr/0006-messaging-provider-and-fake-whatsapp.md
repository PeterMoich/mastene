# ADR-0006: Messaging behind IMessagingProvider with a fake provider first (Phase 5)

Status: Accepted (4 November 2026)

## Context

WhatsApp Business Cloud API needs a Meta account, a business number and approved templates, none of which a test project has.

## Decision

An `IMessagingProvider` interface (`SendTextAsync`, `SendTemplateAsync`, `ParseInbound`, `VerifySignature`). A `FakeWhatsAppProvider` stores messages in the database, and a development-only endpoint simulates inbound replies. The webhook contract mirrors Meta's payload and its `X-Hub-Signature-256` HMAC header, so a `MetaCloudApiProvider` can be added without touching the rest of the app.

## Consequences

The whole flow, including the 24-hour service window and consent checks, is testable with no Meta account.
