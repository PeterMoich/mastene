# ADR-0005: File storage behind IFileStorage (Phase 2-3)

Status: Accepted (4 November 2026)

## Context

Proof of payment, lease PDFs and issue photos need storage that works locally and in Azure.

## Decision

An `IFileStorage` interface with a local-disk implementation for development and an Azure Blob implementation (Azurite locally) chosen by `Storage:Provider`. Keys are opaque GUID paths and the database stores key plus SHA-256. Files are served only through authorised API endpoints, never through public URLs. Type and size are checked on the server by file signature, not extension.

## Consequences

Moving to Azure is a configuration change. Azurite already runs in docker-compose.
