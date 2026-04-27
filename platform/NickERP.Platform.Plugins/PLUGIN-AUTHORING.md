# Authoring a NickERP Platform Plugin

> Status: A.4 shipped (attribute, manifest, loader, registry, extensions). A second consumer (Inspection v2 scanner adapters) will exercise the contract more completely; this doc is the v0 reference.

---

## What a plugin is

A plugin is a class in a sibling assembly that:

1. Implements one or more **contract interfaces** that a module exposes (e.g. `IScannerAdapter`, `IExternalSystemAdapter`, `IAuthorityRulesProvider`).
2. Carries a `[Plugin("type-code")]` attribute on the class declaration.
3. Ships with a `plugin.json` sidecar file describing the type code, version, contracts, and an optional JSON-Schema for instance-level config.
4. Lives as a `*.dll` + `plugin.json` pair under the host's plugins directory at deploy time.

Plugins NEVER reference the consuming module's runtime code. They reference the `*.Abstractions` package that defines the contract — and `NickERP.Platform.Plugins` for the `[Plugin]` attribute.

---

## Minimum example

`MyScanner.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NickERP.Platform.Plugins" Version="0.1.0" />
    <ProjectReference Include="..\NickERP.Inspection.Scanners.Abstractions\NickERP.Inspection.Scanners.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

`MyScanner.cs`:

```csharp
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.Scanners.MyVendor;

[Plugin("my-vendor")]
public sealed class MyScannerAdapter : IScannerAdapter
{
    public string TypeCode => "my-vendor";
    public ScannerCapabilities Capabilities => new(...);
    // ... rest of IScannerAdapter
}
```

`plugin.json` (next to the built `MyScanner.dll` after publish):

```json
{
  "typeCode": "my-vendor",
  "displayName": "My Vendor X-Ray Scanner",
  "version": "1.0.0",
  "description": "Scanner adapter for My Vendor's X-Ray product line.",
  "contracts": [
    "NickERP.Inspection.Scanners.Abstractions.IScannerAdapter"
  ],
  "configSchema": {
    "$schema": "http://json-schema.org/draft-07/schema#",
    "type": "object",
    "required": ["host", "port"],
    "properties": {
      "host": { "type": "string" },
      "port": { "type": "integer", "minimum": 1, "maximum": 65535 }
    }
  }
}
```

---

## Discovery

The host registers plugins at startup with one of two extension methods:

```csharp
// Option A: lazy — registry built on first resolve, types not pre-registered.
services.AddNickErpPlugins("C:\\path\\to\\plugins");

// Option B: eager — load + register types now (preferred for prod deployments).
services.AddNickErpPluginsEager(
    "C:\\path\\to\\plugins",
    loggerFactoryForEagerLoad: builder.Logging.Services
        .BuildServiceProvider()
        .GetRequiredService<ILoggerFactory>());
```

The loader scans `*.dll` files in the directory; for each it looks for either:

- A typed manifest sidecar `<dllname>.plugin.json`, OR
- A generic `plugin.json` in the same directory (single-plugin folders).

It loads the assembly, scans for any class decorated with `[Plugin("...")]`, and matches the attribute's TypeCode against the manifest. Mismatches and duplicate TypeCodes are logged at Error level and skipped — the rest of the plugins still load.

---

## Registry usage

Modules query the registry directly:

```csharp
public class ScannerOnboardingService
{
    private readonly IPluginRegistry _plugins;

    public ScannerOnboardingService(IPluginRegistry plugins) => _plugins = plugins;

    public IEnumerable<PluginManifest> ListAvailableScanners() =>
        _plugins.ForContract(typeof(IScannerAdapter)).Select(p => p.Manifest);

    public IScannerAdapter GetAdapter(string typeCode, IServiceProvider services) =>
        _plugins.Resolve<IScannerAdapter>(typeCode, services);
}
```

`Resolve<T>` throws `KeyNotFoundException` if the TypeCode is unknown and `InvalidOperationException` if the plugin doesn't implement `T`. Both are programming errors at boot — the registry is built once at startup and resolves are deterministic from then on.

---

## Contract versioning (`minHostContractVersion`)

Each Abstractions assembly is stamped with a `[ContractVersion("major.minor")]` attribute (via an MSBuild `<AssemblyAttribute>` ItemGroup in the Abstractions csproj). Plugin manifests declare the **minimum** host contract version they require:

```json
{
  "typeCode": "my-vendor",
  "contracts": [
    "NickERP.Inspection.Scanners.Abstractions.IScannerAdapter"
  ],
  "minHostContractVersion": "1.0",
  ...
}
```

At startup the loader resolves each contract type, reads its assembly's `[ContractVersion]`, and rejects the plugin (with a clear error log line — `"Plugin '<TypeCode>' requires <Contract>@<Min> but host has @<Host>; skipping."`) if the host's version is older than the plugin's declared minimum. Other plugins continue to load.

**Semver-lite rules.**

- Bump **minor** on additive changes (new optional record field, new method on an interface that has a default implementation, new value in an enum that adapters don't switch on).
- Bump **major** on breaking changes (record arity change without a default, removed/renamed members, repurposed semantics).

The loader compares as `host >= min`, matching NuGet's lower-bound contract — a host at `1.5` accepts plugins requiring any value in `1.0`–`1.5`. A plugin built against `1.5` that depends on a member added in `1.5` declares `"minHostContractVersion": "1.5"` and won't load against an older host.

**Backwards-compatibility note.** Plugins shipped before the contract-version layer existed simply omit `minHostContractVersion`; the loader treats that as "any host version" and registers the plugin without a version check. Abstractions assemblies that haven't been stamped yet fall back to `1.0` with a one-line warning. New plugins should always declare a minimum so drift surfaces at startup, not at first call.

## What the loader DOES NOT do (yet)

- **No JSON-Schema validation.** The `configSchema` is captured but not enforced at registration. Admin UI uses it to render forms; the loader trusts the manifest.
- **No assembly isolation.** Plugins load into the default `AssemblyLoadContext` — they share the host's dependency graph. If two plugins want different versions of the same package, the host wins. Hot-reload is not supported.
- **No signature checks.** Plugins are trusted code shipped with the host (in-house only per the v2 architectural decisions). External / third-party plugins are out of scope and would need additional gating.

---

## Roadmap reference

This package implements **Track A.4** of `ROADMAP.md`. Adjacent layers:

- A.4.1 ✅ `[Plugin]` + `PluginManifest`
- A.4.2 ✅ `PluginLoader` (file + assembly modes)
- A.4.3 ✅ `IPluginRegistry` + `PluginRegistry` impl
- A.4.4 mock plugin demo at `platform/demos/plugins/MockEcho/`
- A.4.5 ✅ this doc

---

## Related

- `IDENTITY.md`, `TENANCY.md` — sibling platform layer contracts.
- v2 inspection `ARCHITECTURE.md` §6 — three concrete plugin contracts that build on this layer (`IScannerAdapter`, `IExternalSystemAdapter`, `IAuthorityRulesProvider`).
