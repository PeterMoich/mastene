# ADR-0007: Single landlord per environment, OwnerId kept

Status: Accepted (4 November 2026)

## Context

The product serves one landlord, but the brief asks for room to grow.

## Decision

No multi-tenancy middleware. Properties and tenants carry an indexed `OwnerId`. A landlord's owner id is their own user id; a tenant user's owner id is their landlord's, carried in the `owner_id` claim. Every landlord query filters by it.

## Consequences

Supporting several landlords later means onboarding and a query filter, not a schema change. An admin can create a second Landlord user today; that user simply sees their own empty portfolio.
