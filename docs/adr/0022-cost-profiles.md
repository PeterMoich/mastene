# ADR-0022: Cost profiles (demo and production)

Status: Accepted (5 November 2026)

## Context

The Phase 6 Azure setup (ADR-0017) is sized for real use: a Premium P0v3 plan (needed for the staging
slot), an S0 database and a ClamAV sidecar, about R1,500 to R2,000 a month. The site is mostly a
portfolio and trial site. Karabo asked for it to scale down when the free credit runs out.

An Azure free account isn't billed by itself: when the credit runs out or the 30 days end, the
subscription is disabled until it is upgraded to pay-as-you-go. So there is nothing to react to at that
moment; the size after an upgrade is what costs money.

## Decision

- `costProfile` in `infra/main.bicep`, `demo` by default:
  - **demo**: B1 plan, Basic database, no staging slot, no ClamAV (the EICAR check stays on), availability
    test every 15 minutes from two places. Releases replace the running containers and roll back to the
    previous images if readiness or the smoke checks fail.
  - **production**: P0v3, S0, staging slot with blue-green swaps, ClamAV, availability test every
    5 minutes from three places.
- A monthly **budget** on the resource group emails at 50%, 80% and 100% of the amount, and when the month
  is forecast to go over. Nothing is switched off automatically: a surprise shutdown is worse for a demo
  than an email.
- The `infra` workflow removes what demo doesn't use (the staging slot and the ClamAV container) before
  deploying, because Azure keeps resources a template stops mentioning and won't move a plan with slots to
  Basic.
- The Azure SQL free offer was considered and rejected: Hangfire polls the database every few seconds, so
  the database never pauses and the monthly free allowance would run out in about two days, pausing the
  app for the rest of the month.

## Consequences

- A demo release has a minute or two of restart instead of a zero-downtime swap.
- A rollback after a release that ran a migration puts the old code on the new schema; migrations are
  backward compatible (see docs/operations.md), so that keeps working.
- Uploads on a demo site are only checked for the EICAR test file, not real malware. That is acceptable
  for synthetic demo data and not for real tenants' files, which is one more reason real use needs
  `production`.
