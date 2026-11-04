# ADR-0019: The refresh token lives in an HttpOnly cookie; proxies are trusted by network and hop count

Status: Accepted (4 November 2026). Supersedes the sessionStorage part of ADR-0002.

## Context

Until Phase 6 the web app kept the 7-day refresh token in `sessionStorage`, where any script injected into
the page could read it (flagged since Phase 1). Separately, the API trusted `X-Forwarded-For` from anyone, so
a client could fake its IP address to get round the sign-in rate limit.

## Decision

- **Refresh token**: set by the API as an `HttpOnly`, `SameSite=Strict` cookie, `Path=/api/v1/auth`,
  `Secure` over HTTPS (always in Azure). It is never in a response body, so page scripts can't read it. The
  15-minute access token stays in memory only. After a reload the app calls `/auth/refresh` and the browser
  sends the cookie.
- **CSRF**: refresh and logout also require an `X-Requested-With` header. A cross-site form can't send one,
  and a cross-site script would need a CORS preflight, which the API never allows. `SameSite=Strict` already
  stops cross-site sends; the header is a second lock.
- **Forwarded headers**: trusted only from private networks (configurable) and for a fixed number of hops:
  1 behind nginx in compose, 2 in App Service (its front end, then nginx). In Azure the container is only
  reachable through App Service's front end.

## Consequences

Sessions now survive closing the tab, for up to 7 days or until sign-out (the cookie has an expiry).
Non-browser API clients sign in and refresh with the cookie too.
