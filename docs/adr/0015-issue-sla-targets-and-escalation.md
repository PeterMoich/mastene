# ADR-0015: Issue targets, escalation and the issue history

Status: Accepted (4 November 2026)

## Context

BR14 asks for an issue flow (Open → Acknowledged → In progress → Resolved → Closed) with timestamps
so response times can be measured against an SLA. BR15 asks for target response and resolution times
per priority, and escalation when a target is missed. Reopening a resolved issue clears its resolved
time, so the timestamps alone can't show the full history.

## Decision

- **Targets per priority**, in hours from when the issue was opened, stored as nine columns on the
  settings row (response and resolution for each of four priorities, plus the auto-close days):
  Urgent 4/24, High 24/72, Medium 48/168, Low 72/336, auto-close 7 days. More urgent priorities can't
  have slower targets, and a response target can't be later than its resolution target.
- **Calendar hours**, not business hours. A tenant without water at 22:00 on a Saturday is still
  without water. Business-hours targets would need a holiday calendar; they can be added later if
  wanted. The values are suggestions for a small residential landlord, not legal deadlines; whether
  some repairs have a legal deadline is a question for legal review.
- **Due times are stored on the issue** (`ResponseDueAt`, `ResolutionDueAt`) when it is opened and
  recalculated from the opening time when the landlord changes the priority. Changing the settings
  later doesn't move the targets of issues already open.
- **Escalation is a flag, not a status.** The `issue-sla` Hangfire job runs every 15 minutes (and as
  part of `POST /lease-checks/run`). It records `ResponseMissedAt` when an issue isn't acknowledged by
  its response target and `ResolutionMissedAt` when it isn't resolved by its resolution target, each
  once, and sets `IsEscalated`. A missed target stays recorded even if the priority is lowered
  afterwards, so the SLA report stays honest. Phase 5 will use these timestamps to send the
  escalation message.
- **History is append-only.** Every step writes an `IssueEvent` (kind, who, when, note); the timeline
  is built from these and the comments. The Issue row itself is audited as usual (BR20).
- **Who does what.** The landlord acknowledges, starts, resolves, assigns contractors and changes the
  priority. The tenant confirms the fix or reopens it with a reason; the landlord does this only for an
  issue on a vacant unit. Unconfirmed resolved issues close themselves after the auto-close days.
- **Photos**: at most five per issue, checked again under an application lock (`issue:{id}`) so two
  uploads at once can't make six, and each file only once per issue (unique index on IssueId, Sha256).

## Consequences

Response and resolution times can be measured per issue and summarised per priority (the SLA
report). The job does a small indexed query every 15 minutes. Not handled: business-hours targets,
pausing the clock while waiting for the tenant (e.g. no access), and photo metadata. Phone photos can
carry GPS coordinates in their EXIF data; stripping it needs re-encoding the image, which is flagged
for Phase 6.
