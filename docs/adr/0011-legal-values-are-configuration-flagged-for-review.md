# ADR-0011: Legal values are configuration, flagged for review

Status: Accepted (4 November 2026)

## Context

South African rental law and POPIA apply, but this project has no legal adviser. The brief says not to invent rules.

## Decision

No legal rule is hard-coded. Each value below is a setting with a placeholder default marked `LEGAL REVIEW NEEDED` in settings, seed data and UI help text. The defaults exist so the app runs; they are not legal advice and must be confirmed by a qualified person before any real use.

| Setting | Placeholder default | Why it needs review |
| --- | --- | --- |
| `Deposit.InterestBearingAccountRequired` | true | Rental Housing Act deposit handling |
| `DepositRefundDays` (implemented in Phase 3) | 14 | Refund timing depends on inspection and deductions |
| `Deposit.RequireJointInspection` | true | Move-in and move-out inspection rules. Phase 4 records the landlord's completed checklist and the tenant's acknowledgement (with an optional comment), but doesn't enforce a joint inspection or block deductions without one |
| `Lease.TerminationNoticeDaysTenant` | 20 business days | CPA cancellation rules for fixed-term leases |
| `Lease.RenewalNoticeDaysBeforeEnd` | 40 business days | CPA expiry notice requirement |
| `Lease.MonthToMonthOnLapse` | true | Continuation terms after expiry |
| `Lease.MaxEscalationPct` | 10 | Commercial choice; check against the lease template |
| `Arrears.LateFeeEnabled` | false | Limits on penalties and interest |
| `Popia.RetentionYearsAfterLeaseEnd` | 5 | Retention versus erasure requests |
| `Lease.TemplateVersion` | v0-test | The template wording itself |

Phase 1 also collects no ID numbers: POPIA's minimality principle says not to collect them until a feature needs them.

## Consequences

Settings land with the features that use them (Phases 2 to 5).
