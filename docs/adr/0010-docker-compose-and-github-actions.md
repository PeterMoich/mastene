# ADR-0010: docker-compose as the primary runtime; GitHub Actions for CI

Status: Accepted (4 November 2026)

## Context

Everything must run on a developer machine with one command. Azure is optional.

## Decision

`compose.yaml` runs `sqlserver`, `azurite`, `migrator`, `api` and `web` (nginx serving Angular and proxying `/api`, so the browser sees one origin and no CORS is needed). Secrets come from a git-ignored `.env`. GitHub Actions builds with warnings as errors, audits NuGet and npm packages, runs unit, integration (Testcontainers SQL Server) and component tests, runs CodeQL, and on `main` publishes public images to `ghcr.io/petermoich/mastene-api` and `mastene-web`. Any failing step blocks the images.

## Consequences

Linux containers only. Azure deployment and Bicep are optional in Phase 6. Containers run as non-root users.
