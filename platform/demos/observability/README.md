# NickERP.Platform.Demos.Observability

End-to-end smoke check for **Track A.1**: validates that any new service can wire `NickERP.Platform.Logging` + `NickERP.Platform.Telemetry` in two lines and have logs, traces, and metrics flow into Seq automatically.

This is a **throwaway demo** — it exists to keep the platform contracts honest before Track B starts. Do not import or consume from production services.

## Prerequisites

- Seq running on TEST-SERVER (`http://localhost:5341`). See main `ROADMAP.md` Track A.1.1.
- .NET 10 SDK.

## Run

```bash
cd /c/Shared/NSCIM_PRODUCTION/platform/demos/observability
dotnet run
```

The demo listens on `http://localhost:5259`.

## Endpoints

| Method | Route | What it exercises |
|---|---|---|
| GET | `/demo?caseId=ABC` | Information log + custom parent span (`demo.handle_request`) + nested span (`demo.synthetic_work`) + metric counter increment |
| GET | `/boom` | Error log with exception + span status `Error` + `Activity.AddException` |
| GET | `/openapi/v1.json` | OpenAPI doc |

Hit them with curl:

```bash
curl 'http://localhost:5259/demo?caseId=DEMO-001'
curl 'http://localhost:5259/boom'
```

## What to look for in Seq

Open `http://localhost:5341` (login `admin`).

1. **Logs** — filter `ServiceName = 'Platform.Demos.Observability'`. You should see:
   - `Demo request received for case DEMO-001` with `CorrelationId` filled
   - `Demo /boom endpoint hit; raising synthetic exception` at level `Error` with the stack trace

2. **Traces** — Seq treats OTLP spans as events. Filter `Resource['service']['name'] = 'Platform.Demos.Observability'`. You should see:
   - `demo.handle_request` (parent)
   - `demo.synthetic_work` (child, ~25ms)
   - `demo.boom` (with `@SpanKind = 'Internal'`, status `Error`)
   - Auto-instrumented spans for the inbound HTTP requests (`GET /demo`, `GET /boom`)
   - The `@TraceId` from each span matches the `CorrelationId` on the corresponding log line

3. **Metrics** — `nickerp.demos.observability.requests_served` counter increments per `/demo` hit. ASP.NET Core auto-instrumented metrics also flow (`http.server.request.duration`, etc.).

## What this proves

If all three telemetry types appear correctly attributed to the service in Seq, the platform contracts work. Any new NickERP service can adopt them with the same two lines:

```csharp
builder.UseNickErpLogging("MyService");
builder.UseNickErpTelemetry("MyService");
```

## Roadmap reference

This demo is **Track A.1.4** of `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md`. Once Track B starts, this folder can be deleted — real modules become the validation.
