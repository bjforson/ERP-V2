# Runbook 04 — Plugin DLL fails to load on host start

> **Scope.** A plugin in `{ContentRoot}/plugins/` failed to register
> on host startup. ERP V2's plugin loader is permissive — a single
> failed plugin is logged and skipped; the host stays up. This runbook
> covers the four shapes of failure the loader emits and the
> rollback / patch-forward paths.
>
> **Sister docs:**
> [`platform/NickERP.Platform.Plugins/PLUGIN-AUTHORING.md`](../../platform/NickERP.Platform.Plugins/PLUGIN-AUTHORING.md)
> for the contract author's view; [`03-prerender-stalled.md`](03-prerender-stalled.md)
> if the plugin failure is upstream of an empty backlog.

---

## 1. Symptom

Any of:

- **Host is up, `/healthz/ready` reports `plugin-registry` Unhealthy.**
  Body: `No plugins loaded — check the host's plugins/ folder.`
- **Host is up, `plugin-registry` Healthy with an unexpectedly low
  count.** E.g. `3 plugin(s) loaded` when you shipped 4. The check
  doesn't enforce a per-plugin contract — count only.
- **Functionality silently missing.** Admin UI no longer offers a
  particular adapter type; cases of a particular kind stop being
  ingestible.
- **Log line at startup, one of:**
  - `Failed to load plugin manifest at <path>; skipping.` — JSON parse
    error, missing required field.
  - `Failed to load plugin assembly <dll>; skipping.` — assembly
    couldn't load (missing transitive dep, wrong runtime, signed/unsigned
    mismatch).
  - `Plugin manifest <path> declares (Module=..., TypeCode=...) but
    no matching [Plugin(...)]-decorated class was found in <asm>.`
    — manifest and assembly disagree.
  - `Plugin '<typeCode>' requires <Contract>@<min> but host has
    @<host>; skipping.` — contract version pin (F3 / Sprint 1) said
    the host is too old for this plugin.
  - `Duplicate plugin (Module=..., TypeCode=...) (manifest <path>);
    skipping. The first registration wins.` — two plugins claim the
    same `(module, typeCode)`.

## 2. Severity

| Trigger | Severity | Response window |
|---|---|---|
| `plugin-registry` Unhealthy (0 plugins) | P1 | inside 30 min — host is functionally dead |
| One known plugin missing, others fine | P2 | inside 4 h — narrow capability gap |
| Contract version mismatch on a plugin we don't deploy | P3 | log, ignore; clean up next deploy |
| Duplicate plugin warning | P2 | inside 4 h — risks shipping the wrong DLL |

The host stays up by design (G1 / Sprint 1 — "fail gracefully when a
plugin fails"). That's a feature, not a bug — it lets a marginal
plugin fail in isolation rather than knocking down the whole host.
But you must close the loop on **why** the plugin failed; "host is
green-ish" is not "the world is fine."

## 3. Quick triage (60 seconds)

```bash
# Are any plugins loaded?
curl -s http://127.0.0.1:5410/healthz/ready | jq '.checks[] | select(.name=="plugin-registry")'
```

Output shape:

```json
{
  "name": "plugin-registry",
  "status": "Healthy",
  "description": "5 plugin(s) loaded",
  "error": null
}
```

If `description` is `0 plugin(s) loaded` or status is `Unhealthy`,
something catastrophically broke the discovery — probably the entire
`plugins/` folder is missing or empty. Otherwise count which plugins
*should* be there:

```bash
ls modules/inspection/src/NickERP.Inspection.Web/plugins/*.dll 2>&1 | wc -l
ls modules/inspection/src/NickERP.Inspection.Web/plugins/*.json 2>&1 | wc -l
# Or for a published artifact:
ls publish/inspection-web/plugins/*.dll 2>&1 | wc -l
```

Each `.dll` should pair with a `.json` (or `<dllname>.plugin.json`).
Mismatched counts = manifest / DLL drift.

## 4. Diagnostic commands

### 4.1 List what the loader saw

The plugin loader emits a registration log at startup for every
*loaded* plugin:

```
info: NickERP.Platform.Plugins.PluginLoader[0]
      Loaded plugin (inspection, icums-gh) (ICUMS (Ghana Customs), v0.1.0)
      from .../plugins/NickERP.Inspection.ExternalSystems.IcumsGh.dll;
      implements 1 contract(s).
```

…and an Error / Warning per skipped plugin. Tail Seq filtered to
`SourceContext = NickERP.Platform.Plugins.PluginLoader` to see both:

```
# Equivalent — read the host's startup log file:
tail -200 "C:/Shared/Logs/NickERP.Inspection.Web-$(date +%Y%m%d).log" \
  | grep -i 'PluginLoader'
```

### 4.2 Inspect the plugins folder

```bash
PLUGINS_DIR="C:/Shared/erp-v2-p1/modules/inspection/src/NickERP.Inspection.Web/plugins"
ls -la "$PLUGINS_DIR" 2>&1
# Or for a published deploy artifact:
ls -la "C:/Shared/erp-v2-p1/publish/inspection-web/plugins"
```

For each `*.dll`, confirm three things:

```bash
DLL_NAME="NickERP.Inspection.ExternalSystems.IcumsGh"

# 1. The DLL itself.
ls "$PLUGINS_DIR/$DLL_NAME.dll"

# 2. A sidecar manifest. Either DllName.plugin.json OR plugin.json.
ls "$PLUGINS_DIR/$DLL_NAME.plugin.json" 2>&1 \
  || ls "$PLUGINS_DIR/plugin.json"

# 3. Read the manifest.
cat "$PLUGINS_DIR/$DLL_NAME.plugin.json" 2>&1 \
  || cat "$PLUGINS_DIR/plugin.json"
```

Required manifest fields (per `PluginManifest.LoadFrom` validation):

- `module` (e.g. `"inspection"`)
- `typeCode` (e.g. `"icums-gh"`)
- `displayName`
- `version`

Optional but commonly relevant:

- `minHostContractVersion` — the contract pin from F3 / Sprint 1.
- `contracts` — fully-qualified interface names this plugin
  implements (used by the version check).

### 4.3 Check the host's contract version

```bash
# What contract version does the host's Abstractions assembly stamp?
strings publish/inspection-web/NickERP.Inspection.ExternalSystems.Abstractions.dll \
  2>/dev/null | grep -A1 ContractVersion | head -5
```

The `[assembly: ContractVersion("X.Y")]` attribute is set in the
Abstractions project's `csproj`. Today's value (Sprint 7) is
`1.1` — a plugin pinning `minHostContractVersion: "1.2"` will be
rejected.

### 4.4 Check for duplicate `(module, typeCode)` keys

```bash
# Pull (module, typeCode) from every manifest in the plugins folder.
for j in "$PLUGINS_DIR"/*.json; do
  echo -n "$(basename "$j"): "
  jq -r '.module + "/" + .typeCode' "$j" 2>&1
done | sort -t: -k2
```

Two manifests with the same `module/typeCode` is the duplicate
case — the loader keeps the first one it sees and logs an Error for
the rest. File order is filesystem-enumeration order (not stable
across deploys), so a duplicate today might silently succeed
tomorrow with a different "winner."

## 5. Resolution

### 5.1 Manifest is malformed

Symptom: `Failed to load plugin manifest at <path>; skipping.` or
`<path>: TypeCode is required.` (etc.) on startup.

```bash
# Validate JSON shape.
jq . "$PLUGINS_DIR/<bad-manifest>.json"
```

`jq` exits 0 on valid JSON. Missing required fields will not show as
JSON errors — those are caught by `PluginManifest.LoadFrom`.

Fix: edit the manifest, add the missing field, restart the host.

If the manifest came from a build, fix the source under
`modules/inspection/plugins/<name>/plugin.json` and re-publish — the
deploy artifact picks it up via `<None Update="plugin.json">` in the
csproj, and the publish step copies it next to the DLL.

### 5.2 Assembly fails to load

Symptom: `Failed to load plugin assembly <dll>; skipping.` —
inner exception is `BadImageFormatException` (wrong .NET runtime),
`FileLoadException` (missing transitive dep), or
`FileNotFoundException` (transitive dep DLL not deployed).

```bash
# Inspect the DLL's runtime target.
powershell.exe -Command \
  "(Get-Item '$PLUGINS_DIR/NickERP.Inspection.ExternalSystems.IcumsGh.dll').VersionInfo | Format-List FileVersion, ProductVersion"

# Confirm transitive deps. Most plugins have a .deps.json sibling
# that lists every dependency; check it's there.
ls "$PLUGINS_DIR/<plugin-name>.deps.json"
```

Resolution:

- **Wrong runtime.** Re-publish against the host's TFM (today: net10.0).
- **Missing dep.** The publish step normally bundles every dep.
  If a `dotnet publish ... -o <dir>` was used to stage, check the
  output dir is correct. Some plugins also ship native DLLs (e.g.
  `runtimes/win-x64/native`) — confirm those copied across.
- **Mixed signed/unsigned.** This bites strong-named assemblies; v2
  doesn't strong-name its plugins, so a "signed/unsigned mix-up"
  here is more likely a CI build versus local-dev build mix. Re-publish
  cleanly.

### 5.3 Manifest claims a class the assembly doesn't have

Symptom: `Plugin manifest <path> declares (Module='...', TypeCode='...')
but no matching [Plugin(...)]-decorated class was found in <asm>.`

The manifest's `(module, typeCode)` must match a class in the
assembly decorated with `[Plugin(typeCode, Module=module)]`.

```bash
# What [Plugin] attributes does the assembly actually carry?
# Quick-and-dirty: grep the strings table.
strings "$PLUGINS_DIR/<plugin-name>.dll" 2>/dev/null \
  | grep -i 'PluginAttribute\|TypeCode' | head -20
```

For a real check, decompile with ildasm / dotPeek / ICSharpCode. Or
read the source under `modules/<module>/plugins/<plugin>/`:

```bash
grep -rn '\[Plugin(' "modules/inspection/plugins/<plugin-name>"
```

Resolution: edit either the manifest or the `[Plugin(...)]` attribute
to match. Re-publish, restart.

### 5.4 Contract version mismatch

Symptom: `Plugin '<typeCode>' requires <Contract>@<min> but host has
@<host>; skipping.`

This is the F3 / Sprint 1 contract pin doing its job — the plugin
declared it needs a newer host than the one running.

Two paths:

- **Upgrade the host.** If the plugin is correct (the contract did
  bump), shipping a build with the matching host version is the only
  fix. Roll forward via [`01-deploy.md`](01-deploy.md).
- **Roll back the plugin.** Replace the offending DLL + manifest with
  the previous version (the one that worked against this host's
  contract version). This is the on-call "stop the bleeding" path:
  ```bash
  # Snapshot what's there.
  cp "$PLUGINS_DIR/<plugin>.dll" "$PLUGINS_DIR/<plugin>.dll.bak"
  cp "$PLUGINS_DIR/<plugin>.plugin.json" "$PLUGINS_DIR/<plugin>.plugin.json.bak"

  # Restore from a known-good earlier build artifact.
  cp /path/to/last-good/<plugin>.dll        "$PLUGINS_DIR/<plugin>.dll"
  cp /path/to/last-good/<plugin>.plugin.json "$PLUGINS_DIR/<plugin>.plugin.json"

  # Restart the host.
  ```
  After the rollback, file the upgrade follow-up — leaving the host
  pinned to the old plugin indefinitely is technical debt.

### 5.5 Duplicate plugin

Symptom: `Duplicate plugin (Module='...', TypeCode='...') (manifest
<path>); skipping. The first registration wins.`

Two manifests claim the same `(module, typeCode)`. Almost always:
"old DLL not cleaned up after a rename" or "manifest copied wholesale
from another plugin and `typeCode` not bumped."

```bash
# Identify the duplicates.
# (Re-use §4.4's command.)

# Delete the wrong one. If unsure which is "right," check git
# history for the most recent intended typeCode bump.
rm "$PLUGINS_DIR/<duplicate-name>.dll"
rm "$PLUGINS_DIR/<duplicate-name>.plugin.json"
```

Restart the host, confirm the count is right, file a follow-up if
the duplicate came from a build script that should have caught it.

### 5.6 Empty `plugins/` folder

Symptom: `plugin-registry` Unhealthy, `0 plugin(s) loaded`.

Most likely a deploy step skipped the plugin staging.

```bash
# Find the source-tree plugins.
find modules -name '*.csproj' -path '*/plugins/*' 2>&1

# Re-publish each into the host's plugins/ dir. The csproj output
# already targets that path via <Target>; if not, robocopy:
for p in modules/inspection/plugins/*/; do
  proj_name=$(basename "$p")
  dotnet publish "$p$proj_name.csproj" -c Release \
    -o "modules/inspection/src/NickERP.Inspection.Web/plugins" \
    --nologo
done
```

Restart, recheck `/healthz/ready`.

### 5.7 Restore minimal-privilege state

Plugins themselves don't bring elevated access — but a deploy that
swapped in an unsigned DLL from "trust me" sources would. Confirm
before moving on:

- The plugin DLLs in `$PLUGINS_DIR` came from the canonical build
  pipeline (CI artifact, not a hand-built local DLL).
- No plugin is granted host-process privileges beyond what
  `Microsoft.Extensions.DependencyInjection` ctor injection provides.
  Plugins do not get a `BYPASSRLS` connection or a postgres-superuser
  credential — they share the host's `nscim_app` connection.
- If the plugin ships a config schema that requires writing to a
  filesystem path (e.g. ICUMS adapter's `BatchDropPath` /
  `OutboxPath`), the path must be inside the host's accessible
  storage tree, not somewhere with broader rights. Sample-check the
  config rows:
  ```bash
  psql -U postgres -d nickerp_inspection -c \
    'SELECT "Id", "TypeCode", "ConfigJson"::jsonb
     FROM inspection.external_system_instances LIMIT 5;'
  ```

## 6. Verification

After any §5 path:

1. `/healthz/ready` → `plugin-registry` Healthy, with the correct
   `N plugin(s) loaded` count.
2. The startup log shows a `Loaded plugin (...)` line for every
   plugin you expected, and **no** `skipping` lines.
3. Functional smoke: hit a feature that exercises the previously-failed
   plugin. For the ICUMS adapter, that's the External Systems admin
   page or a `SubmitAsync` call through a case. For a scanner adapter,
   drop a test scan into the watch folder and confirm the
   `ScannerIngestionWorker` picks it up (verifiable via a new
   `scan_artifacts` row appearing within the configured poll
   interval).
4. Audit log entry for the deploy / rollback (when the audit
   projection lands per `PLAN.md` Sprint 8 / P3).

## 7. Aftermath

### 7.1 Postmortem template

```
## Plugin load failure: <YYYY-MM-DD HH:MM>
- Plugin: <module/typeCode>
- Failure shape: malformed-manifest | bad-assembly | manifest-class-mismatch | contract-version | duplicate | empty-folder
- Detection: /healthz/ready Unhealthy | log alert | manual triage
- Fix path: rollback | patch-forward | re-publish
- Time-to-restore: <minutes from detection to next-Healthy probe>
- Was a host restart required? <yes / no>
- Followups filed: <CHANGELOG.md / open-issue links>
```

### 7.2 Who to notify

Single-engineer system today: capture in `CHANGELOG.md`. If a
contract-version mismatch was the cause, also update
`PLAN.md` / `ROADMAP.md` with the host-version bump that the plugin
was waiting on.

## 8. References

- `platform/NickERP.Platform.Plugins/PluginLoader.cs` — the loader,
  the canonical source for every log-line shape above.
- `platform/NickERP.Platform.Plugins/PluginManifest.cs` — manifest
  schema + required-field validation.
- `platform/NickERP.Platform.Plugins/ContractVersionAttribute.cs`
  — the `[ContractVersion("X.Y")]` attribute the loader compares
  against.
- [`platform/NickERP.Platform.Plugins/PLUGIN-AUTHORING.md`](../../platform/NickERP.Platform.Plugins/PLUGIN-AUTHORING.md)
  — author's guide; tells you how the manifest is supposed to be
  shaped.
- `modules/inspection/src/NickERP.Inspection.Web/HealthChecks/PluginRegistryHealthCheck.cs`
  — what `/healthz/ready` checks for "plugin-registry."
- [`03-prerender-stalled.md`](03-prerender-stalled.md) — sister
  runbook; if scanner plugins fail to load, no `ScanArtifact` rows
  arrive and the PreRender backlog goes to zero — symptom shape is
  similar but the cause is here.
- [`PLAN.md`](../../PLAN.md) §18 — Sprint 7 / P1 origin.
