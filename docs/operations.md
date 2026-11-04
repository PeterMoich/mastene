# Operations

How Tenant Manager runs in Azure, how a release goes out, and what to do when something is wrong.
Nothing here needs clicking around the Azure portal: the infrastructure is in `infra/` (Bicep) and the
releases are in `.github/workflows/`.

## What runs where

| Piece | Azure resource | Notes |
| --- | --- | --- |
| Web app + API | One Linux App Service (P0v3) running three containers: `web` (nginx + Angular, the public one), `api` (ASP.NET Core and Hangfire) and `clamav` (virus scanner) | Same shape as docker compose; the containers share `localhost` |
| Releases | A `staging` deployment slot | Blue-green: each release starts in staging, then the slots swap |
| Database | Azure SQL (S0), TLS 1.2, transparent data encryption | Point-in-time restore for 7 days (Azure default) |
| Documents and photos | Storage account, private container `documents`, ZRS, soft delete 30 days | Files are only served through the API |
| Secrets | Key Vault (RBAC, purge protection) | App settings refer to secrets; the app reads them with its managed identity |
| Telemetry | Application Insights + Log Analytics | Traces, metrics, container logs, availability test, alerts, SLO workbook |

## First-time setup

1. **Create a resource group** (South Africa North is closest): `az group create -n rg-mastene-prod -l southafricanorth`.
2. **Let GitHub sign in to Azure without a password (OIDC).** Create an app registration with a federated
   credential for `repo:PeterMoich/mastene:environment:production`, and give it *Contributor* and *User Access
   Administrator* (for the Key Vault role assignments) on the resource group.
3. **In GitHub → Settings → Secrets and variables → Actions** add:
   - secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `SQL_ADMIN_PASSWORD`,
     `JWT_SIGNING_KEY` (e.g. `openssl rand -base64 48`), and `SEED_PASSWORD` if you want the demo data;
   - variables: `AZURE_RESOURCE_GROUP`, and `ALERT_EMAIL` for alerts.
   Create an environment called `production`; add yourself as a required reviewer to approve each release.
4. **Make the container images readable**: in GitHub → Packages, set `mastene-api` and `mastene-web` to public,
   or pass `registryUsername`/`registryPassword` (a token with `read:packages`) to the Bicep deployment.
5. **Run the `infra` workflow** with "what if" ticked to preview, then again unticked to create everything.
   Copy the `siteName` output into the `AZURE_SITE_NAME` variable. The deploy workflow now runs after every
   green CI build on `master`.

## A release

`deploy.yml` runs after CI passes on `master` (or by hand, for any commit sha whose images exist):

1. The staging slot's `api` and `web` containers are pointed at the images CI built for that commit.
2. Staging restarts. The new API applies database migrations on start-up (EF Core holds a lock, so a
   second instance just waits). Staging never runs background jobs or seeds data: those settings stick to
   the production slot.
3. Staging is checked: readiness (API and database), the page, the security headers, and that the API and
   the WhatsApp webhook refuse unauthenticated calls.
4. The slots swap. Production keeps serving during the swap.
5. Production must be ready within five minutes, or the workflow swaps back.

**Migrations must be backward compatible**: just before the swap the *old* code runs against the *new*
schema. Add columns and tables freely; remove or rename them in two releases (stop using it, then drop it).

## Rolling back

Run in Cloud Shell, or locally after `az login`:

```bash
az webapp deployment slot swap -g <rg> -n <site> --slot staging --target-slot production
```

After a swap the previous release is in the staging slot, so swapping again goes back. To go further back,
run the deploy workflow by hand with an older commit sha. Database migrations aren't undone automatically;
because they are backward compatible, the older code keeps working.

## Feature flags

Switch a risky feature off with an app setting. No deploy is needed; it takes effect within seconds:

| Setting | Off means |
| --- | --- |
| `Features__WhatsAppSending` | Nothing is sent to WhatsApp; messages wait in the queue and go out when it's switched on again |
| `Features__Reminders` | The daily rent, lease and renewal reminders aren't queued |

```bash
az webapp config appsettings set -g <rg> -n <site> --settings Features__WhatsAppSending=false
```

## Service levels (SLIs and SLOs)

| SLI | SLO (30 days) | Alert (fast burn) |
| --- | --- | --- |
| Share of API requests answered in under 500 ms (health probes excluded) | 99% | More than 5% slower than 500 ms in an hour |
| Share of API requests that don't fail with a 5xx | 99.5% | More than 2% 5xx in 15 minutes |
| Readiness check from Johannesburg, Amsterdam and Virginia | — | Failing from 2 or more locations |
| WhatsApp send failures and rejected webhook calls | — | 5 or more in an hour |

The **Tenant Manager - service levels** workbook (Application Insights → Workbooks) shows the 30-day
compliance, latency percentiles, errors, the slowest endpoints and the business events (payments verified,
issues opened and escalated, messages sent and failed).

Useful queries (Application Insights → Logs):

```kusto
// One request, end to end, with its database and HTTP calls
union requests, dependencies | where operation_Id == "<traceId from a ProblemDetails error>"

// Container logs (Serilog JSON) for a trace
AppServiceConsoleLogs | where ResultDescription has "<traceId>"
```

Every error the API returns carries a `traceId`, so a user's screenshot of an error leads straight to the trace.

## When something is wrong

| Symptom | Check | Fix |
| --- | --- | --- |
| Site down or "unavailable" alert | App Service → Log stream; `/health/ready` | A failed release: roll back. Database: Azure SQL → Query editor works? |
| `/health/ready` fails, `/health/live` works | The database connection | Key Vault reference status (App Service → Configuration); SQL firewall "Allow Azure services" |
| Uploads refused "scanner isn't available" | The `clamav` container is still loading virus definitions (a few minutes after a restart) | Wait; or set `Scanning__Provider=Eicar` temporarily (not for long: nothing is scanned) |
| WhatsApp messages stuck as "Sending…" | `Features__WhatsAppSending`; the `messages-dispatch` job in the Hangfire dashboard | Meta token expired: update the `whatsapp-access-token` secret in Key Vault, restart |
| Reminders didn't go out | Hangfire dashboard → Recurring jobs → `reminders` (08:50 SAST) | Trigger it from the dashboard |

The Hangfire dashboard opens from **Settings → Open the background jobs dashboard** (Admins only).

## Secrets

All secrets live in Key Vault; the repository and the app settings never hold one. To rotate:

| Secret | How |
| --- | --- |
| `jwt-signing-key` | Set a new value and restart. Everyone signs in again (sessions are signed with it) |
| `sql-password` (in `sql-connection`) | Change the SQL admin password, update `sql-connection`, restart |
| `storage-connection` | Regenerate key 2, update the secret, restart, then regenerate key 1 |
| `whatsapp-*` | Update from Meta's app settings, restart |

## Backups and restore

- **Database**: Azure SQL keeps point-in-time backups for 7 days. To restore, use Azure SQL → Restore to a new
  database, check it, then swap the `sql-connection` secret to point at it.
- **Files**: soft delete keeps deleted blobs for 30 days (Storage → Containers → Show deleted blobs).
- **Audit log**: append-only in the same database, so it is restored with it.
