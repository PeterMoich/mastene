# ADR-0013: Lease overlap is checked under application locks

Status: Accepted (4 November 2026)

## Context

BR03 says a unit has at most one lease in force for any date, and (from the Phase 0 review) a tenant
can't hold leases on two units in the same property at the same time. Both are "check, then write"
rules across several rows, so two requests arriving together could each pass the check.

The Phase 0 design proposed a filtered unique index on `Leases(UnitId)` for leases in force. That
doesn't work: a signed renewal waits for its start date while the current lease is still Active on
the same unit, and SQL Server checks unique indexes per statement, so swapping a renewal in
(old to Renewed, new to Active) could fail depending on the order EF Core sends the two updates.

## Decision

- The rule is a date check, not a status check: leases in force (waiting for signatures, Active,
  Expiring) may not have overlapping dates on the same unit, or for the same tenant in the same
  property. A month-to-month lease holds its unit until a termination date is set. A renewal is not
  compared with the lease it renews.
- The check runs when a draft is created or edited (early feedback) and again, authoritatively, when
  it is sent for signing. Drafts don't count, so a landlord can prepare alternatives.
- The authoritative check runs inside a transaction holding SQL Server application locks
  (`sp_getapplock`) on `unit:{id}` and `tenant:{id}`, taken in sorted order so two requests never
  deadlock. The second request waits, then sees the first lease and gets 409 `lease.overlap`.
- Signing takes a lock on `lease:{id}`, so a landlord and tenant signing at the same moment can't
  both miss that the lease is now fully signed.
- Database constraints still guard what they can: each party signs once (`LeaseId, Party`), each
  expiry notice is recorded once (`LeaseId, DaysBefore`), and a lease has at most one renewal that
  hasn't been cancelled.

## Consequences

The rule holds under concurrency, which the integration test `BR03_Concurrent_generation_...` checks
against a real SQL Server. The locks are SQL Server specific and live in Infrastructure behind
`IApplicationDbContext.InLockedTransactionAsync`. The PDF file is written before the transaction
commits, so a failed commit can leave an unreferenced file in storage; a clean-up job can be added
if that ever matters.
