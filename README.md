# Tenant Manager (mastene)

A web app for a small South African residential landlord: properties and units, tenants,
leases, rent and proof of payment, maintenance issues, and WhatsApp messaging.
It is a portfolio and test project; all data in it is synthetic.

**Status: Phase 1 of 6** — solution skeleton, CI, authentication and roles, and
properties, units and tenants. Leases, payments, issues and WhatsApp follow in Phases 2 to 5.

| Layer | Technology |
| --- | --- |
| API | ASP.NET Core 10, EF Core 10, SQL Server, ASP.NET Core Identity + JWT, FluentValidation, Serilog, Swagger |
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

### Demo logins

Every demo login uses the `SEED_PASSWORD` from your `.env`.

| Role | Email | Sees |
| --- | --- | --- |
| Landlord | `landlord@mastene.test` | Properties, units, tenants |
| Tenant | `sipho.dlamini@example.com` | Their own details only |
| Tenant | `lerato.mokoena@example.com` | Their own details only |
| Admin | `admin@mastene.test` | Staff user accounts only |

The demo data is two properties in Gauteng with five units, and three tenants (two with logins).
To start again from an empty database: `docker compose down -v`.

## Develop without Docker

Requirements: .NET SDK 10.0.403 or later, Node.js 22, and a SQL Server you can reach
(the compose `sqlserver` service on its own is easiest: `docker compose up sqlserver`).

```bash
# API on http://localhost:5080 (migrates and seeds on start in Development)
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

Business rules are named in test names (e.g. `BR19_Tenant_with_active_lease_cannot_be_deleted`)
so a failing test points straight at the rule. Phase 1 covers:

| Rule | Where it's tested |
| --- | --- |
| BR01 — a tenant only sees their own data | `AccessControlTests` |
| BR19 — a tenant with an active lease or balance cannot be deleted; soft delete | `TenantTests`, `TenantApiTests` |
| BR20 — every state change is audited; the audit log is append-only | `AuditTests` |

## Architecture

```
src/
  TenantManager.Domain          entities and business rules; no package references
  TenantManager.Application     use cases (services), validators, DTOs, interfaces
  TenantManager.Infrastructure  EF Core, Identity, JWT, audit interceptor, seeding
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
| GET, POST | `/api/v1/users`; PUT `/users/{id}/role`, `/users/{id}/lock` | Admin |
| GET | `/api/v1/audit` | Admin, Landlord |
| GET | `/health/live`, `/health/ready` | anyone |

## CI

`.github/workflows/ci.yml` runs on every push to `main` and every pull request:

1. Backend: build with warnings as errors (NuGet vulnerability audit included), check transitive
   packages for known vulnerabilities, unit tests, integration tests against SQL Server.
2. Frontend: `npm ci`, lint, component tests, production build, `npm audit`.
3. CodeQL security scan of the C# and TypeScript code.
4. Docker images for the API and web app, published to `ghcr.io/petermoich/mastene-*` from `main`.

A failure in any of the first three blocks the images.

## Legal note

South African rental law and POPIA apply to an app like this, but no legal rules are invented here.
Values such as deposit handling and notice periods will be settings with placeholder defaults
marked **LEGAL REVIEW NEEDED** ([ADR-0011](docs/adr/0011-legal-values-are-configuration-flagged-for-review.md)).
They must be confirmed by a qualified person before any real use.
