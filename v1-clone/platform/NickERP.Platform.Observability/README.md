# NickERP.Platform.Observability

Cross-app observability stack for the NickERP suite. One project reference and four lines of `Program.cs` give you OpenTelemetry traces + metrics with OTLP export, structured Serilog logs (JSON file + console), a loopback-only Prometheus scrape endpoint, and a request-scoped correlation id that flows through traces, logs, and the response header. Opinionated defaults so apps don't each carry their own observability boilerplate.

## Opt-in (four lines)

In your app's `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddNickErpLogging("MyApp");
builder.AddNickErpObservability("MyApp");

// ... rest of registrations ...

var app = builder.Build();

// ... rest of pipeline ...

app.UseNickErpCorrelation();
app.MapNickErpMetrics();

app.Run();
```

And in your `.csproj`:

```xml
<ProjectReference Include="..\..\platform\NickERP.Platform.Observability\NickERP.Platform.Observability.csproj" />
```

## Local dev — OTLP collector in Docker

```bash
docker run -d --rm --name otelcol \
  -p 4317:4317 -p 4318:4318 \
  -v $(pwd)/otelcol.yaml:/etc/otelcol-contrib/config.yaml \
  otel/opentelemetry-collector-contrib
```

Six-line `otelcol.yaml` that just dumps to stdout:

```yaml
receivers:
  otlp: { protocols: { grpc: {}, http: {} } }
exporters:
  debug: { verbosity: detailed }
service:
  pipelines:
    traces:  { receivers: [otlp], exporters: [debug] }
    metrics: { receivers: [otlp], exporters: [debug] }
```

## `/metrics` endpoint

Mapped at `/metrics`. Loopback-only by default — requests from any non-loopback caller get a 404 (the route's existence is hidden). To widen, set:

```
NickERP:Observability:Prometheus:AllowedNetworks = 10.0.0.0/8,192.168.1.0/24
```

CIDR list, comma-separated. Loopback is always allowed regardless of this list.

## Importing the dashboard

`platform/NickERP.Platform.Observability/dashboards/finance.json` is a Grafana dashboard (schema 36+). In Grafana: **Dashboards -> New -> Import -> Upload JSON file**. When prompted, pick a Prometheus datasource for the `${DS_PROMETHEUS}` placeholder.

## Configuration reference

| Key | Default | Notes |
|---|---|---|
| `NickERP:Observability:OtlpEndpoint` | `http://localhost:4317` | OTLP gRPC endpoint for both traces and metrics. |
| `NickERP:Observability:Tracing:Enabled` | `true` | Set to `false` to disable trace export entirely. |
| `NickERP:Observability:Metrics:Enabled` | `true` | Set to `false` to disable metric export entirely. |
| `NickERP:Observability:Prometheus:AllowedNetworks` | _(empty)_ | CIDR list. Loopback is always allowed. |
| `NickERP:Logging:Directory` | `C:\Logs\NickERP\{appName}` | File sink directory. Created on startup; falls back to console-only on IO error. |

Built-in resource attributes: `service.name`, `service.version`, `nickerp.app.name`, `nickerp.app.version`. Built-in log enrichers: machine name, process id/name, environment, thread id, plus `AppName`, `AppVersion`, and per-request `CorrelationId`.

## Activate in another app

In your app's `Program.cs`, add the two `builder.*` calls right after `WebApplication.CreateBuilder(args)`, the two `app.*` calls right before `app.Run()`, and add the project reference. Done — the same OTLP exporter, the same log shape, the same `/metrics` route, the same correlation behaviour as NickFinance.

## Package note

Uses `OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.15.3-beta.1 — the package has not yet shipped a stable 1.x. Other OpenTelemetry packages are pinned at 1.15.x stable. If you see a vulnerability advisory after a future restore, bump the affected package and re-test.
