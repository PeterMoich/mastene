# ADR-0009: Hangfire on SQL Server for background jobs (Phase 2-5)

Status: Accepted (4 November 2026)

## Context

Rent-due, overdue, lease-expiry and SLA reminders run on a schedule.

## Decision

Hangfire with SQL Server storage. Jobs are idempotent: each reminder records what it sent for which period. The Hangfire dashboard is restricted to Admin.

## Consequences

One extra schema in the same database. Not yet used in Phase 1.
