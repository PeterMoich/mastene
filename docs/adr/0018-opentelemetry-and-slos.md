# ADR-0018: OpenTelemetry, Application Insights and SLOs

Status: Accepted (4 November 2026)

## Context

The brief asks for OpenTelemetry tracing, structured logging, Application Insights, defined SLIs/SLOs and a
dashboard.

## Decision

- **OpenTelemetry for traces and metrics.** With `APPLICATIONINSIGHTS_CONNECTION_STRING` set (Azure) the Azure
  Monitor distro exports everything to Application Insights, including ASP.NET Core, HttpClient and SQL
  spans. With `OTEL_EXPORTER_OTLP_ENDPOINT` set, any OpenTelemetry collector gets them instead. Health probes
  aren't traced.
- **Business metrics** on a `TenantManager` meter: payments verified, issues opened and escalated, WhatsApp
  messages sent and failed, and webhook calls rejected.
- **Logs stay on Serilog's JSON console output**, which carries the trace and span ids. App Service ships
  container output to Log Analytics, where logs and traces join on the trace id. Every API error includes
  its `traceId`.
- **SLOs over 30 days**: 99% of API requests under 500 ms; 99.5% of API requests without a 5xx. Alerts fire on
  a fast burn (more than 5% slow in an hour; more than 2% errors in 15 minutes) and on readiness failing
  from two of three regions. A workbook shows compliance, latency, errors, the slowest endpoints and the
  business metrics (docs/operations.md).

## Consequences

The SLOs are a starting point for a small landlord's app; they should be reviewed once there is real
traffic. Log export through OpenTelemetry was left out: Serilog already writes structured logs that App
Service collects.
