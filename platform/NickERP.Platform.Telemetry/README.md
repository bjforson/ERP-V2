# NickERP.Platform.Telemetry

OpenTelemetry tracing + metrics for every NickERP service, with one-call wiring and Seq's OTLP receiver as the default backend.

## Usage

```csharp
// Program.cs (after UseNickErpLogging)
var builder = WebApplication.CreateBuilder(args);
builder.UseNickErpLogging("NSCIM.API");
builder.UseNickErpTelemetry("NSCIM.API");
// ... rest of host setup
```

That's it. The service immediately emits:

- **Traces** — spans for every inbound HTTP request (ASP.NET Core), every outbound `HttpClient` call, every manual span you create on `NickErpActivity.Source`. Exported to Seq's OTLP endpoint at `http://localhost:5341/ingest/otlp` by default.
- **Metrics** — request rate / duration / error rate (ASP.NET Core), HttpClient latencies, .NET runtime counters (GC, threads, exceptions, allocations). Same OTLP endpoint.
- **Console exporter** — auto-on in `Development` environment, off in production unless `NickErp:Telemetry:ConsoleExporter=true`.
- **Resource attributes** — `service.name`, `service.version`, `deployment.environment`, `nickerp.service` on every span and metric. Use these in Seq filters.

## Manual spans / metrics

Use the static `NickErpActivity` to keep names under one root:

```csharp
using NickERP.Platform.Telemetry;

using var span = NickErpActivity.Source.StartActivity("inspection.case.review");
span?.SetTag("case.id", caseId);
span?.SetTag("location.id", locationId);

// ... business logic ...

if (verdict is not null)
{
    span?.SetTag("verdict", verdict.Decision.ToString());
}

// counters
private static readonly Counter<long> CasesReviewed =
    NickErpActivity.Meter.CreateCounter<long>("nickerp.inspection.cases_reviewed");

CasesReviewed.Add(1, new KeyValuePair<string, object?>("verdict", verdict.Decision));
```

Span and metric names should follow `nickerp.<module>.<action>` convention. Tag keys follow OTel semantic conventions where they exist (`http.method`, `db.system`, etc.).

## Configuration overrides (`appsettings.json`)

```json
{
  "NickErp": {
    "Telemetry": {
      "OtlpEndpoint": "http://localhost:5341/ingest/otlp",
      "OtlpHeaders": "",
      "ConsoleExporter": false,
      "Environment": "Production",
      "ServiceVersion": "1.0.0"
    }
  }
}
```

`OtlpEndpoint` — point at any OTLP/HTTP receiver (Seq, Tempo, Jaeger, Honeycomb, Grafana Cloud). Default is Seq on TEST-SERVER.

`OtlpHeaders` — comma-separated `key=value` pairs for backends that need API keys (e.g. Honeycomb: `x-honeycomb-team=KEY`).

`ConsoleExporter` — when `true`, additionally emits to console. Defaults to on in `Development`, off elsewhere.

## Querying traces in Seq

Seq treats OTLP-received spans as events. The OTel resource attributes go under the `Resource` field on each event (NOT `Properties`). Useful filters in the Seq UI:

- `Resource['service']['name'] = 'NSCIM.API'` — all spans from one service
- `Resource['nickerp']['service'] like 'NickHR%'` — same, via the NickERP-specific resource attribute
- `Resource['deployment']['environment'] = 'Production'` — env-scoped
- `@TraceId = '7bd54fa8...'` — pull every span of one trace across services
- `@Message like '%inspection.case%'` — filter by span name (Seq stores it on the message template)

Span-level tags you set with `span.SetTag(...)` appear as event `Properties` (e.g. `@Properties['case.id'] = 'C-12345'`).

OTLP-received spans correlate with structured logs from `NickERP.Platform.Logging` via the shared `@TraceId` / `CorrelationId` — pin a trace in the trace view and Seq highlights all logs within that trace's time window.

If you adopt Tempo or Jaeger later, just point `OtlpEndpoint` at the new backend — service code doesn't change. Note that the per-signal path appendage (`/v1/traces`, `/v1/metrics`) is handled internally by `UseNickErpTelemetry`, so set `OtlpEndpoint` to the BASE URL of the OTLP receiver, not a signal-specific URL.

## What auto-instrumentation covers

| Source | Spans | Metrics |
|---|---|---|
| ASP.NET Core | inbound HTTP, exceptions, route resolution | `http.server.request.duration`, request counts |
| HttpClient | outbound HTTP, retries, DNS | `http.client.request.duration` |
| .NET runtime | — | GC counts, heap size, thread pool, exception count, allocations |

For database tracing, opt the consuming service into:

- **Npgsql** ≥ 6.0 ships built-in OTel via `Npgsql.OpenTelemetry`. Add the package and call `tracerProviderBuilder.AddNpgsql()` (or rely on the Npgsql data-source's built-in span emission).
- **EF Core** — wait for the EF team's first-party instrumentation, or use `OpenTelemetry.Contrib.Instrumentation.EntityFrameworkCore` if comfortable with prerelease.

The Telemetry package leaves these out so it doesn't drag prerelease dependencies into every consuming service.

## Roadmap reference

This package implements **Track A.1.3** of `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md`. Adjacent layers:

- A.1.1 ✅ Seq install (running on TEST-SERVER)
- A.1.2 ✅ `NickERP.Platform.Logging`
- A.1.4 demo app exercising both Logging + Telemetry (next)
- A.1.5 PLATFORM.md observability conventions (next)
