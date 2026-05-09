# ============================================================
# NickERP v2 — Cutover dry-run runner (Sprint 60 Phase A — C1)
# ============================================================
# Provisions a clean PostgreSQL 17 instance, creates the three
# canonical NickERP databases (`nickerp_platform`, `nickerp_inspection`,
# `nickerp_nickfinance`), applies every staged EF Core migration in
# dependency order via the `dotnet ef migrations script --idempotent
# | psql -f` pattern (FU-5 / `docs/MIGRATIONS.md` — sidesteps the
# `dotnet ef database update` Windows env-var quirk), runs the
# `PilotAcceptance` test suite against the provisioned target, and
# emits a markdown report under
# `tools/cutover-dryrun/reports/dryrun-{date}-migration-report.md`.
#
# This script implements `PLAN.md §23.2 work item A` — the engineering
# rehearsal that converts runbook 14 §4-§10 + the `live-deploy-staged-
# migrations` operator action from prose-with-shell-fragments into a
# tested step the operator can run on day 1 of the pilot stand-up.
#
# CANONICAL LAYOUT (six EF Core DbContexts, three Postgres databases):
#
#   nickerp_platform          schema identity     IdentityDbContext
#                             schema tenancy      TenancyDbContext
#                             schema audit        AuditDbContext
#                             schema queueing     QueueingDbContext
#   nickerp_inspection        schema inspection   InspectionDbContext
#   nickerp_nickfinance       schema nickfinance  NickFinanceDbContext
#
# Each context's `__EFMigrationsHistory` lives in its OWN schema (H3
# posture — `nscim_app` never needs CREATE on `public`).
#
# ============================================================
# Modes
# ============================================================
#   .\run.ps1                              # Container mode (Docker PG17 spin-up)
#   .\run.ps1 -TargetUri postgres://...    # Real-host mode (caller-managed PG17)
#   .\run.ps1 -Cleanup                     # Container mode + drop container at end
#   .\run.ps1 -SkipTests                   # Skip the dotnet test phase (migration-only run)
#
# In container mode the script starts `postgres:17` on port 55432
# (configurable via `-Port`), waits for readiness, provisions DB +
# `nscim_app` role, then proceeds. Default keeps the container alive
# for post-run inspection; pass `-Cleanup` to remove it.
#
# In real-host mode the caller passes a connection URI for a PG17
# admin (typically `postgres` superuser). The script does not start
# or stop any process — it only applies migrations. Use this mode
# against a staging HA pair per `PLAN.md §23.2` vendor-neutrality.
#
# ============================================================
# Exit codes
# ============================================================
#   0  — clean run; report written
#   1  — pre-flight failure (missing dotnet / psql / Docker)
#   2  — Postgres provision failure
#   3  — migration apply failure (report written; names failing migration)
#   4  — `dotnet test --filter PilotAcceptance` failure
#   5  — repo / project misconfiguration (e.g. expected DbContext path absent)
#
# ============================================================

[CmdletBinding()]
param(
    [string]$TargetUri = "",
    [int]$Port = 55432,
    [string]$ContainerName = "nickerp-cutover-dryrun-pg17",
    [string]$Image = "postgres:17",
    [string]$NscimPassword = "",
    [string]$AdminPassword = "",
    [switch]$Cleanup,
    [switch]$SkipTests,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ----- Repo root locator (this file lives in tools/cutover-dryrun/) -----------
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot  = (Resolve-Path (Join-Path $ScriptDir '..\..')).Path
$ReportsDir = Join-Path $ScriptDir 'reports'
if (-not (Test-Path $ReportsDir)) { New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null }

# ----- Canonical DbContext catalogue (PLAN.md §23.2 spec) ---------------------
# Order matters: platform DB first (4 contexts share it), then inspection,
# then nickfinance. Within nickerp_platform we apply identity → tenancy →
# audit → queueing (Identity is the legacy aspnet-style root so it goes
# first; the others have no cross-context FK references — H3 posture keeps
# every history table in its own schema).
$DbContexts = @(
    [pscustomobject]@{
        Name        = 'IdentityDbContext'
        Project     = 'platform/NickERP.Platform.Identity.Database'
        Database    = 'nickerp_platform'
        Schema      = 'identity'
    },
    [pscustomobject]@{
        Name        = 'TenancyDbContext'
        Project     = 'platform/NickERP.Platform.Tenancy.Database'
        Database    = 'nickerp_platform'
        Schema      = 'tenancy'
    },
    [pscustomobject]@{
        Name        = 'AuditDbContext'
        Project     = 'platform/NickERP.Platform.Audit.Database'
        Database    = 'nickerp_platform'
        Schema      = 'audit'
    },
    [pscustomobject]@{
        Name        = 'QueueingDbContext'
        Project     = 'platform/NickERP.Platform.Queueing.Database'
        Database    = 'nickerp_platform'
        Schema      = 'queueing'
    },
    [pscustomobject]@{
        Name        = 'InspectionDbContext'
        Project     = 'modules/inspection/src/NickERP.Inspection.Database'
        Database    = 'nickerp_inspection'
        Schema      = 'inspection'
    },
    [pscustomobject]@{
        Name        = 'NickFinanceDbContext'
        Project     = 'modules/nickfinance/src/NickERP.NickFinance.Database'
        Database    = 'nickerp_nickfinance'
        Schema      = 'nickfinance'
    }
)

$Databases = @('nickerp_platform', 'nickerp_inspection', 'nickerp_nickfinance')

# ============================================================
# Helpers
# ============================================================

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Info {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Gray
}

function Resolve-PsqlExe {
    # MIGRATIONS.md §worked-example uses `'/c/Program Files/PostgreSQL/18/bin/psql.exe'`.
    # Prefer the highest-version Windows install on the PATH. Fall back to
    # plain `psql` if it resolves (Linux / WSL / a chocolatey shim).
    $candidates = @(
        'C:/Program Files/PostgreSQL/18/bin/psql.exe',
        'C:/Program Files/PostgreSQL/17/bin/psql.exe',
        'C:/Program Files/PostgreSQL/16/bin/psql.exe',
        'C:/Program Files/PostgreSQL/15/bin/psql.exe'
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $cmd = Get-Command psql -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Test-Preflight {
    param([bool]$NeedsDocker)

    Write-Step "Pre-flight checks"

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Write-Error "dotnet CLI not on PATH"; return 1 }
    Write-Info "dotnet : $($dotnet.Source)"

    $efVersion = (& dotnet ef --version 2>&1 | Select-String -Pattern '^\s*\d+').ToString().Trim()
    if (-not $efVersion) { Write-Error "dotnet-ef tool not installed (run 'dotnet tool install -g dotnet-ef')"; return 1 }
    Write-Info "dotnet ef : $efVersion"

    $psql = Resolve-PsqlExe
    if (-not $psql) { Write-Error "psql.exe not found (looked in C:\Program Files\PostgreSQL\<ver>\bin\)"; return 1 }
    Write-Info "psql : $psql"
    $script:PsqlExe = $psql

    if ($NeedsDocker) {
        $docker = Get-Command docker -ErrorAction SilentlyContinue
        if (-not $docker) {
            Write-Error "Docker not on PATH — needed for container mode. Pass -TargetUri postgres://... for real-host mode."
            return 1
        }
        Write-Info "docker : $($docker.Source)"
    }

    return 0
}

function Start-PostgresContainer {
    param(
        [string]$Name,
        [string]$Image,
        [int]$Port,
        [string]$AdminPassword
    )
    Write-Step "Provisioning ephemeral PostgreSQL container"
    Write-Info "image=$Image  name=$Name  port=$Port"

    # Remove any leftover container with the same name.
    $existing = & docker ps -a --filter "name=^/${Name}$" --format '{{.ID}}' 2>$null
    if ($existing) {
        Write-Info "Removing stale container $Name ($existing)"
        & docker rm -f $Name 2>&1 | Out-Null
    }

    & docker run -d `
        --name $Name `
        -e POSTGRES_PASSWORD=$AdminPassword `
        -e POSTGRES_INITDB_ARGS="--encoding=UTF8 --locale=C" `
        -p "${Port}:5432" `
        $Image 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return 2 }

    # Wait for readiness — pg_isready inside the container.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        & docker exec $Name pg_isready -U postgres -h 127.0.0.1 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Info "container ready"
            return 0
        }
        Start-Sleep -Milliseconds 750
    }
    Write-Error "Postgres container $Name did not become ready within 60s"
    return 2
}

function Get-PostgresVersion {
    param([hashtable]$Env)
    $env:PGPASSWORD = $Env.AdminPassword
    try {
        $out = & $script:PsqlExe -h $Env.Host -p $Env.Port -U $Env.AdminUser -d postgres -tAc "SELECT version();" 2>&1
        return ($out | Out-String).Trim()
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

function Invoke-Psql {
    param(
        [hashtable]$Env,
        [string]$Database,
        [string]$Sql,
        [string]$User = ""
    )
    if ([string]::IsNullOrEmpty($User)) { $User = $Env.AdminUser }
    $env:PGPASSWORD = if ($User -eq $Env.AdminUser) { $Env.AdminPassword } else { $Env.NscimPassword }
    try {
        $out = & $script:PsqlExe -h $Env.Host -p $Env.Port -U $User -d $Database -v ON_ERROR_STOP=1 -tAc $Sql 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = ($out | Out-String).Trim() }
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

function Invoke-PsqlFile {
    param(
        [hashtable]$Env,
        [string]$Database,
        [string]$File,
        [string]$User = ""
    )
    if ([string]::IsNullOrEmpty($User)) { $User = $Env.AdminUser }
    $env:PGPASSWORD = if ($User -eq $Env.AdminUser) { $Env.AdminPassword } else { $Env.NscimPassword }
    try {
        $out = & $script:PsqlExe -h $Env.Host -p $Env.Port -U $User -d $Database -v ON_ERROR_STOP=1 -f $File 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = ($out | Out-String).Trim() }
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

function Initialize-Databases {
    param([hashtable]$Env)
    Write-Step "Creating databases + nscim_app role"

    # MIGRATIONS.md §NickFinance-prerequisites: CREATE DATABASE owned by
    # postgres so the EF migration's grants stanza can ALTER DEFAULT
    # PRIVILEGES. nscim_app is created LOGIN NOSUPERUSER NOBYPASSRLS so
    # RLS policies actually enforce against it — runbook 11 / SEC-RLS-1.
    foreach ($db in $Databases) {
        $exists = Invoke-Psql -Env $Env -Database 'postgres' -Sql "SELECT 1 FROM pg_database WHERE datname = '$db';"
        if ($exists.ExitCode -ne 0) {
            Write-Error "Probe of pg_database for '$db' failed: $($exists.Output)"
            return 2
        }
        if ([string]::IsNullOrEmpty($exists.Output)) {
            Write-Info "CREATE DATABASE $db OWNER postgres"
            $r = Invoke-Psql -Env $Env -Database 'postgres' -Sql "CREATE DATABASE `"$db`" OWNER postgres;"
            if ($r.ExitCode -ne 0) { Write-Error "CREATE DATABASE $db failed: $($r.Output)"; return 2 }
        } else {
            Write-Info "$db exists (idempotent skip)"
        }
    }

    # nscim_app role — DO block keeps it idempotent. Real-host mode may
    # already have a long-lived nscim_app; we only CREATE if absent.
    $createRole = @"
DO `$`$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN
    CREATE ROLE nscim_app WITH LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '$($Env.NscimPassword)';
  END IF;
END `$`$;
"@
    $r = Invoke-Psql -Env $Env -Database 'postgres' -Sql $createRole
    if ($r.ExitCode -ne 0) { Write-Error "CREATE ROLE nscim_app failed: $($r.Output)"; return 2 }
    Write-Info "nscim_app : LOGIN NOSUPERUSER NOBYPASSRLS"

    # GRANT CONNECT on each DB so nscim_app can reach it.
    foreach ($db in $Databases) {
        $r = Invoke-Psql -Env $Env -Database 'postgres' -Sql "GRANT CONNECT ON DATABASE `"$db`" TO nscim_app;"
        if ($r.ExitCode -ne 0) { Write-Error "GRANT CONNECT on $db failed: $($r.Output)"; return 2 }
    }

    return 0
}

function Get-AppliedMigrations {
    param(
        [hashtable]$Env,
        [string]$Database,
        [string]$Schema
    )
    # The history table only exists once the first migration runs. Probe
    # for the schema first; absent schema = zero migrations applied. A
    # non-zero psql exit means the probe itself failed (host unreachable,
    # bad creds) — surface that as a throw so the caller doesn't mistake
    # it for "no rows yet."
    $hasSchema = Invoke-Psql -Env $Env -Database $Database `
        -Sql "SELECT 1 FROM information_schema.schemata WHERE schema_name = '$Schema';"
    if ($hasSchema.ExitCode -ne 0) { throw "Schema probe failed for $Database.$Schema : $($hasSchema.Output)" }
    if ([string]::IsNullOrEmpty($hasSchema.Output)) { return @() }

    $hasTable = Invoke-Psql -Env $Env -Database $Database `
        -Sql "SELECT 1 FROM information_schema.tables WHERE table_schema = '$Schema' AND table_name = '__EFMigrationsHistory';"
    if ($hasTable.ExitCode -ne 0) { throw "Table probe failed for $Database.$Schema.__EFMigrationsHistory : $($hasTable.Output)" }
    if ([string]::IsNullOrEmpty($hasTable.Output)) { return @() }

    $rows = Invoke-Psql -Env $Env -Database $Database `
        -Sql "SELECT `"MigrationId`" FROM `"$Schema`".`"__EFMigrationsHistory`" ORDER BY `"MigrationId`";"
    if ($rows.ExitCode -ne 0) { throw "History read failed for $Database.$Schema : $($rows.Output)" }
    if ([string]::IsNullOrEmpty($rows.Output)) { return @() }
    return $rows.Output -split "`r?`n" | Where-Object { $_ -ne '' }
}

function Get-ExpectedMigrations {
    param([string]$ProjectPath)
    # EF Core migration files are *.cs in <project>/Migrations/, excluding
    # *.Designer.cs and *ModelSnapshot.cs. Filename is the timestamped id.
    $migrationsDir = Join-Path $ProjectPath 'Migrations'
    if (-not (Test-Path $migrationsDir)) { return @() }
    Get-ChildItem -Path $migrationsDir -Filter '*.cs' |
        Where-Object { $_.Name -notmatch '\.Designer\.cs$' -and $_.Name -notmatch 'ModelSnapshot\.cs$' } |
        ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) } |
        Sort-Object
}

function Invoke-MigrationApply {
    param(
        [hashtable]$Env,
        [pscustomobject]$Context,
        [hashtable]$ContextResult
    )

    $projectAbs = Join-Path $RepoRoot $Context.Project
    if (-not (Test-Path $projectAbs)) {
        Write-Error "Project path absent: $projectAbs"
        $ContextResult.Status = 'PROJECT_MISSING'
        return 5
    }

    Write-Step "Apply migrations: $($Context.Name) → $($Context.Database).$($Context.Schema)"

    $expected = Get-ExpectedMigrations -ProjectPath $projectAbs
    $appliedBefore = Get-AppliedMigrations -Env $Env -Database $Context.Database -Schema $Context.Schema
    $ContextResult.ExpectedCount = $expected.Count
    $ContextResult.AppliedBefore = $appliedBefore.Count
    Write-Info "expected=$($expected.Count) applied-before=$($appliedBefore.Count)"

    # Generate idempotent SQL via `dotnet ef migrations script --idempotent`.
    # This is the FU-5-recommended path: SQL generation is purely local
    # (no DB connection) so it sidesteps the dotnet ef child-process
    # env-var quirk on Windows. Apply via `psql -f`, which reads
    # PGPASSWORD correctly.
    $sqlPath = Join-Path $env:TEMP "nickerp-cutover-$($Context.Name)-$(Get-Date -Format 'yyyyMMddHHmmss').sql"

    $genStart = Get-Date
    Push-Location $projectAbs
    try {
        $genOut = & dotnet ef migrations script --idempotent --output $sqlPath --context $Context.Name 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "dotnet ef migrations script failed for $($Context.Name)"
            Write-Host ($genOut | Out-String)
            $ContextResult.Status = 'SCRIPT_GEN_FAILED'
            $ContextResult.FailureDetail = ($genOut | Out-String).Trim()
            return 3
        }
    }
    finally {
        Pop-Location
    }
    $ContextResult.GenerationSeconds = [math]::Round(((Get-Date) - $genStart).TotalSeconds, 2)
    Write-Info "script generated in $($ContextResult.GenerationSeconds)s — $sqlPath"

    # Apply via psql. Use the admin (postgres) user — runbook 14 / MIGRATIONS.md
    # §dev-cycle-shortcut: DDL is run by an operator with elevated rights, not
    # by the application. nscim_app stays out of the migration-apply path
    # entirely.
    $applyStart = Get-Date
    $r = Invoke-PsqlFile -Env $Env -Database $Context.Database -File $sqlPath
    $ContextResult.ApplySeconds = [math]::Round(((Get-Date) - $applyStart).TotalSeconds, 2)
    if ($r.ExitCode -ne 0) {
        Write-Error "psql apply failed for $($Context.Name)"
        Write-Host $r.Output
        $ContextResult.Status = 'APPLY_FAILED'
        $ContextResult.FailureDetail = $r.Output

        # Try to identify which migration tripped — find the last DO $EF$
        # block that started in the script and report it.
        $lastMigration = ''
        try {
            $sqlText = Get-Content -Path $sqlPath -Raw
            $matches = [regex]::Matches($sqlText, "MigrationId.*?'(?<id>\d{14}_[^']+)'")
            if ($matches.Count -gt 0) { $lastMigration = $matches[$matches.Count - 1].Groups['id'].Value }
        } catch {}
        $ContextResult.FailedMigrationGuess = $lastMigration
        Write-Info "best-guess failing migration: $lastMigration"
        Write-Info "reproduction: $script:PsqlExe -h $($Env.Host) -p $($Env.Port) -U $($Env.AdminUser) -d $($Context.Database) -v ON_ERROR_STOP=1 -f `"$sqlPath`""
        return 3
    }

    $appliedAfter = Get-AppliedMigrations -Env $Env -Database $Context.Database -Schema $Context.Schema
    $ContextResult.AppliedAfter = $appliedAfter.Count
    $ContextResult.NewlyApplied = $appliedAfter.Count - $appliedBefore.Count
    $ContextResult.HistoryRows = $appliedAfter
    $ContextResult.SqlScriptPath = $sqlPath

    if ($ContextResult.NewlyApplied -eq 0) {
        Write-Info "0 new migrations applied (idempotent re-run)"
    } else {
        Write-Info "applied $($ContextResult.NewlyApplied) new migration(s) in $($ContextResult.ApplySeconds)s"
    }

    if ($appliedAfter.Count -ne $expected.Count) {
        Write-Warning "applied count $($appliedAfter.Count) != expected count $($expected.Count) for $($Context.Name)"
        $ContextResult.Status = 'COUNT_MISMATCH'
        return 3
    }

    $ContextResult.Status = 'OK'
    return 0
}

function Invoke-PilotAcceptanceTests {
    param([hashtable]$Env)
    Write-Step "Run dotnet test --filter Category=PilotAcceptance"

    # PilotAcceptanceFixture reads NICKSCAN_DB_PASSWORD and connects to
    # localhost:5432 as `postgres`. In container mode we expose 55432 by
    # default, so PilotAcceptance only round-trips end-to-end if the
    # caller runs against a host-port-5432 server. Surface that constraint
    # rather than silently skipping.
    if ($Env.Port -ne 5432) {
        Write-Warning "PilotAcceptance fixture is hard-wired to localhost:5432 — current target is port $($Env.Port)."
        Write-Warning "Tests will skip via the fixture's null-return path. Re-run with -Port 5432 to exercise them."
    }

    $env:NICKSCAN_DB_PASSWORD = $Env.AdminPassword
    $testStart = Get-Date
    Push-Location $RepoRoot
    try {
        $logPath = Join-Path $env:TEMP "nickerp-cutover-tests-$(Get-Date -Format 'yyyyMMddHHmmss').log"
        & dotnet test NickERP.Tests.slnx `
            --filter "Category=PilotAcceptance" `
            --logger "console;verbosity=normal" `
            --nologo 2>&1 | Tee-Object -FilePath $logPath | Out-Host
        $exitCode = $LASTEXITCODE
        $duration = [math]::Round(((Get-Date) - $testStart).TotalSeconds, 2)
        return @{
            ExitCode = $exitCode
            DurationSeconds = $duration
            LogPath = $logPath
        }
    }
    finally {
        Pop-Location
        Remove-Item Env:\NICKSCAN_DB_PASSWORD -ErrorAction SilentlyContinue
    }
}

function Write-Report {
    param(
        [hashtable]$ReportData,
        [string]$Path
    )
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# NickERP cutover dry-run — migration report")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("> Generated by ``tools/cutover-dryrun/run.ps1`` — Sprint 60 Phase A (PLAN.md §23.2 work item A).")
    [void]$sb.AppendLine("> Verifies the runbook 14 §4-§10 migration block on a clean PG17 target.")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## Run summary")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Field | Value |")
    [void]$sb.AppendLine("|---|---|")
    [void]$sb.AppendLine("| Started | $($ReportData.StartedAt) |")
    [void]$sb.AppendLine("| Finished | $($ReportData.FinishedAt) |")
    [void]$sb.AppendLine("| Duration | $($ReportData.DurationSeconds)s |")
    [void]$sb.AppendLine("| Mode | $($ReportData.Mode) |")
    [void]$sb.AppendLine("| Target | $($ReportData.TargetHost):$($ReportData.TargetPort) |")
    [void]$sb.AppendLine("| Postgres version | ``$($ReportData.PostgresVersion)`` |")
    [void]$sb.AppendLine("| Exit code | **$($ReportData.ExitCode)** |")
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("## Migrations per context")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Context | DB | Schema | Expected | Applied (after) | Newly applied | Apply (s) | Status |")
    [void]$sb.AppendLine("|---|---|---|---:|---:|---:|---:|---|")
    foreach ($ctx in $ReportData.Contexts) {
        $applyS = if ($ctx.ApplySeconds) { $ctx.ApplySeconds } else { '-' }
        [void]$sb.AppendLine("| ``$($ctx.Name)`` | $($ctx.Database) | $($ctx.Schema) | $($ctx.ExpectedCount) | $($ctx.AppliedAfter) | $($ctx.NewlyApplied) | $applyS | $($ctx.Status) |")
    }
    [void]$sb.AppendLine("")

    $totalExpected = ($ReportData.Contexts | Measure-Object -Property ExpectedCount -Sum).Sum
    $totalApplied  = ($ReportData.Contexts | Measure-Object -Property AppliedAfter -Sum).Sum
    $totalNew      = ($ReportData.Contexts | Measure-Object -Property NewlyApplied -Sum).Sum
    [void]$sb.AppendLine("**Totals:** expected=$totalExpected, applied-after=$totalApplied, newly-applied=$totalNew.")
    [void]$sb.AppendLine("")

    if ($ReportData.Contexts | Where-Object { $_.Status -eq 'APPLY_FAILED' -or $_.Status -eq 'SCRIPT_GEN_FAILED' -or $_.Status -eq 'COUNT_MISMATCH' }) {
        [void]$sb.AppendLine("## Failure detail")
        [void]$sb.AppendLine("")
        foreach ($ctx in $ReportData.Contexts) {
            if ($ctx.Status -eq 'APPLY_FAILED' -or $ctx.Status -eq 'SCRIPT_GEN_FAILED' -or $ctx.Status -eq 'COUNT_MISMATCH') {
                [void]$sb.AppendLine("### $($ctx.Name) — $($ctx.Status)")
                [void]$sb.AppendLine("")
                if ($ctx.FailedMigrationGuess) {
                    [void]$sb.AppendLine("- Best-guess failing migration: ``$($ctx.FailedMigrationGuess)``")
                }
                if ($ctx.SqlScriptPath) {
                    [void]$sb.AppendLine("- Reproduction:")
                    [void]$sb.AppendLine("")
                    [void]$sb.AppendLine("  ``````")
                    [void]$sb.AppendLine("  PGPASSWORD=`$(env) psql -h $($ReportData.TargetHost) -p $($ReportData.TargetPort) -U $($ReportData.AdminUser) -d $($ctx.Database) -v ON_ERROR_STOP=1 -f $($ctx.SqlScriptPath)")
                    [void]$sb.AppendLine("  ``````")
                    [void]$sb.AppendLine("")
                }
                if ($ctx.FailureDetail) {
                    [void]$sb.AppendLine("- psql output:")
                    [void]$sb.AppendLine("")
                    [void]$sb.AppendLine("  ``````")
                    foreach ($line in ($ctx.FailureDetail -split "`r?`n")) {
                        [void]$sb.AppendLine("  $line")
                    }
                    [void]$sb.AppendLine("  ``````")
                    [void]$sb.AppendLine("")
                }
            }
        }
    }

    [void]$sb.AppendLine("## __EFMigrationsHistory final state")
    [void]$sb.AppendLine("")
    foreach ($ctx in $ReportData.Contexts) {
        [void]$sb.AppendLine("### $($ctx.Database).$($ctx.Schema)")
        [void]$sb.AppendLine("")
        if ($ctx.HistoryRows -and $ctx.HistoryRows.Count -gt 0) {
            [void]$sb.AppendLine("``````")
            foreach ($row in $ctx.HistoryRows) { [void]$sb.AppendLine($row) }
            [void]$sb.AppendLine("``````")
        } else {
            [void]$sb.AppendLine("_(no rows — schema or history table absent)_")
        }
        [void]$sb.AppendLine("")
    }

    [void]$sb.AppendLine("## Test summary")
    [void]$sb.AppendLine("")
    if ($ReportData.TestResult) {
        $tr = $ReportData.TestResult
        $verdict = if ($tr.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }
        [void]$sb.AppendLine("- Filter: ``Category=PilotAcceptance``")
        [void]$sb.AppendLine("- Verdict: **$verdict**")
        [void]$sb.AppendLine("- Duration: $($tr.DurationSeconds)s")
        [void]$sb.AppendLine("- Test log: ``$($tr.LogPath)``")
        [void]$sb.AppendLine("- dotnet test exit code: $($tr.ExitCode)")
    } else {
        [void]$sb.AppendLine("_skipped (-SkipTests)_")
    }
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("## Generated artifacts")
    [void]$sb.AppendLine("")
    foreach ($ctx in $ReportData.Contexts) {
        if ($ctx.SqlScriptPath) {
            [void]$sb.AppendLine("- ``$($ctx.SqlScriptPath)`` — idempotent SQL for $($ctx.Name) (regenerable)")
        }
    }
    [void]$sb.AppendLine("")

    Set-Content -Path $Path -Value $sb.ToString() -Encoding UTF8
    Write-Info "report : $Path"
}

# ============================================================
# Main
# ============================================================

$startedAt = Get-Date
$mode = if ([string]::IsNullOrEmpty($TargetUri)) { 'container' } else { 'real-host' }

if ($DryRun) {
    Write-Step "Dry run plan"
    Write-Info "mode      : $mode"
    Write-Info "contexts  : $($DbContexts.Count) ($($DbContexts.Name -join ', '))"
    Write-Info "databases : $($Databases -join ', ')"
    Write-Info "reports   : $ReportsDir"
    Write-Info "no commands executed"
    exit 0
}

# Pre-flight.
$preflightExit = Test-Preflight -NeedsDocker:($mode -eq 'container')
if ($preflightExit -ne 0) { exit $preflightExit }

# Resolve env (passwords + connection params).
$envParams = @{}
if ($mode -eq 'container') {
    if ([string]::IsNullOrEmpty($AdminPassword)) { $AdminPassword = 'cutover-dryrun-admin-pw' }
    if ([string]::IsNullOrEmpty($NscimPassword)) { $NscimPassword = 'cutover-dryrun-nscim-pw' }
    $envParams = @{
        Mode          = 'container'
        Host          = '127.0.0.1'
        Port          = $Port
        AdminUser     = 'postgres'
        AdminPassword = $AdminPassword
        NscimPassword = $NscimPassword
        ContainerName = $ContainerName
    }

    $startExit = Start-PostgresContainer -Name $ContainerName -Image $Image -Port $Port -AdminPassword $AdminPassword
    if ($startExit -ne 0) { exit $startExit }
} else {
    # Parse postgres://user:pass@host:port/db (db is ignored — we use the
    # canonical 3-DB layout). PowerShell's [Uri] handles the url shape.
    try {
        $uri = [Uri]$TargetUri
    } catch {
        Write-Error "Could not parse -TargetUri '$TargetUri': $_"
        exit 5
    }
    $userInfo = $uri.UserInfo -split ':', 2
    $adminUser = if ($userInfo.Count -ge 1 -and $userInfo[0]) { $userInfo[0] } else { 'postgres' }
    $adminPw   = if ($userInfo.Count -ge 2) { [Uri]::UnescapeDataString($userInfo[1]) } else { $AdminPassword }
    if ([string]::IsNullOrEmpty($adminPw)) { Write-Error "Admin password absent in -TargetUri and -AdminPassword"; exit 5 }
    if ([string]::IsNullOrEmpty($NscimPassword)) { $NscimPassword = $adminPw }
    $envParams = @{
        Mode          = 'real-host'
        Host          = $uri.Host
        Port          = if ($uri.Port -gt 0) { $uri.Port } else { 5432 }
        AdminUser     = $adminUser
        AdminPassword = $adminPw
        NscimPassword = $NscimPassword
    }
}

# Gather PG version.
$pgVersion = Get-PostgresVersion -Env $envParams
Write-Info "postgres : $pgVersion"
if ($pgVersion -notmatch 'PostgreSQL\s+17\b') {
    Write-Warning "Target is not PostgreSQL 17 (per runbook 11). Continuing - script does not enforce."
}

# Provision databases + nscim_app role.
$initExit = Initialize-Databases -Env $envParams
if ($initExit -ne 0) { exit $initExit }

# Apply migrations per context, in declared order.
$contextResults = @()
$migrationFailed = $false
foreach ($ctx in $DbContexts) {
    $r = [ordered]@{
        Name                 = $ctx.Name
        Project              = $ctx.Project
        Database             = $ctx.Database
        Schema               = $ctx.Schema
        ExpectedCount        = 0
        AppliedBefore        = 0
        AppliedAfter         = 0
        NewlyApplied         = 0
        GenerationSeconds    = 0
        ApplySeconds         = 0
        SqlScriptPath        = $null
        HistoryRows          = @()
        Status               = 'PENDING'
        FailureDetail        = $null
        FailedMigrationGuess = $null
    }
    $rh = [hashtable]$r
    $rc = Invoke-MigrationApply -Env $envParams -Context $ctx -ContextResult $rh
    $contextResults += [pscustomobject]$rh
    if ($rc -ne 0) {
        $migrationFailed = $true
        break
    }
}

# PilotAcceptance tests (optional).
$testResult = $null
if (-not $SkipTests -and -not $migrationFailed) {
    $testResult = Invoke-PilotAcceptanceTests -Env $envParams
}

# Compose report.
$finishedAt = Get-Date
$exitCode = 0
if ($migrationFailed) { $exitCode = 3 }
elseif ($testResult -and $testResult.ExitCode -ne 0) { $exitCode = 4 }

$reportData = @{
    StartedAt        = $startedAt.ToString('o')
    FinishedAt       = $finishedAt.ToString('o')
    DurationSeconds  = [math]::Round(($finishedAt - $startedAt).TotalSeconds, 2)
    Mode             = $envParams.Mode
    TargetHost       = $envParams.Host
    TargetPort       = $envParams.Port
    AdminUser        = $envParams.AdminUser
    PostgresVersion  = $pgVersion
    Contexts         = $contextResults
    TestResult       = $testResult
    ExitCode         = $exitCode
}

$reportName = "dryrun-$(Get-Date -Format 'yyyy-MM-dd-HHmmss')-migration-report.md"
$reportPath = Join-Path $ReportsDir $reportName
Write-Report -ReportData $reportData -Path $reportPath

# Cleanup container if requested.
if ($mode -eq 'container' -and $Cleanup) {
    Write-Step "Removing container $ContainerName"
    & docker rm -f $ContainerName 2>&1 | Out-Null
}

Write-Step "Done — exit $exitCode"
exit $exitCode
