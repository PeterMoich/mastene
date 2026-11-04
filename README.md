# Tenant Manager (mastene)

A web app for a small South African residential landlord: properties and units, tenants,
leases, rent and proof of payment, maintenance issues, and WhatsApp messaging.
It is a portfolio and test project; all data in it is synthetic.

**Status: all six phases built.** Authentication and roles, properties, units and tenants (Phase 1);
12-month leases with PDF generation, signing, renewals and a daily lifecycle job (Phase 2); rent
charges, proof of payment, the ledger, receipts and deposits (Phase 3); maintenance issues with
photos, contractors, response and resolution targets, and inspections (Phase 4); WhatsApp messaging
with consent, the 24-hour window, templates, a signed webhook and reminders (Phase 5); and Azure
infrastructure as code, blue-green deployment, OpenTelemetry with SLOs, security hardening and POPIA
data export and erasure (Phase 6). Running it on Azure needs a subscription: see
[docs/operations.md](docs/operations.md).

| Layer | Technology |
| --- | --- |
| API | ASP.NET Core 10, EF Core 10, SQL Server, ASP.NET Core Identity + JWT, FluentValidation, Serilog, Swagger |
| Documents and jobs | QuestPDF (Community licence), Azure Blob Storage (Azurite locally), Hangfire on SQL Server |
| Web | Angular 22 (standalone, signals, zoneless), Angular Material 3 |
| Tests | xUnit + Shouldly, Testcontainers (real SQL Server), Vitest component tests |
| Run | Docker Compose; GitHub Actions for CI and container images |

## Run it

You need [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or Docker Engine with Compose v2).

```bash
cp .env.example .env      # then change the three values in .env
docker compose up --build
```

The first start takes a few minutes while SQL Server and the images download.

| What | Where |
| --- | --- |
| Web app | http://localhost:4200 |
| API docs (Swagger) | http://localhost:5080/swagger |
| SQL Server | `localhost,1433`, user `sa`, password from `.env` |
| Azurite (blob storage) | `localhost:10000`, the emulator's standard development account |

### Demo logins

Every demo login uses the `SEED_PASSWORD` from your `.env`.

| Role | Email | Sees |
| --- | --- | --- |
| Landlord | `landlord@mastene.test` | Properties, units, tenants |
| Tenant | `sipho.dlamini@example.com` | Their own details only |
| Tenant | `lerato.mokoena@example.com` | Their own details only |
| Admin | `admin@mastene.test` | Staff user accounts only |

The demo data is two properties in Gauteng with five units, three tenants (two with logins), and two
signed leases. Sipho's (Jacaranda Court, Flat 1) started eight months ago. Lerato's (Marula Gardens,
Cottage A) has about a month left, so it shows as **Expiring**. Johan has no lease, so you can try the
whole flow: as the landlord, create a draft for him, send it for signing and sign it; then give Johan
a login on the Tenants page, sign in as him and sign too.

The leases come with rent history. Sipho is paid up except this month, and his proof of payment for
it is waiting on the landlord's **Payments** page. Lerato paid only part of last month and nothing
this month, so she is in **arrears**. Every verified payment has a receipt PDF.

There are maintenance issues at every stage, and three contractors. Sipho's kitchen tap is in
progress with the plumber. Lerato's geyser was reported 30 hours ago and nobody acknowledged it, so it
missed its 24-hour response target and is **escalated**. Her gate remote is marked resolved and waits
for her to confirm it is fixed (or reopen it). Flat 2 is vacant and has the landlord's own painting
job. Both leases have a completed move-in inspection with photos; Sipho has acknowledged his, Lerato
hasn't yet. All photos are synthetic coloured blocks.

On **Messages**, Sipho has agreed to WhatsApp messages and wrote two hours ago about the plumber, so
his thread is inside WhatsApp's 24-hour window and a reply goes to WhatsApp. Lerato hasn't agreed, so
nothing goes to her on WhatsApp; replies to her stay in the app. WhatsApp itself is simulated (no Meta
account needed): use **Simulate tenant reply** to play the tenant, and reply `STOP` to see consent
withdrawn. Sign in as a tenant and open **My details** or **Messages** to give or withdraw consent.

To start again from an empty database: `docker compose down -v`. A database from an earlier phase
keeps its data and gets the Phase 4 demo issues, contractors and inspections added on the next start.

## Develop without Docker

Requirements: .NET SDK 10.0.403 or later, Node.js 22, and a SQL Server you can reach
(the compose `sqlserver` service on its own is easiest: `docker compose up sqlserver`).

```bash
# API on http://localhost:5080 (migrates and seeds on start in Development; PDFs go to ./data/files)
cd src/TenantManager.Api
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost,1433;Database=TenantManager;User Id=sa;Password=<your SA password>;Encrypt=False;TrustServerCertificate=True"
dotnet user-secrets set "Seed:DemoData" "true"
dotnet user-secrets set "Seed:Password" "<a demo password>"
dotnet run

# Web app on http://localhost:4200, proxying /api to the API
cd web
npm ci
npm start
```

In Development, if `Jwt:SigningKey` is not set the API generates a random key at startup, so
sign-ins stop working after a restart. Set one with `dotnet user-secrets set "Jwt:SigningKey" "<32+ characters>"`.

## Run the tests

```bash
dotnet test tests/TenantManager.Domain.Tests          # domain rules and validators, no dependencies
dotnet test tests/TenantManager.Api.IntegrationTests  # needs Docker: starts a real SQL Server
cd web && npx ng test --watch=false                   # Angular component tests
cd web && npx ng lint                                 # ESLint incl. template accessibility rules
```

Business rules are named in test names (e.g. `BR19_Tenant_with_an_active_lease_cannot_be_deleted`)
so a failing test points straight at the rule. Covered so far:

| Rule | Where it's tested |
| --- | --- |
| BR01 — a tenant only sees their own data (including leases and lease PDFs) | `AccessControlTests`, `LeaseApiTests` |
| BR02 — a lease is always 12 months from its start (leap years included) | `LeaseTests`, `LeaseApiTests` |
| BR03 — no overlapping leases on a unit; one lease per tenant per property; safe under concurrency | `LeaseTests`, `LeaseApiTests` |
| BR04 — a lease can't change once sent for signing | `LeaseTests`, `LeaseApiTests` |
| BR05 — Expiring at 90 days; notices at 90, 60 and 30 days, each once | `LeaseTests`, `LeaseApiTests` |
| BR06 — renewal needs both signatures; escalation rounded to the nearest rand | `LeaseTests`, `LeaseApiTests` |
| BR07 — at the end with no renewal: month-to-month or ended, per setting | `LeaseTests`, `LeaseApiTests` |
| BR08 — rent due on a set day with a grace period, then overdue; one reminder per month | `RentLedgerTests`, `PaymentApiTests` |
| BR09 — partial payments, oldest month first, arrears | `RentLedgerTests`, `PaymentApiTests` |
| BR10 — the same proof file can't be submitted twice | `PaymentApiTests` |
| BR11 — rejection needs a reason; verifying issues a receipt | `ProofOfPaymentTests`, `PaymentApiTests` |
| BR12 — PDF/JPG/PNG up to 5 MB, typed from the file's bytes, virus-scan hook | `UploadValidatorTests`, `PaymentApiTests` |
| BR13 — deposit kept apart from rent; deductions with reasons, never more than held | `DepositTests`, `PaymentApiTests` |
| BR12 (photos) — JPG/PNG up to 5 MB, at most five per issue or per inspection area | `IssueTests`, `InspectionTests`, `IssueApiTests`, `InspectionApiTests` |
| BR14 — issue flow Open → Acknowledged → In progress → Resolved → Closed, timestamped; only the tenant confirms or reopens | `IssueTests`, `IssueApiTests` |
| BR15 — targets per priority; a missed target escalates, once; priority changes recalculate | `IssueTests`, `SlaTargetTests`, `IssueApiTests` |
| BR16 — WhatsApp only to tenants whose latest consent is a grant for their current number; STOP withdraws it | `ConsentTests`, `MessagingApiTests` |
| BR17 — free-form WhatsApp only inside the 24-hour window, templates otherwise; outside it a reply stays in the app | `ConversationTests`, `MessagingApiTests` |
| BR18 — the webhook checks Meta's HMAC signature; retried deliveries are stored once | `MessagingApiTests` |
| BR19 — a tenant with a lease in force or a balance cannot be deleted; soft delete | `TenantTests`, `TenantApiTests`, `PaymentApiTests` |
| BR20 — every state change is audited, including sign-ins and user changes; the audit log is append-only | `AuditTests`, `LeaseApiTests`, `IssueApiTests`, `SecurityAuditTests` |
| POPIA — a tenant's data export; erasure only when allowed, without copying old values into the audit log | `PrivacyApiTests` |
| Photos lose their GPS location (orientation kept) | `ImageMetadataTests` |

## Architecture

```
src/
  TenantManager.Domain          entities and business rules; no package references
  TenantManager.Application     use cases (services), validators, DTOs, interfaces
  TenantManager.Infrastructure  EF Core, Identity, JWT, audit interceptor, PDF rendering,
                                file storage, Hangfire jobs, seeding
  TenantManager.Api             controllers, auth policies, ProblemDetails, rate limits
web/                            Angular app
tests/                          domain tests and API integration tests
docker/                         Dockerfiles and the nginx config template
infra/                          Bicep for every Azure resource (ADR-0017)
docs/adr/                       architecture decision records
docs/operations.md              Azure setup, releases, rollback, feature flags, SLOs, runbook
docs/security.md                OWASP Top 10 review and POPIA
```

Dependencies point inward: `Api → Application → Domain`, and `Infrastructure` implements
Application's interfaces. Key choices, each explained in [docs/adr](docs/adr/README.md):

- **Security by default.** Every endpoint requires a signed-in user unless marked otherwise.
  Role policies sit on each controller action, and data is filtered by owner (landlord) or tenant
  in the query itself. Another tenant's record returns 404, not 403, so ids can't be probed.
- **Errors** are RFC 7807 ProblemDetails with a stable `code` (e.g. `tenant.has_active_lease`)
  and a `traceId`: 400 for invalid fields, 404, 409 for conflicts with current data, 422 for rule breaches.
- **Audit.** An EF Core interceptor writes one row per changed entity in the same transaction,
  and a database trigger blocks changes to the audit table.
- **Soft delete** for properties, units and tenants, with filtered unique indexes so a deleted
  "Flat 1" doesn't block a new one.
- **Rate limits:** 10 sign-in attempts per IP per minute, 300 requests per user per minute.
  Forwarded client IPs are trusted only from the proxy's network, for a fixed number of hops.
- **Sessions.** A 15-minute access token in memory; the refresh token is an HttpOnly, SameSite=Strict
  cookie for the auth endpoints only, which page scripts can't read
  ([ADR-0019](docs/adr/0019-refresh-token-in-an-httponly-cookie.md)).
- **Observability.** OpenTelemetry traces and metrics to Application Insights (or any OTLP collector),
  Serilog JSON logs with trace ids, SLOs with alerts ([ADR-0018](docs/adr/0018-opentelemetry-and-slos.md)).
- **Uploads** are typed by their bytes, size-capped, virus-scanned (ClamAV in Azure, optional in compose),
  and photos lose their location metadata.
- **Lists** take `page`, `pageSize` (max 100), `sort` (field name, `-` prefix for descending)
  and filters such as `search` and `status`.

### Leases

| Status | How it gets there | What can happen next |
| --- | --- | --- |
| Draft | Landlord creates it | Edit, discard, send for signing |
| Waiting for signatures | Sent for signing: the PDF is made and the lease is frozen (BR04) | Each party signs once; the landlord can cancel |
| Active | Both have signed and the start date has come | Renew, terminate; flagged Expiring 90 days before the end |
| Expiring | 90 days or less left (setting) | Renew, terminate |
| Active (month to month) | End date passed with no renewal, setting on (BR07) | Renew from a chosen date, terminate |
| Renewed | Its signed renewal started | Nothing; history |
| Ended | End date passed with no renewal, setting off (BR07) | Nothing; history |
| Terminated | Cancelled before signing, or its scheduled last day passed | Nothing; history |

- **Signing** records the user, time and IP address in the app. It is in-app confirmation, not a
  legal e-signature (flagged for legal review). The second signature produces a signed PDF.
- A **renewal** is version 2 (3, …) of the same lease number. It starts the day after the current
  lease ends, with rent raised by the agreed escalation and rounded to the nearest rand.
- **Overlaps** (BR03) are checked under a database lock on the unit and tenant when a lease is sent
  for signing, so two requests can't both pass ([ADR-0013](docs/adr/0013-lease-overlap-under-application-locks.md)).
- A **daily job** (Hangfire, 00:15 South African time) starts signed leases, swaps in renewals,
  flags expiring leases, records expiry notices for Phase 5 to send, lapses ended leases and applies
  scheduled terminations. Landlords can also run it from the Leases page ("Run daily checks now").
- PDFs are stored once and never overwritten, in Azure Blob Storage (Azurite locally) or a local
  folder, chosen by `Storage:Provider`. They are only served through the API, never by public URL.

### Rent, payments and deposits

- **Charges.** Each month a lease is in force gets one rent charge, raised when the lease starts and
  then by the daily job on the 1st. A partial first or last month is charged by days (a setting).
  Rent is due on the configured day and is **overdue** after the grace period; the job records one
  overdue reminder per month for Phase 5 to send.
- **Proof of payment.** The tenant (or the landlord on their behalf) uploads a PDF, JPG or PNG up to
  5 MB. The server checks the real file type from its first bytes, scans it, and refuses an exact
  duplicate. The landlord **verifies** it (it then counts, and a receipt PDF is issued) or **rejects**
  it with a reason.
- **Ledger.** Worked out from charges and verified payments each time it is shown, never stored, so
  it can't drift ([ADR-0014](docs/adr/0014-rent-ledger-is-computed-not-stored.md)). A payment pays
  the month it was marked for, then the oldest unpaid months; anything extra is credit.
- **Deposit.** Kept apart from rent and shared by every version of a lease. The landlord records
  when it was received; deductions (each with a reason) and the refund are recorded after the lease
  ends, and deductions can never exceed what is held.

### Issues, contractors and inspections

- **Logging an issue.** A tenant reports an issue on their running lease with a category, a
  priority, a description and up to five photos (JPG or PNG, 5 MB each, typed from the file's bytes
  and virus-scanned like proof of payment). The landlord can log one on any unit; on a let unit it
  is shared with the tenant.
- **Flow (BR14).** Open → Acknowledged → In progress → Resolved → Closed. The landlord moves it along
  (starting work also acknowledges it). The tenant confirms the fix, or reopens it with a reason; if
  they do neither, it closes by itself after 7 days (a setting). Each step is timestamped and written
  to the issue's history, which the detail page shows together with the comments.
- **Targets (BR15).** Each priority has a response target (acknowledge by) and a resolution target
  (resolve by), in hours from when the issue was logged: Urgent 4 h / 24 h, High 24 h / 72 h, Medium
  48 h / 7 days, Low 72 h / 14 days. They are calendar hours and are editable on the Settings page.
  A job every 15 minutes flags an issue as **escalated** when a target is missed; escalation is a flag,
  not a status, so the issue keeps its place in the flow
  ([ADR-0015](docs/adr/0015-issue-sla-targets-and-escalation.md)). The landlord's **SLA report**
  shows, per priority, how many issues met each target and the average times.
- **Contractors** are a simple list (name, trade, phone), not logins. They can be assigned to an
  issue, and the tenant sees who is coming and their number. A contractor still assigned to an
  unfinished issue can't be deleted.
- **Inspections.** Each lease number has one move-in and one move-out checklist. The landlord fills in
  each area (condition, notes, up to five photos), then completes it, after which it can't change; the
  tenant then sees it and acknowledges it, optionally with a comment. A move-out shows what each area
  was like at move-in and flags the ones that got worse. A renewal keeps the original move-in.
- **Photos** are served only through the API to signed-in users who may see them. The web app fetches
  them with the user's token and shows them from `blob:` URLs, so the page's CSP allows `img-src blob:`.

### WhatsApp messaging and reminders

- **One thread per tenant**, across WhatsApp and the app. The landlord sees all of them, unread first;
  a tenant sees their own and can write in the app too.
- **Consent (BR16).** Nothing goes to a tenant on WhatsApp unless their latest consent record is a
  grant for their current phone number. Tenants agree in the app (the wording is versioned and stored
  with each record); the landlord can record consent given another way, with a note saying how. A
  changed phone number needs new consent. Replying `STOP` on WhatsApp withdraws it.
- **The 24-hour window (BR17).** WhatsApp allows free-form messages only within 24 hours of the
  tenant's last WhatsApp message. Inside it, the landlord's reply goes to WhatsApp; outside it, the
  reply is kept in the app (the tenant still sees it there) and the screen says so. To reach the tenant
  on WhatsApp then, send a **template**. Messages the tenant writes in the app don't open the window.
- **Automatic messages** (all templates): a receipt when a payment is verified, the reason when one is
  rejected, issue updates (acknowledged, work started, resolved), and the daily reminders: rent due in
  3 days (a setting), rent overdue, lease ending (90/60/30 days) and a renewal waiting for the
  tenant's signature. Each is sent once. Reminders older than a week aren't sent late.
- **Outbox.** Messages are saved as queued in the same transaction as the change that caused them,
  then sent at once and by a job every minute, with retries. Consent and the window are checked again
  just before sending.
- **Webhook (BR18).** `POST /api/v1/webhooks/whatsapp` takes Meta's payload, refuses anything without a
  valid `X-Hub-Signature-256` (HMAC-SHA256 of the body with the app secret), stores messages from known
  tenants once (by WhatsApp message id) and applies delivery statuses, which only move forward.
  Messages from unknown numbers aren't stored.
- **Providers.** The fake provider is the default: sends succeed (numbers ending in `0000` fail, to try
  failures) and **Simulate tenant reply** sends a signed payload through the real webhook code. Setting
  `WHATSAPP_PROVIDER=Meta` and the four Meta values in `.env` switches to the WhatsApp Cloud API; the
  eight templates must first be created and approved in Meta's WhatsApp Manager with the same names
  and wording ([ADR-0016](docs/adr/0016-whatsapp-outbox-consent-and-the-24-hour-window.md)).
- **Background jobs dashboard.** Admins open it from **Settings**. The API issues a two-minute link,
  which sets a 30-minute cookie for `/hangfire` only.

### Privacy (POPIA)

- **My details → Download all my data** gives a tenant a zip of everything held about them: a JSON file
  with every record, plus their leases, proofs of payment, receipts and issue photos.
- **Privacy requests** (Admin) exports any tenant's data, and erases a former tenant's personal details once
  no lease is in force, nothing is owed and the retention period after their last lease has passed (a
  setting, LEGAL REVIEW NEEDED). Lease, payment and maintenance records are kept, without their details.
  The audit log notes what was erased, not the old values
  ([ADR-0020](docs/adr/0020-popia-export-and-erasure.md)).

### API (v1)

| Method | Route | Role |
| --- | --- | --- |
| POST | `/api/v1/auth/login`, `/auth/refresh`, `/auth/logout` (refresh and logout use the cookie and need `X-Requested-With`) | anyone |
| GET | `/api/v1/me` | signed in |
| GET, POST | `/api/v1/properties` | Landlord |
| GET, PUT, DELETE | `/api/v1/properties/{id}` | Landlord |
| GET, POST | `/api/v1/properties/{id}/units` | Landlord |
| GET, PUT, DELETE | `/api/v1/units/{id}` | Landlord |
| GET, POST | `/api/v1/tenants` | Landlord |
| GET | `/api/v1/tenants/{id}` | Landlord, or the tenant themself |
| PUT, DELETE | `/api/v1/tenants/{id}` | Landlord |
| POST | `/api/v1/tenants/{id}/login` | Landlord (returns a one-time password) |
| GET | `/api/v1/leases`, `/leases/{id}`, `/leases/{id}/versions` | Landlord, or the tenant (own, no drafts) |
| POST, PUT, DELETE | `/api/v1/leases`, `/leases/{id}` (drafts only) | Landlord |
| POST | `/api/v1/leases/{id}/generate`, `/renewals`, `/terminate` | Landlord |
| POST | `/api/v1/leases/{id}/sign` | Landlord, or the lease's tenant |
| GET | `/api/v1/leases/{id}/documents/{documentId}` (PDF) | Landlord, or the lease's tenant |
| POST | `/api/v1/lease-checks/run` | Landlord |
| GET | `/api/v1/leases/{id}/ledger` | Landlord, or the lease's tenant |
| GET, POST | `/api/v1/payments` (POST is a multipart upload) | Landlord, or the tenant (own) |
| GET | `/api/v1/payments/{id}`, `/payments/{id}/file`, `/payments/{id}/receipt` | Landlord, or the tenant (own) |
| POST | `/api/v1/payments/{id}/verify`, `/payments/{id}/reject` | Landlord |
| GET | `/api/v1/arrears` | Landlord |
| GET | `/api/v1/leases/{id}/deposit` | Landlord, or the lease's tenant |
| POST | `/api/v1/leases/{id}/deposit/receive`, `/deductions`, `/refund` | Landlord |
| GET, POST | `/api/v1/issues` | Landlord, or the tenant (own) |
| GET | `/api/v1/issues/{id}`, `/issues/{id}/photos/{photoId}` | Landlord, or the tenant (own) |
| POST | `/api/v1/issues/{id}/acknowledge`, `/start`, `/resolve`; PUT `/contractor`, `/priority` | Landlord |
| POST | `/api/v1/issues/{id}/close`, `/reopen` | the tenant (the landlord when there is no tenant) |
| POST | `/api/v1/issues/{id}/comments`, `/photos` (multipart) | Landlord, or the tenant (own) |
| GET | `/api/v1/issues/sla-report?days=90` | Landlord |
| GET, POST, PUT, DELETE | `/api/v1/contractors`, `/contractors/{id}` | Landlord |
| GET | `/api/v1/leases/{id}/inspections` | Landlord, or the lease's tenant (completed only) |
| POST | `/api/v1/leases/{id}/inspections` | Landlord |
| GET | `/api/v1/inspections/{id}`, `/inspections/{id}/photos/{photoId}` | Landlord, or the lease's tenant (completed only) |
| PUT, POST | `/api/v1/inspections/{id}`, `/items`, `/items/{itemId}`, `/items/{itemId}/photos`, `/complete` | Landlord |
| POST | `/api/v1/inspections/{id}/acknowledge` | the lease's tenant |
| GET | `/api/v1/conversations` | Landlord |
| GET | `/api/v1/conversations/{tenantId}` | Landlord, or the tenant (own) |
| POST | `/api/v1/conversations/{tenantId}/messages` | Landlord, or the tenant (own; kept in the app) |
| POST | `/api/v1/conversations/{tenantId}/templates` | Landlord |
| GET | `/api/v1/message-templates` | Landlord |
| GET, POST | `/api/v1/tenants/{tenantId}/consents` | Landlord, or the tenant (own) |
| GET, POST | `/api/v1/webhooks/whatsapp` | Meta (signed; GET is the verify handshake) |
| POST | `/api/v1/dev/whatsapp/inbound` | Landlord, fake provider only |
| POST | `/api/v1/admin/jobs-dashboard` | Admin (returns a one-time dashboard link) |
| GET | `/api/v1/tenants/{tenantId}/export` (zip) | Admin, or the tenant (own) |
| GET | `/api/v1/privacy/tenants` | Admin |
| POST | `/api/v1/tenants/{tenantId}/anonymise` | Admin |
| GET, PUT | `/api/v1/settings` | GET Admin and Landlord, PUT Admin |
| GET, POST | `/api/v1/users`; PUT `/users/{id}/role`, `/users/{id}/lock` | Admin |
| GET | `/api/v1/audit` | Admin, Landlord |
| GET | `/health/live`, `/health/ready` | anyone |

## CI and deployment

`.github/workflows/ci.yml` runs on every push to `master` and `phase-*`, and on every pull request:

1. Backend: build with warnings as errors (NuGet vulnerability audit included), check transitive
   packages for known vulnerabilities, unit tests, integration tests against SQL Server.
2. Frontend: `npm ci`, lint (with template accessibility rules), component tests, production build, `npm audit`.
3. CodeQL security scan of the C# and TypeScript code.
4. Bicep: the Azure templates are compiled and linted.
5. Smoke test: the whole stack is started with `docker compose` and exercised through nginx.
6. Docker images for the API and web app, published to `ghcr.io/petermoich/mastene-*` from `master`.

A failure in any of 1–5 blocks the images, and `deploy.yml` only runs after CI succeeds on `master`:
it releases to the staging slot, smoke-tests it, swaps it into production and swaps back if production
isn't healthy. `infra.yml` creates or updates the Azure resources. Both need an Azure subscription and
the setup in [docs/operations.md](docs/operations.md). Dependabot proposes dependency updates weekly.

## Legal note

South African rental law and POPIA apply to an app like this, but no legal rules are invented here.
Notice periods, escalation limits, month-to-month continuation, the deposit refund period, the inspection rules and the WhatsApp consent wording are settings or flags with placeholder
defaults marked **LEGAL REVIEW NEEDED** ([ADR-0011](docs/adr/0011-legal-values-are-configuration-flagged-for-review.md)),
which an Admin can change on the Settings page. The lease PDF wording (template `v0-test`) is
placeholder text, and every page carries a "SAMPLE TEST DOCUMENT - LEGAL REVIEW NEEDED" banner.
All of it must be confirmed by a qualified person before any real use.
