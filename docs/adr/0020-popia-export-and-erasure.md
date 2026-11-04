# ADR-0020: POPIA access and erasure

Status: Accepted (4 November 2026)

## Context

The brief asks that a tenant's personal data can be exported or deleted where the law allows, with legal
review flagged rather than guessed. Some records must be kept (tax, legal claims), and the audit log is
append-only by design (ADR-0008).

## Decision

- **Export**: the tenant (My details) or an Admin downloads a zip: `tenant.json` with every record about them
  (details, login, leases and signatures, charges, payments, deposits, issues and comments, completed
  inspections, consent history, messages) plus their lease PDFs, proofs of payment, receipts and the issue
  photos they uploaded. Each export is audited.
- **Erasure (anonymisation)**: Admin only, and refused while a lease is in force, money is owed, or the
  retention period after the last lease (setting, default 5 years, LEGAL REVIEW NEEDED) hasn't passed. It
  replaces the tenant's name, email and phone, the login's email and name (and locks it), WhatsApp message
  texts, consent phone numbers, the tenant's issue comments and names in issue histories, signature names and
  IP addresses, and inspection comments. It keeps lease, payment, receipt and maintenance records, without
  the person's details.
- **The audit log records what was erased, not the old values**: during erasure the audit interceptor writes
  `[erased]` in place of changed text values, so erasing doesn't copy the data into the append-only log.
- **Photos lose their location on upload**: the GPS part of EXIF is emptied (orientation kept), and XMP, IPTC,
  comments and PNG text chunks are removed.

## Consequences

LEGAL REVIEW NEEDED, and left open: how long the audit log (which holds values from before erasure) and the
stored PDFs (which name the tenant) must or may be kept, and how to purge them. No purge job exists yet. A
purge would need a separate, approved process, because the audit table blocks changes.
