# ADR-0002: ASP.NET Core Identity with JWT and rotating refresh tokens

Status: Accepted (4 November 2026)

## Context

The brief allowed Identity or Entra ID. This is a test project with local users and no corporate directory.

## Decision

ASP.NET Core Identity on SQL Server. Access tokens are JWTs signed with HMAC-SHA256 and valid for 15 minutes; claims are `sub`, `email`, `name`, `role`, `owner_id` and (for tenants) `tenant_id`, with inbound claim mapping switched off. Refresh tokens are 256-bit random values, valid for 7 days, stored only as SHA-256 hashes and rotated on every use. Presenting an already-used refresh token revokes every session for that user (theft detection). Five failed logins lock the account for 15 minutes. Policies: `AdminOnly`, `LandlordOnly`, `AdminOrLandlord`, `LandlordOrTenant`, and a fallback policy requiring authentication on every endpoint not marked `[AllowAnonymous]`.

## Consequences

No external identity provider. Entra ID can be added later as a second authentication scheme. The Angular app keeps the refresh token in `sessionStorage`; moving it to an httpOnly cookie is planned for Phase 6 hardening.
