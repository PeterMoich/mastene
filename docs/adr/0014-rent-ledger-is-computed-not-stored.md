# ADR-0014: The rent ledger is computed, not stored

Status: Accepted (4 November 2026)

## Context

BR09 asks for a ledger per lease: amount due, paid, balance and arrears, with partial payments.
The Phase 0 data model had a `PaymentAllocation` table (which payment paid which month) and a
separate `Receipt` table. Stored allocations have to be kept right whenever anything changes: a
payment marked for a later month, an overpayment that should pay next month's rent once it is
charged, or a charge raised late. Each of those is a chance for the stored figures to disagree with
the payments and charges they came from.

## Decision

- **Stored:** rent charges (`RentCharges`, one per lease per month, unique) and proofs of payment
  (`Payments`). These are the facts.
- **Computed on every read:** the ledger. `RentLedger.Compute` takes the charges and the *verified*
  payments and allocates each payment, in the order it was verified, to the month it was marked for
  and then to the oldest unpaid months. What is left is credit, which pays later months as they are
  charged. It is a pure function in the Domain, so every rule is unit-tested without a database.
- **Receipts** are a snapshot: when a payment is verified, the receipt PDF records how it was applied
  at that moment and the balance after it. The receipt number and file live on the payment row
  (`ReceiptNumber`, `ReceiptKey`) instead of a separate table.
- A charge's state is Paid, Due, or Overdue (past the due date plus the grace period). Arrears is the
  unpaid part of overdue charges. Overdue reminders are recorded once per charge (`OverdueNoticeOn`).
- Partial first and last months are charged by days when `ProrateRent` is on (the default). This is
  a commercial choice, so it is a setting.

## Consequences

The ledger can never disagree with the payments and charges, and changing how allocation works means
changing one function. The cost is computing it on each read, which is trivial at this scale (a lease
has at most a few dozen charges). A charge raised for an earlier month after later payments were
verified can move where those payments are shown as applied; receipts already issued keep what they
said, which is the right behaviour for a receipt. Not handled yet, and flagged for Phase 6: crediting
rent already charged for days after a termination that was scheduled mid-month, and late fees
(`Arrears.LateFeeEnabled`, a legal question).
