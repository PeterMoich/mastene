# Security review (OWASP Top 10, 2021)

What the app does about each risk, and what is still open. Reviewed for Phase 6.

| Risk | What's in place | Open |
| --- | --- | --- |
| **A01 Broken access control** | Every endpoint needs a signed-in user unless marked public (fallback policy). Role policies on each action. Data is filtered by landlord or tenant inside the query, so another tenant's record is a 404, not a 403 (ids can't be probed). Integration tests per area check that tenants can't see each other's data (BR01). | — |
| **A02 Cryptographic failures** | TLS 1.2+ everywhere in Azure, HTTPS only, HSTS. Azure SQL TDE and Storage encryption at rest. Passwords hashed by ASP.NET Core Identity (PBKDF2). Refresh tokens stored as SHA-256 hashes. Secrets only in Key Vault or GitHub secrets. | Data Protection keys are stored unencrypted in the database (protected by TDE); wrapping them with a Key Vault key would add a layer |
| **A03 Injection** | EF Core parameterises every query; the only raw SQL is interpolated (`ExecuteSqlInterpolated`, so parameterised). Sorting is from a whitelist. Angular escapes all output; no `innerHTML`. A strict CSP (`script-src 'self'`). | — |
| **A04 Insecure design** | Business rules in the domain with tests named after them. Check-then-write rules (overlapping leases, photo limits) run under database locks. File uploads typed by their bytes, size-capped before reading, virus-scanned, stripped of location metadata, never served from a public URL. | — |
| **A05 Security misconfiguration** | Security headers on every response (nginx and API), CSP, `X-Frame-Options: DENY`, no server tokens. Swagger only in development. Non-root containers. Forwarded headers trusted only from the proxy, a fixed number of hops. | — |
| **A06 Vulnerable components** | CI fails on known-vulnerable NuGet packages (including transitive ones) and on high npm advisories. CodeQL on C# and TypeScript. Dependency updates are applied by hand (no Dependabot version-update PRs). | — |
| **A07 Identification and authentication** | Lockout after 5 failures for 15 minutes, rate limit of 10 sign-ins per minute per IP, the same answer for unknown emails and wrong passwords. 15-minute access tokens kept in memory; rotating refresh tokens in an HttpOnly, SameSite=Strict cookie scoped to `/api/v1/auth` and requiring an anti-forgery header; reuse revokes the whole session family. | No MFA; no password reset by email (no email sending in this project) |
| **A08 Software and data integrity** | Images built and tested by CI are the ones deployed (tagged by commit). The WhatsApp webhook checks Meta's HMAC signature before reading the body. The audit log is append-only (a database trigger blocks changes). | Images aren't signed (cosign) yet |
| **A09 Logging and monitoring** | Structured JSON logs with trace ids, OpenTelemetry traces and metrics, audit rows for every state change and every sign-in, lockout and user change, SLO and messaging alerts. No secrets, tokens or passwords are logged. | — |
| **A10 SSRF** | The only outbound calls are to fixed hosts (Meta's Graph API, ClamAV on localhost); no URL comes from a user. | — |

## Privacy (POPIA)

- **Minimal data**: no ID numbers; photos lose their GPS location and other identifying metadata on upload.
- **Consent** for WhatsApp, versioned and append-only (Phase 5).
- **Access**: a tenant downloads everything held about them (My details → Download all my data).
- **Erasure**: an Admin can erase a former tenant's details once no lease is in force, nothing is owed and
  the retention period (a setting, LEGAL REVIEW NEEDED) has passed. Erasing doesn't copy the old values into
  the audit log. Kept, without the person's details: leases, payments, receipts and the unit's maintenance history.
- **Open, needs legal review**: retention of the audit log (it still holds old values from before erasure) and
  of the stored PDFs, which name the tenant.
