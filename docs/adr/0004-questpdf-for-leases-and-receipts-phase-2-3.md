# ADR-0004: QuestPDF for leases and receipts (Phase 2-3)

Status: Accepted (4 November 2026)

## Context

Leases and receipts must be generated as PDFs.

## Decision

QuestPDF under its Community licence, behind an `IPdfRenderer` interface. Layout is C# code; the lease template wording is configuration and is itself flagged LEGAL REVIEW NEEDED.

## Consequences

Rendering is testable (render to bytes and assert on the text). Not yet used in Phase 1.
