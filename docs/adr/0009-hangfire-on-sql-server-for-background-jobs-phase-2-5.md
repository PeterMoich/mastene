# ADR-0009: Hangfire on SQL Server for background jobs (Phase 2-5)

Status: Accepted (4 November 2026)

## Context

Rent-due, overdue, lease-expiry and SLA reminders run on a schedule.

## Decision

Hangfire with SQL Server storage. Jobs are idempotent: each reminder records what it sent for which period. The Hangfire dashboard is restricted to Admin.

## Consequences

One extra schema in the same database. Implemented in Phase 2 with one recurring job, `lease-lifecycle`, at 00:15 Africa/Johannesburg. Phase 4 adds `issue-sla` every 15 minutes (ADR-0015). Hangfire is off in tests and in the migrator (`Jobs:Enabled`); the same logic runs from `POST /api/v1/lease-checks/run`. The Hangfire dashboard is not exposed yet: it can't read the API's bearer tokens, so it needs its own sign-in, planned with the reminder jobs in Phase 5.
