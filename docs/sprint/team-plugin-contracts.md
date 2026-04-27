# Team PC — Plugin Contract Versioning

## Mission

Detect plugin contract drift at host startup, not at first call. Add a `[ContractVersion]` attribute to every Abstractions assembly; have plugin manifests declare a `minHostContractVersion`; have the plugin loader refuse to register plugins whose declared minimum exceeds the host's current contract version, with a clear log message.

## Why this matters

In this session we changed `IAuthorityRulesProvider.InspectionCaseData` from a 5-arg record to a 6-arg record (added `IReadOnlyList<ScanSnapshot> Scans`). Plugins built against the old shape would `MissingMethodException` at first call. Today **nothing** prevents that — `PluginLoader` calls `Assembly.LoadFrom` and registers anything decorated with `[Plugin]`, regardless of which contract version it was compiled against. Production deploys with rolling plugin updates will hit this immediately.

## Current state

- `platform/NickERP.Platform.Plugins/PluginLoader.cs` discovers plugin DLLs, reads `plugin.json` next to each, and registers via `IPluginRegistry`. No version check.
- `platform/NickERP.Platform.Plugins/PluginManifest.cs` is a record with `TypeCode`, `DisplayName`, `Version`, `Description`, `Contracts[]`, `ConfigSchema`. No host-version field.
- `platform/NickERP.Platform.Plugins/PluginAttribute.cs` carries `TypeCode`. No version.
- Three Abstractions assemblies exist and have stable `1.0.x` package versions:
  - `modules/inspection/src/NickERP.Inspection.Scanners.Abstractions/`
  - `modules/inspection/src/NickERP.Inspection.ExternalSystems.Abstractions/`
  - `modules/inspection/src/NickERP.Inspection.Authorities.Abstractions/`
- Five plugin.json files exist:
  - `modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/plugin.json`
  - `modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000/plugin.json`
  - `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/plugin.json`
  - `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/plugin.json`
  - `modules/inspection/plugins/NickERP.Inspection.Authorities.CustomsGh/plugin.json`

## Deliverables

### 1. `[ContractVersion]` attribute

New file `platform/NickERP.Platform.Plugins/ContractVersionAttribute.cs`:

```csharp
namespace NickERP.Platform.Plugins;

/// <summary>
/// Marks an Abstractions assembly with a semver-shaped contract version.
/// The plugin loader compares each plugin's declared minHostContractVersion
/// against this value and refuses to load mismatched plugins at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class ContractVersionAttribute : Attribute
{
    public ContractVersionAttribute(string version)
    {
        if (!Version.TryParse(version, out var parsed))
            throw new ArgumentException($"Invalid contract version '{version}'. Use major.minor (e.g. '1.0').", nameof(version));
        Version = parsed;
    }

    public Version Version { get; }
}
```

Stamp every Abstractions assembly. The cleanest mechanism is via the csproj's `AssemblyAttribute`:

```xml
<ItemGroup>
  <AssemblyAttribute Include="NickERP.Platform.Plugins.ContractVersionAttribute">
    <_Parameter1>1.0</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

Add to:
- `modules/inspection/src/NickERP.Inspection.Scanners.Abstractions/NickERP.Inspection.Scanners.Abstractions.csproj`
- `modules/inspection/src/NickERP.Inspection.ExternalSystems.Abstractions/NickERP.Inspection.ExternalSystems.Abstractions.csproj`
- `modules/inspection/src/NickERP.Inspection.Authorities.Abstractions/NickERP.Inspection.Authorities.Abstractions.csproj` — bump this one to `1.1` because the `Scans` field was added mid-session.

### 2. `minHostContractVersion` in `PluginManifest`

Modify `platform/NickERP.Platform.Plugins/PluginManifest.cs` to add:

```csharp
/// <summary>
/// Minimum host Abstractions assembly contract version required. Format: "major.minor".
/// Required for any plugin that depends on a non-1.0 contract.
/// </summary>
[JsonPropertyName("minHostContractVersion")]
public string? MinHostContractVersion { get; init; }
```

### 3. Loader rejection

Modify `platform/NickERP.Platform.Plugins/PluginLoader.cs` to:

1. After loading a plugin assembly, for each `Contracts[]` entry in its manifest:
   - Resolve the contract type (via `Type.GetType(string fullName)`).
   - Get its assembly's `ContractVersionAttribute`. If the attribute is missing, log a warning and skip the version check (legacy compatibility).
   - If the manifest declares `minHostContractVersion`:
     - Parse it; if unparseable, log error + skip plugin.
     - If the host's contract version is `< min`, log a clear error and skip plugin.
2. Log a single line per loaded plugin including the contract version pair: `Loaded plugin 'fs6000' v0.1.0 against IScannerAdapter@1.0`.

Pseudocode for the check:

```csharp
foreach (var contractTypeName in manifest.Contracts)
{
    var contractType = ResolveContractType(contractTypeName);
    if (contractType is null) { /* error + skip */ continue; }

    var hostVersion = contractType.Assembly
        .GetCustomAttribute<ContractVersionAttribute>()?.Version
        ?? new Version(1, 0);

    if (manifest.MinHostContractVersion is { } minStr)
    {
        if (!Version.TryParse(minStr, out var min)) { /* error + skip */ continue; }
        if (hostVersion < min)
        {
            _logger.LogError(
                "Plugin '{TypeCode}' requires {Contract}@{Min} but host has @{Host}; skipping.",
                manifest.TypeCode, contractType.Name, min, hostVersion);
            continue;
        }
    }
}
```

### 4. Update existing manifests to declare their minimums

For each plugin.json, add `"minHostContractVersion": "1.0"` (or `"1.1"` for `gh-customs` which uses the new `ScanSnapshot` field):

- `modules/inspection/plugins/NickERP.Inspection.Scanners.Mock/plugin.json` → `"1.0"`
- `modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000/plugin.json` → `"1.0"`
- `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.Mock/plugin.json` → `"1.0"`
- `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/plugin.json` → `"1.0"`
- `modules/inspection/plugins/NickERP.Inspection.Authorities.CustomsGh/plugin.json` → `"1.1"` (uses `ScanSnapshot` added this session)

### 5. Update PLUGIN-AUTHORING.md

If `platform/NickERP.Platform.Plugins/PLUGIN-AUTHORING.md` exists, add a section explaining `minHostContractVersion` and how to bump contract versions.

## Acceptance criteria

1. **Build green** — repo-wide `dotnet build` returns 0 errors, 0 new warnings.

2. **All five plugins still load** — restart Inspection v2, verify `/plugins` returns 5 plugins as today; check the host log for the new "Loaded plugin '{code}' against {Contract}@{version}" line on each.

3. **Drift detection works** — hand-edit one plugin.json (e.g., the FS6000 one) to set `"minHostContractVersion": "99.0"`. Restart the host. Expected: that plugin is **not** registered (verify `/plugins` shows 4, not 5); host log contains `requires IScannerAdapter@99.0 but host has @1.0; skipping.`. Other plugins still load.

4. **Missing attribute is graceful** — temporarily remove the `<AssemblyAttribute>` from one Abstractions csproj and rebuild. Plugins targeting that contract should load with a warning ("Contract X has no ContractVersion attribute; assuming 1.0"), not crash. Restore after testing.

5. **Bumped contract works** — `gh-customs` declares `1.1`, `Authorities.Abstractions` is at `1.1` → loads successfully.

## Out of scope

- Don't add tests — Team TF handles that. (You can drop a comment in PluginLoader pointing TF at the test cases this enables: drift-detection happy path, drift-detection rejection, missing-attribute graceful degradation.)
- Don't add `TenantId` to plugin configs — that's Team PT.
- Don't change the `[Plugin]` attribute itself — leave `TypeCode` alone.

## Dependencies

- **Inbound:** none.
- **Outbound:** none directly. Team PT will add a `TenantId` field to `ScannerDeviceConfig` / `ExternalSystemConfig` (which would be a contract bump from 1.0 → 1.1 on those Abstractions); coordinate so PT bumps the contract version when it merges.

## Notes / gotchas

- **Contract version semantics.** Use semver-lite: bump major on breaking change (record arity, removed field, renamed type), bump minor on additive change (new optional field). The loader compares as `host >= min`, so a host at `1.5` accepts plugins requiring `1.0`-`1.5`. This matches NuGet's lower-bound contract.
- **Strong-naming is out of scope.** A future hardening step would sign Abstractions assemblies and verify identity at load time. For now, version comparison is the entire contract.
- **Embed the host version in `IPluginRegistry.All`** so admin UI can show it. Optional but nice.

## Commit message convention

```
feat(platform): plugin contract version pinning (Sprint PC)

Plugins now declare minHostContractVersion in plugin.json; the loader
rejects plugins whose minimum exceeds the host's
[ContractVersion]-stamped Abstractions version. Catches stale-DLL
MissingMethodException at startup instead of first call.

- New ContractVersionAttribute on every Abstractions assembly
- New MinHostContractVersion field on PluginManifest
- PluginLoader compares + skips on mismatch with a clear error log

Verified: hand-edited a plugin.json to require 99.0; restart shows
that plugin skipped with the expected error and other plugins still
load. Authorities.Abstractions bumped to 1.1 (added ScanSnapshot
this session); gh-customs.plugin.json declares minHostContractVersion
"1.1" and loads successfully.

Co-Authored-By: Claude (Sprint Team PC)
```
