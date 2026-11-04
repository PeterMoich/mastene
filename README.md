# Tenant Manager (mastene)

A web app for a small South African residential landlord: properties and units, tenants,
leases, rent and proof of payment, maintenance issues, and WhatsApp messaging.
It is a portfolio and test project; all data in it is synthetic.

**Status: Phase 3 of 6** — authentication and roles, properties, units and tenants (Phase 1);
12-month leases with PDF generation, signing, renewals and a daily lifecycle job (Phase 2); and
rent charges, proof of payment, the rent ledger, receipts and deposits (Phase 3). Issues and
WhatsApp follow in Phases 4 and 5.

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

To start again from an empty database: `docker compose down -v`. If you ran Phase 1 before, do this
once so the Phase 2 demo leases are seeded.

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
| BR19 — a tenant with a lease in force or a balance cannot be deleted; soft delete | `TenantTests`, `TenantApiTests`, `PaymentApiTests` |
| BR20 — every state change is audited; the audit log is append-only | `AuditTests`, `LeaseApiTests` |

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
docker/                         Dockerfiles and nginx config
docs/adr/                       architecture decision records
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

### API (v1)

| Method | Route | Role |
| --- | --- | --- |
| POST | `/api/v1/auth/login`, `/auth/refresh`, `/auth/logout` | anyone |
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
| GET, PUT | `/api/v1/settings` | GET Admin and Landlord, PUT Admin |
| GET, POST | `/api/v1/users`; PUT `/users/{id}/role`, `/users/{id}/lock` | Admin |
| GET | `/api/v1/audit` | Admin, Landlord |
| GET | `/health/live`, `/health/ready` | anyone |

## CI

`.github/workflows/ci.yml` runs on every push to `master` and every pull request:

1. Backend: build with warnings as errors (NuGet vulnerability audit included), check transitive
   packages for known vulnerabilities, unit tests, integration tests against SQL Server.
2. Frontend: `npm ci`, lint, component tests, production build, `npm audit`.
3. CodeQL security scan of the C# and TypeScript code.
4. Docker images for the API and web app, published to `ghcr.io/petermoich/mastene-*` from `master`.

A failure in any of the first three blocks the images.

## Legal note

South African rental law and POPIA apply to an app like this, but no legal rules are invented here.
Notice periods, escalation limits, month-to-month continuation and the deposit refund period are settings with placeholder
defaults marked **LEGAL REVIEW NEEDED** ([ADR-0011](docs/adr/0011-legal-values-are-configuration-flagged-for-review.md)),
which an Admin can change on the Settings page. The lease PDF wording (template `v0-test`) is
placeholder text, and every page carries a "SAMPLE TEST DOCUMENT - LEGAL REVIEW NEEDED" banner.
All of it must be confirmed by a qualified person before any real use.
