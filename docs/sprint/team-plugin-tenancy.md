# Team PT — Plugin Tenancy

## Mission

Make plugins tenant-aware. Add `long TenantId` to `ScannerDeviceConfig` and `ExternalSystemConfig`; have the host populate it from `ITenantContext`; partition any in-memory plugin state (especially `IcumsGhAdapter._indexes`) by tenant so two tenants pointing at the same physical resource don't share state.

## Why this matters

Today's plugin contracts:

```csharp
public sealed record ScannerDeviceConfig(Guid DeviceId, Guid LocationId, Guid? StationId, string ConfigJson);
public sealed record ExternalSystemConfig(Guid InstanceId, string ConfigJson);
```

…carry no tenant. `IcumsGhAdapter._indexes` is a `static ConcurrentDictionary<string, IcumBatchIndex>` keyed by `$"{instanceId}|{cfg.BatchDropPath}|{cfg.CacheTtlSeconds}"`. Two tenants in a shared deployment that point at the same physical drop folder would hit the same cache entry — the cache contains every container in that folder, with no tenant filter. The leak is gated by physical-folder-sharing today, but plugin-side state should be tenant-aware on principle and before any future plugin (Finance MoMo, HR comms gateway) holds money or PII in static caches.

`IAuthorityRulesProvider.InspectionCaseData` already carries `TenantId` — only the other two contracts need it.

## Current state

- `modules/inspection/src/NickERP.Inspection.Scanners.Abstractions/IScannerAdapter.cs` — `ScannerDeviceConfig` is the 4-arg record.
- `modules/inspection/src/NickERP.Inspection.ExternalSystems.Abstractions/IExternalSystemAdapter.cs` — `ExternalSystemConfig` is the 2-arg record.
- `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs` constructs both at three call sites:
  - `SimulateScanAsync` line ~118: `new ScannerDeviceConfig(device.Id, device.LocationId, device.StationId, device.ConfigJson)`
  - `FetchDocumentsAsync` line ~188: `new ExternalSystemConfig(instance.Id, instance.ConfigJson)`
  - `SubmitAsync` line ~349: `new ExternalSystemConfig(instance.Id, instance.ConfigJson)`
- `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/IcumsGhAdapter.cs` line ~40: `private static readonly ConcurrentDictionary<string, IcumBatchIndex> _indexes = new(...)` — keyed by instanceId|path|ttl.

## Deliverables

### 1. Add `TenantId` to plugin configs

Modify `IScannerAdapter.cs`:

```csharp
public sealed record ScannerDeviceConfig(
    Guid DeviceId,
    Guid LocationId,
    Guid? StationId,
    long TenantId,
    string ConfigJson);
```

Modify `IExternalSystemAdapter.cs`:

```csharp
public sealed record ExternalSystemConfig(
    Guid InstanceId,
    long TenantId,
    string ConfigJson);
```

Bump `[ContractVersion]` on both Abstractions assemblies from `1.0` to `1.1` (coordinate with Team PC). Bump every plugin.json that targets these contracts to `"minHostContractVersion": "1.1"`.

### 2. Host wires TenantId at all three call sites

In `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs`, update construction:

```csharp
// SimulateScanAsync
var config = new ScannerDeviceConfig(device.Id, device.LocationId, device.StationId, tenantId, device.ConfigJson);

// FetchDocumentsAsync
new ExternalSystemConfig(instance.Id, tenantId, instance.ConfigJson)

// SubmitAsync
new ExternalSystemConfig(instance.Id, tenantId, instance.ConfigJson)
```

Where `tenantId` comes from the existing `_tenant.TenantId` (Team TS removed the `SetTenant(1)` fallback, so `_tenant.IsResolved` is now load-bearing — your code reads `_tenant.TenantId` only after the existing `EnsureTenant` / actor resolution).

### 3. Partition `IcumsGhAdapter._indexes` by tenant

In `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/IcumsGhAdapter.cs`, change the key:

Before:
```csharp
var key = $"{instanceId}|{cfg.BatchDropPath}|{cfg.CacheTtlSeconds}";
```

After:
```csharp
var key = $"{config.TenantId}|{instanceId}|{cfg.BatchDropPath}|{cfg.CacheTtlSeconds}";
```

(Read `config.TenantId` from the new field.) Document the change in the class XML doc — the static cache is now tenant-scoped.

### 4. Update mock plugins + other adapters

Mock plugins must accept the new contract shape:

- `modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/MockScannerAdapter.cs` — uses `config` only for `DeviceId` today; no change needed in body, just verify it still compiles against the new record arity.
- `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/MockExternalSystemAdapter.cs` — same.
- `modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000/FS6000ScannerAdapter.cs` — its `_seen` HashSet is per-instance via the static class field. Rebuild check: should it be partitioned by tenant too? Since FS6000 is keyed off file paths and a dropped file is a physical fact (one tenant per file), the answer is "not strictly necessary, but defensive." Add `TenantId` to the seen-stem signature: `_seen.Add($"{tenantId}|{stem}")`.
- `modules/inspection/plugins/NickERP.Inspection.Authorities.CustomsGh/` — already gets `TenantId` via `InspectionCaseData`; no change.

### 5. Plugin staging

Rebuild every plugin and re-stage their DLLs into the host's `plugins/` folder. (Team TS / PM may add `dotnet publish` automation for this, but for now it's manual — see `TESTING.md` for the cp commands.)

## Acceptance criteria

1. **Build green:** `dotnet build` from repo root → 0 errors. Every plugin and the host both compile.

2. **All plugins load against the new contract:** restart Inspection v2; `/plugins` shows all five plugins; host log confirms each loaded against `IScannerAdapter@1.1` / `IExternalSystemAdapter@1.1` / `IAuthorityRulesProvider@1.1`.

3. **End-to-end smoke unchanged:** the existing scan → fetch → verdict → submit flow works as before. (The TenantId field is added, not the behavior.)

4. **Cache partition test:** in a Postgres + host running setup, manually insert two tenants and two `external_system_instances` (one per tenant) both pointing at the same `BatchDropPath`. Drop a batch JSON containing container "ABC123". Set the dev-bypass header to a user in tenant 1, hit `/cases/{id}` and click Fetch documents — verify the BOE for "ABC123" appears for tenant 1's case. Switch dev-bypass to a tenant 2 user, repeat — same BOE appears for tenant 2's case. Inspect `_indexes` (via debugger or a temporary log line) — there should be **two** entries, one per tenant, each keyed by `"{tenantId}|{instanceId}|..."`.

5. **Old plugins fail to load:** if Team PC merged first, attempt to drop an FS6000 plugin built before this change into `plugins/`. Verify it's rejected with the version-mismatch error.

## Out of scope

- Don't add tests — Team TF.
- Don't migrate to a per-DI-scope plugin config — keep the `static` cache, just key it by tenant. Plugin lifecycle reform is its own work item.
- Don't change `IAuthorityRulesProvider` — already tenant-aware.

## Dependencies

- **Inbound:**
  - **Team TS** — `_tenant.IsResolved` now throws on unset, so the host resolution path must run before the worker invokes plugins. Coordinate timing.
  - **Team PC** — bumps to `[ContractVersion]` and `minHostContractVersion` should land in PC's commit so the version pinning catches mismatches; or in this team's commit if PC is delayed. Coordinate via commit messages.
- **Outbound:** Team DP's new `ScannerIngestionWorker` will construct `ScannerDeviceConfig` and must pass tenant; DP's brief assumes the field exists.

## Notes / gotchas

- **Static state lifetime.** `_indexes` is a `static` cache — it persists for the host process lifetime. Tenant churn (a tenant deleted, an instance reconfigured) doesn't evict; that's a known cost. Document it as a follow-up; not in this sprint.
- **Mock plugins are reference impls.** Make their bodies as simple as possible; they're load-bearing on plugin-pattern documentation.

## Commit message convention

```
feat(inspection): TenantId on plugin configs (Sprint PT)

Plugin contracts now carry the tenant identity. ScannerDeviceConfig
gained a long TenantId field; ExternalSystemConfig gained the same.
Both Abstractions assemblies bump [ContractVersion] from 1.0 to 1.1;
plugin.jsons declare minHostContractVersion="1.1".

IcumsGhAdapter's static _indexes cache is now keyed by
{tenantId}|{instanceId}|{path}|{ttl} — two tenants pointing at the
same physical drop folder get isolated cache entries. FS6000's _seen
stem signature is also tenant-prefixed defensively.

Co-Authored-By: Claude (Sprint Team PT)
```
