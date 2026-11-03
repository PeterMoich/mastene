# ADR-0012: Angular standalone app with signals and Angular Material

Status: Accepted (4 November 2026)

## Context

The brief asks for the latest Angular, standalone components, a typed client, WCAG 2.1 AA and responsive layouts.

## Decision

Angular 22 (zoneless, standalone components, signals), Angular Material 3 components, lazy-loaded routes per role guarded by `roleGuard`, and an HTTP interceptor that attaches the access token and refreshes once on a 401. ESLint runs Angular's template accessibility rules. Component tests use the Angular CLI's Vitest runner.

**Changed in Phase 1:** the typed client (`core/api.service.ts`, `core/api.models.ts`) is written by hand rather than generated with NSwag. With roughly twenty endpoints, a hand-written client is easier to read and review; generation from the OpenAPI document can replace it once the API surface settles.

## Consequences

Route guards are only a convenience; the API enforces every rule. Hand-written models can drift from the API, which the integration tests and a later generated client guard against.
