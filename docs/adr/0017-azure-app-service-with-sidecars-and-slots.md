# ADR-0017: Azure App Service with sidecar containers, staging slot swaps and feature flags

Status: Accepted (4 November 2026)

## Context

The brief asks for deployment to Azure from GitHub Actions, blocked by any failing test or scan, with
staging slots or blue-green swaps, feature flags for risky features and infrastructure as code for
everything. The app is two containers (nginx + Angular, and the API) that expect to share an origin, plus
an optional virus scanner.

## Decision

- **One Linux App Service running the containers as sidecars** (`web` main, `api`, `clamav`), which share
  `localhost` like a compose network. The browser keeps one origin (no CORS), nginx proxies `/api` to
  `http://localhost:5080`, and one deployment unit means one swap releases both containers together.
  nginx's upstream is a template variable, so the same image runs in compose and in Azure.
- **Blue-green with a staging slot.** `deploy.yml` runs only after CI succeeds on `master`, points the staging
  slot at that commit's images, waits for readiness, smoke-tests, swaps, checks production and swaps back if
  it isn't healthy. Background jobs and demo seeding stick to the production slot.
- **Migrations on start-up, backward compatible**: the new code migrates in staging before the swap, so the
  old code briefly runs on the new schema. Destructive changes take two releases.
- **Bicep for every resource**, built and linted in CI on every push: App Service, slot, SQL, Storage, Key
  Vault (secrets referenced from app settings, read with the managed identity), Log Analytics, Application
  Insights, availability test, alerts and the SLO workbook. `infra.yml` deploys it (what-if first).
- **Feature flags as app settings** (`Features:WhatsAppSending`, `Features:Reminders`), read on each use, so a
  risky feature can be switched off in seconds without a deploy.
- **GitHub signs in to Azure with OIDC**, so no Azure password is stored in GitHub.

## Consequences

Low cost (one P0v3 plan) and a simple mental model. Scaling out scales all three containers together,
which is fine at this size. Not done: Azure Front Door/WAF, private endpoints for SQL and Storage, image
signing. Each can be added without changing the app. The workflows are written but can't be run until an
Azure subscription is connected (docs/operations.md, "First-time setup").
