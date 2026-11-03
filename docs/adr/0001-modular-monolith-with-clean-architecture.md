# ADR-0001: Modular monolith with clean architecture

Status: Accepted (4 November 2026)

## Context

One small team, one landlord per environment, and a short-lived test project. A distributed design would add operational cost with no benefit.

## Decision

One deployable API split into four projects with dependencies pointing inward: `Api → Application → Domain`, and `Infrastructure` implements Application's interfaces. Domain has no package references. Each use case is a method on an application service (`PropertyService`, `TenantService`), called directly by a thin controller.

**Changed in Phase 1:** the design proposed a small in-house command dispatcher (`ISender`) with pipeline behaviours. Plain services turned out simpler for the same result: validation is one explicit `ValidateAndThrowAsync` call per write, and auditing and timestamps are handled by an EF Core interceptor rather than a pipeline step.

## Consequences

Easy to run, debug and test. Aggregates are separated by folder and namespace, so a later split is possible but not free.
