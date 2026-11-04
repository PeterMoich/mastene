# ADR-0004: QuestPDF for leases and receipts (Phase 2-3)

Status: Accepted (4 November 2026)

## Context

Leases and receipts must be generated as PDFs.

## Decision

QuestPDF under its Community licence, behind an `IPdfRenderer` interface. Layout is C# code; the lease template wording is configuration and is itself flagged LEGAL REVIEW NEEDED.

## Consequences

Rendering is testable: the integration tests extract the PDF text with PdfPig and check the parties, amounts and signature record. Implemented in Phase 2 (`QuestPdfLeaseRenderer`, template `v0-test`); receipts follow in Phase 3.
