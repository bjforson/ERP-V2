# NickERP.Platform.Logging

Standard NickERP Serilog wiring. One call at startup, every service logs the same way to the same places.

## Usage

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.UseNickErpLogging(serviceName: "NSCIM.API");
// ... rest of host setup
```

That's it. Logs flow to:

- **Seq** (primary) — `http://localhost:5341` on TEST-SERVER. Override with `NickErp:Logging:SeqUrl` in config.
- **Per-service rolling file** (fallback) — `C:\Shared\Logs\<serviceName>\log-YYYYMMDD.txt`, 14-day retention. Override root with `NickErp:Logging:FileRoot`.
- **Console** (dev) — pretty format with timestamp, level, service, correlation id.

## Enrichers added automatically

| Property | Source |
|---|---|
| `ServiceName` | the `serviceName` argument you passed |
| `MachineName` | hostname |
| `ProcessId` | OS PID |
| `ThreadId` | managed thread id |
| `CorrelationId` | `Activity.Current.RootId` ?? `Activity.Current.Id` ?? new GUID |

## Configuration overrides (`appsettings.json`)

```json
{
  "NickErp": {
    "Logging": {
      "SeqUrl": "http://localhost:5341",
      "SeqApiKey": "",
      "FileRoot": "C:\\Shared\\Logs",
      "MinimumLevel": "Information"
    }
  }
}
```

`MinimumLevel` accepts `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. Defaults to `Information`. Microsoft / ASP.NET Core / EF Core / System namespaces are clamped one level higher to keep noise down.

`SeqApiKey` is optional today (Seq install is single-tenant). Set per-service later when Seq is multi-tenanted.

## Querying in Seq

`http://localhost:5341` — log in as `admin`. Useful filter strings:

- `ServiceName = 'NSCIM.API'`
- `CorrelationId = 'xyz...'`
- `@Level >= 'Warning' AND ServiceName like 'NickHR%'`

## Roadmap reference

This package implements **Track A.1.2** of `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md`. Adjacent layers:

- A.1.1 Seq install (done — running on TEST-SERVER)
- A.1.3 `NickERP.Platform.Telemetry` — OpenTelemetry traces + metrics (next)
- A.1.4 Demo app exercising both
- A.1.5 Convention docs (this file is the seed)
