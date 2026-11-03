# ADR-0008: Append-only audit log in the main database

Status: Accepted (4 November 2026)

## Context

Every state change must be audited (BR20), and the audit must not disagree with the data.

## Decision

An EF Core `SaveChangesInterceptor` adds one `AuditLogs` row per inserted, updated or soft-deleted entity marked `IAuditable`, in the same `SaveChanges` call and therefore the same transaction. Each row records time, user, entity type and id, action, the changed fields before and after (JSON), and the request trace id. An `INSTEAD OF UPDATE, DELETE` trigger makes the table append-only at the database level.

## Consequences

The audit can never miss or invent a change. Identity's own user and role changes are not yet audited; that is noted for Phase 6. Personal data inside audit rows will need handling when POPIA erasure is built.
