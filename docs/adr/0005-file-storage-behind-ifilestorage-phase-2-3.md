# ADR-0005: File storage behind IFileStorage (Phase 2-3)

Status: Accepted (4 November 2026)

## Context

Proof of payment, lease PDFs and issue photos need storage that works locally and in Azure.

## Decision

An `IFileStorage` interface with a local-disk implementation for development and an Azure Blob implementation (Azurite locally) chosen by `Storage:Provider`. Keys are opaque GUID paths and the database stores key plus SHA-256. Files are served only through authorised API endpoints, never through public URLs. Type and size are checked on the server by file signature, not extension.

## Consequences

Moving to Azure is a configuration change. Implemented in Phase 2: docker-compose uses `AzureBlobFileStorage` against Azurite (the CI smoke test downloads a lease PDF from it), and tests and `dotnet run` use `LocalFileStorage`. Uploads arrived in Phase 3 with proof of payment: `UploadValidator` checks size (refusing anything over 5 MB even if the request lies about its length), the real type from the file's first bytes, and a virus-scan hook (`IFileScanner`). The development scanner only recognises the EICAR test file; production must plug in a real scanner such as ClamAV or Microsoft Defender for Storage.
