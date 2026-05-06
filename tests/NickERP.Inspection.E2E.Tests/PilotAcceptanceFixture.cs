using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Pilot;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Platform.Tenancy.Pilot;
using Npgsql;

namespace NickERP.Inspection.E2E.Tests;

/// <summary>
/// Sprint 53 — scenario engine for the pilot-acceptance integration test
/// suite. Stands up a per-run pair of Postgres databases (platform +
/// inspection), applies migrations, and exposes a small suite of
/// scenario helpers that emit exactly the audit events / DB rows the 5
/// pilot-readiness gates observe in production. The companion
/// <see cref="PilotAcceptanceTests"/> drives this fixture to prove the
/// gates flip Pass under a realistic flow.
///
/// <para>
/// <b>Why a dedicated fixture.</b> Sprint 43's <c>PilotReadinessService</c>
/// runs 5 gate probes against real audit / inspection state; previous
/// E2E tests (D4 <see cref="FullCaseLifecycleTests"/>, E1
/// <see cref="MultiLocationFederationTests"/>) drive the workflow
/// service end-to-end but don't isolate the gate signals. This fixture
/// is the SCENARIO ENGINE — each helper writes one of the gate-evidence
/// signals so the test can drive the gates Pass-by-Pass and assert them
/// individually.
/// </para>
///
/// <para>
/// <b>DB strategy.</b> Testcontainers is preferred but Docker is not
/// available on the build host (per the F2 audit + the existing fixtures
/// that all fall back to <c>localhost:5432</c>). This fixture follows
/// the same pattern: connect to dev Postgres on
/// <c>localhost:5432</c> with <c>NICKSCAN_DB_PASSWORD</c>, create
/// per-run unique-suffixed databases, drop them on disposal. If
/// <c>NICKSCAN_DB_PASSWORD</c> is unset, <see cref="CreateAsync"/>
/// returns <see langword="null"/> so the calling test can skip rather
/// than fail loudly — pilot-acceptance tests are opt-in via the
/// <c>[Trait("Category", "PilotAcceptance")]</c> filter.
/// </para>
///
/// <para>
/// <b>Tenant lifecycle.</b> The fixture provisions two active tenants:
/// the default tenant 1 (seeded by <c>TenancyDbContext.HasData</c>) and
/// a freshly-allocated tenant whose id we capture into
/// <see cref="TenantBId"/>. Both are ACTIVE
/// (<c>tenancy.tenants.State = 0</c>); both stay alive for the test
/// run; both are torn down with the database on disposal. No leftover
/// rows on the dev Postgres after the test exits cleanly.
/// </para>
///
/// <para>
/// <b>No production code changes.</b> This fixture only writes audit
/// events / DB rows that the production probes already observe. No
/// new <c>SetSystemContext</c> callers, no new event types, no
/// modifications to <c>PilotReadinessService</c>. Gate proofs are real;
/// the fixture just realistically exercises them.
/// </para>
/// </summary>
internal sealed class PilotAcceptanceFixture : IAsyncDisposable
{
    private const string AdminTemplate =
        "Host=localhost;Port=5432;Database={0};Username=postgres;Password={1};Pooling=false";

    public string PlatformDbName { get; }
    public string InspectionDbName { get; }
    public string PlatformConnectionString { get; }
    public string InspectionConnectionString { get; }

    /// <summary>The default seeded tenant id — id 1, code <c>nick-tc-scan</c>.</summary>
    public long TenantAId => 1L;

    /// <summary>
    /// The second tenant freshly allocated by the fixture in
    /// <see cref="ProvisionAsync"/>. Always <c>&gt; TenantAId</c>.
    /// </summary>
    public long TenantBId { get; private set; }

    private readonly string _adminConnectionString;
    private bool _disposed;

    private PilotAcceptanceFixture(string adminPassword, string suffix)
    {
        _adminConnectionString = string.Format(AdminTemplate, "postgres", adminPassword);
        PlatformDbName = $"nickerp_e2e_pa_{suffix}_platform";
        InspectionDbName = $"nickerp_e2e_pa_{suffix}_inspection";
        PlatformConnectionString = string.Format(AdminTemplate, PlatformDbName, adminPassword);
        InspectionConnectionString = string.Format(AdminTemplate, InspectionDbName, adminPassword);
    }

    /// <summary>
    /// Stand up the test DB pair and return the fixture, OR return
    /// <see langword="null"/> if <c>NICKSCAN_DB_PASSWORD</c> is not set
    /// (so the test can skip rather than fail). Mirrors the
    /// skip-if-not-available pattern from the Sprint 43 RLS integration
    /// test where Postgres dependence is gated by env-var presence.
    /// </summary>
    public static async Task<PilotAcceptanceFixture?> CreateAsync(CancellationToken ct = default)
    {
        var password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        var fixture = new PilotAcceptanceFixture(password, suffix);
        await fixture.CreateDatabasesAsync(ct);
        return fixture;
    }

    private async Task CreateDatabasesAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{PlatformDbName}\";", conn))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{InspectionDbName}\";", conn))
            await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Apply migrations across all 4 contexts (Identity / Audit /
    /// Tenancy / Inspection) and provision the second tenant. Call
    /// once per fixture instance, immediately after
    /// <see cref="CreateAsync"/>.
    /// </summary>
    public async Task ProvisionAsync(CancellationToken ct = default)
    {
        // Reuse the shared schema applier so we apply exactly the same
        // migration set the production host runs at startup. The history
        // tables land in per-context schemas (Sprint H3 posture).
        await TestSchemaApplier.ApplyAllAsync(
            PlatformConnectionString,
            InspectionConnectionString,
            ct);

        // Provision the second tenant. tenancy.tenants is intentionally
        // not under RLS (root of the tenant graph; see TENANCY.md) so
        // no app.tenant_id push is needed for the INSERT. The
        // identity-always Id is server-assigned and we read it back so
        // tests can target the right tenant.
        await using var conn = new NpgsqlConnection(PlatformConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO tenancy.tenants
                (""Code"", ""Name"", ""BillingPlan"", ""TimeZone"", ""Locale"",
                 ""Currency"", ""State"", ""RetentionDays"",
                 ""AllowMultiServiceMembership"", ""CaseVisibilityModel"",
                 ""CreatedAt"")
              VALUES ('pilot-acceptance-b', 'Pilot Acceptance B', 'internal',
                      'Africa/Accra', 'en-GH', 'GHS', 0, 90, true, 0, @now)
              RETURNING ""Id"";",
            conn);
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        TenantBId = (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ---- scenario helpers ---------------------------------------------------

    /// <summary>
    /// Helper #1 — register a scanner and emit a single
    /// <c>nickerp.inspection.scan_recorded</c> audit event for the
    /// supplied tenant. This is exactly the audit-event shape
    /// <c>PilotReadinessService.ProbeScannerAdapterAsync</c> looks up
    /// to flip <c>gate.scanner.adapter</c> to Pass. Vendor-neutral —
    /// the device-type-code argument feeds the audit payload but the
    /// gate's predicate is type-agnostic.
    /// </summary>
    /// <returns>The scanner instance id and the proof event id.</returns>
    public async Task<(Guid scannerInstanceId, Guid proofEventId)> RegisterScannerAsync(
        long tenantId,
        string deviceTypeCode = "fs6000",
        CancellationToken ct = default)
    {
        var locationId = Guid.NewGuid();
        var scannerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Write Location + Scanner instance in the inspection DB so the
        // gate signal is grounded in real configuration (mirrors what
        // ScannerIngestionWorker would do under live traffic).
        await using (var conn = new NpgsqlConnection(InspectionConnectionString))
        {
            await conn.OpenAsync(ct);
            await using (var setTenant = new NpgsqlCommand(
                $"SET app.tenant_id = '{tenantId}';", conn))
                await setTenant.ExecuteNonQueryAsync(ct);

            await using (var loc = new NpgsqlCommand(
                @"INSERT INTO inspection.locations
                    (""Id"", ""Code"", ""Name"", ""Region"", ""TimeZone"",
                     ""IsActive"", ""CreatedAt"", ""TenantId"")
                  VALUES (@id, @code, @name, 'Greater Accra', 'Africa/Accra',
                          true, @now, @tenant);", conn))
            {
                loc.Parameters.AddWithValue("id", locationId);
                loc.Parameters.AddWithValue("code", $"loc-{tenantId}-{Guid.NewGuid():N}".Substring(0, 32));
                loc.Parameters.AddWithValue("name", $"Pilot acceptance location T{tenantId}");
                loc.Parameters.AddWithValue("now", now);
                loc.Parameters.AddWithValue("tenant", tenantId);
                await loc.ExecuteNonQueryAsync(ct);
            }

            await using (var scn = new NpgsqlCommand(
                @"INSERT INTO inspection.scanner_device_instances
                    (""Id"", ""LocationId"", ""StationId"", ""TypeCode"", ""DisplayName"",
                     ""Description"", ""ConfigJson"", ""IsActive"", ""CreatedAt"", ""TenantId"")
                  VALUES (@id, @loc, NULL, @typeCode, @name,
                          'Pilot acceptance scanner', '{}'::jsonb, true, @now, @tenant);", conn))
            {
                scn.Parameters.AddWithValue("id", scannerId);
                scn.Parameters.AddWithValue("loc", locationId);
                scn.Parameters.AddWithValue("typeCode", deviceTypeCode);
                scn.Parameters.AddWithValue("name", $"PA Scanner T{tenantId}");
                scn.Parameters.AddWithValue("now", now);
                scn.Parameters.AddWithValue("tenant", tenantId);
                await scn.ExecuteNonQueryAsync(ct);
            }
        }

        // The gate's actual signal: a single nickerp.inspection.scan_recorded
        // audit event. Production CaseWorkflowService.IngestArtifactAsync
        // emits this when a scan lands; here we synthesize one directly so
        // the gate has its proof without spinning the full ingestion
        // pipeline (PreRenderWorker, plugin loader, FS6000 byte-decode).
        var proofEventId = await EmitAuditEventAsync(
            tenantId,
            eventType: "nickerp.inspection.scan_recorded",
            entityType: "Scan",
            entityId: scannerId.ToString(),
            payload: new { scannerInstanceId = scannerId, deviceTypeCode, locationId },
            ct);

        return (scannerId, proofEventId);
    }

    /// <summary>
    /// Helper #2 — emit one <c>inspection.scan.captured</c> audit event
    /// with <c>replay_source = "edge"</c>, exactly as
    /// <c>EdgeReplayEndpoint.AugmentPayload</c> would write under a
    /// successful edge replay. This is the signal
    /// <c>PilotReadinessService.ProbeEdgeRoundtripAsync</c> looks up to
    /// flip <c>gate.edge.roundtrip</c> to Pass.
    /// </summary>
    public async Task<Guid> EmitEdgeRoundTripAsync(
        long tenantId,
        Guid scannerInstanceId,
        string edgeNodeId = "pilot-edge-01",
        CancellationToken ct = default)
    {
        var sourcePath = $"/edge/{edgeNodeId}/{Guid.NewGuid():N}.zip";
        var now = DateTimeOffset.UtcNow;

        // Mirror the augment-and-store shape EdgeReplayEndpoint would
        // write: original payload + replay_source/replay_node_id/
        // replayed_at at the top level.
        var augmented = new
        {
            scannerId = scannerInstanceId.ToString(),
            sourcePath,
            replay_source = "edge",
            replay_node_id = edgeNodeId,
            replayed_at = now.ToString("O")
        };

        return await EmitAuditEventAsync(
            tenantId,
            eventType: "inspection.scan.captured",
            entityType: "ScanArtifact",
            entityId: sourcePath,
            payload: augmented,
            ct);
    }

    /// <summary>
    /// Helper #3 — open and decision a non-synthetic
    /// <see cref="InspectionCase"/> end-to-end, emitting the
    /// <c>nickerp.inspection.verdict_set</c> audit event the gate
    /// observes. <see cref="InspectionCase.IsSynthetic"/> is set
    /// according to the supplied flag — pass <c>isSynthetic: false</c>
    /// to satisfy <c>gate.analyst.decisioned_real_case</c>; pass
    /// <c>isSynthetic: true</c> to PROVE the gate ignores synthetic
    /// cases (the negative-control test).
    /// </summary>
    /// <returns>The case id and the proof verdict event id.</returns>
    public async Task<(Guid caseId, Guid verdictProofEventId)> OpenAndDecisionRealCaseAsync(
        long tenantId,
        Guid analystUserId,
        Guid? scannerLocationId = null,
        bool isSynthetic = false,
        CancellationToken ct = default)
    {
        var caseId = Guid.NewGuid();
        var verdictId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Resolve a Location id — accept the caller's hint or seed a
        // throwaway. The case row needs a real LocationId because the
        // FK constraint on inspection.cases.LocationId is on; the helper
        // tolerates either path so callers can chain
        // RegisterScannerAsync's location into here.
        Guid locationId;
        if (scannerLocationId is { } known)
        {
            locationId = known;
        }
        else
        {
            locationId = Guid.NewGuid();
            await using var seed = new NpgsqlConnection(InspectionConnectionString);
            await seed.OpenAsync(ct);
            await using var setTenant = new NpgsqlCommand(
                $"SET app.tenant_id = '{tenantId}';", seed);
            await setTenant.ExecuteNonQueryAsync(ct);
            await using var locCmd = new NpgsqlCommand(
                @"INSERT INTO inspection.locations
                    (""Id"", ""Code"", ""Name"", ""Region"", ""TimeZone"",
                     ""IsActive"", ""CreatedAt"", ""TenantId"")
                  VALUES (@id, @code, 'PA temp location', 'Greater Accra', 'Africa/Accra',
                          true, @now, @tenant);", seed);
            locCmd.Parameters.AddWithValue("id", locationId);
            locCmd.Parameters.AddWithValue("code", $"pa-{Guid.NewGuid():N}".Substring(0, 32));
            locCmd.Parameters.AddWithValue("now", now);
            locCmd.Parameters.AddWithValue("tenant", tenantId);
            await locCmd.ExecuteNonQueryAsync(ct);
        }

        // Insert the case + verdict directly. We bypass CaseWorkflowService
        // to keep the helper focused on the gate signal: the gate observes
        // (a) a Verdict row and (b) IsSynthetic = false, plus the
        // nickerp.inspection.verdict_set audit event. The full lifecycle
        // is exercised by the existing D4 test; this helper just lays
        // down the proofs.
        await using (var conn = new NpgsqlConnection(InspectionConnectionString))
        {
            await conn.OpenAsync(ct);
            await using (var setTenant = new NpgsqlCommand(
                $"SET app.tenant_id = '{tenantId}';", conn))
                await setTenant.ExecuteNonQueryAsync(ct);

            await using (var caseCmd = new NpgsqlCommand(
                @"INSERT INTO inspection.cases
                    (""Id"", ""LocationId"", ""SubjectType"", ""SubjectIdentifier"",
                     ""SubjectPayloadJson"", ""State"", ""OpenedAt"", ""StateEnteredAt"",
                     ""ReviewQueue"", ""RetentionClass"", ""LegalHold"",
                     ""IsSynthetic"", ""TenantId"")
                  VALUES (@id, @loc, 0, @subj,
                          '{}'::jsonb, 5, @now, @now,
                          0, 0, false,
                          @synth, @tenant);", conn))
            {
                caseCmd.Parameters.AddWithValue("id", caseId);
                caseCmd.Parameters.AddWithValue("loc", locationId);
                caseCmd.Parameters.AddWithValue("subj", $"PA-{caseId:N}".Substring(0, 16));
                caseCmd.Parameters.AddWithValue("now", now);
                caseCmd.Parameters.AddWithValue("synth", isSynthetic);
                caseCmd.Parameters.AddWithValue("tenant", tenantId);
                await caseCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var verdictCmd = new NpgsqlCommand(
                @"INSERT INTO inspection.verdicts
                    (""Id"", ""CaseId"", ""Decision"", ""Basis"",
                     ""DecidedAt"", ""DecidedByUserId"", ""TenantId"")
                  VALUES (@id, @caseId, 0, 'Pilot acceptance — clear',
                          @now, @actor, @tenant);", conn))
            {
                verdictCmd.Parameters.AddWithValue("id", verdictId);
                verdictCmd.Parameters.AddWithValue("caseId", caseId);
                verdictCmd.Parameters.AddWithValue("now", now);
                verdictCmd.Parameters.AddWithValue("actor", analystUserId);
                verdictCmd.Parameters.AddWithValue("tenant", tenantId);
                await verdictCmd.ExecuteNonQueryAsync(ct);
            }
        }

        // Emit the verdict_set audit event. Production
        // CaseWorkflowService.SetVerdictAsync writes EntityType="Verdict",
        // EntityId=verdict.Id; the gate's predicate (in
        // PilotReadinessService.ProbeAnalystDecisionedRealCaseAsync)
        // resolves the proof event by EntityId=case.Id.ToString() but the
        // gate state is driven by the data-source's HasDecisionedRealCase
        // bool, so even if the proof event id resolves null the gate
        // still flips Pass. Emit one shaped exactly like production.
        var proofEventId = await EmitAuditEventAsync(
            tenantId,
            eventType: "nickerp.inspection.verdict_set",
            entityType: "Verdict",
            entityId: verdictId.ToString(),
            payload: new { Id = verdictId, CaseId = caseId, Decision = 0, Basis = "Pilot acceptance — clear", IsSynthetic = isSynthetic },
            ct);

        return (caseId, proofEventId);
    }

    /// <summary>
    /// Helper #4 — write an <c>OutboundSubmission</c> in
    /// <c>Status = "accepted"</c> with <c>LastAttemptAt</c> not null
    /// for the supplied tenant. Vendor-neutral; the gate doesn't care
    /// which adapter (icums-gh / cmr / boe) flipped the row, just that
    /// the row exists in the right shape.
    /// </summary>
    public async Task CompleteExternalSystemSubmissionAsync(
        long tenantId,
        Guid caseId,
        CancellationToken ct = default)
    {
        var submissionId = Guid.NewGuid();
        var externalInstanceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var conn = new NpgsqlConnection(InspectionConnectionString);
        await conn.OpenAsync(ct);
        await using (var setTenant = new NpgsqlCommand(
            $"SET app.tenant_id = '{tenantId}';", conn))
            await setTenant.ExecuteNonQueryAsync(ct);

        // Need an ExternalSystemInstance for the FK on
        // OutboundSubmission. Vendor-neutral type-code; the actual adapter
        // stays cold — we just need a row that satisfies the FK.
        await using (var extCmd = new NpgsqlCommand(
            @"INSERT INTO inspection.external_system_instances
                (""Id"", ""TypeCode"", ""DisplayName"", ""Description"", ""Scope"",
                 ""ConfigJson"", ""IsActive"", ""CreatedAt"", ""TenantId"")
              VALUES (@id, 'icums-gh', 'PA External', NULL, 0,
                      '{}'::jsonb, true, @now, @tenant);", conn))
        {
            extCmd.Parameters.AddWithValue("id", externalInstanceId);
            extCmd.Parameters.AddWithValue("now", now);
            extCmd.Parameters.AddWithValue("tenant", tenantId);
            await extCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var subCmd = new NpgsqlCommand(
            @"INSERT INTO inspection.outbound_submissions
                (""Id"", ""CaseId"", ""ExternalSystemInstanceId"",
                 ""PayloadJson"", ""IdempotencyKey"", ""Status"", ""SubmittedAt"",
                 ""RespondedAt"", ""Priority"", ""LastAttemptAt"",
                 ""RetryCount"", ""TenantId"")
              VALUES (@id, @caseId, @ext,
                      '{}'::jsonb, @key, 'accepted', @now,
                      @now, 0, @now,
                      0, @tenant);", conn))
        {
            subCmd.Parameters.AddWithValue("id", submissionId);
            subCmd.Parameters.AddWithValue("caseId", caseId);
            subCmd.Parameters.AddWithValue("ext", externalInstanceId);
            subCmd.Parameters.AddWithValue("key", $"pa|{tenantId}|{caseId}|{submissionId}");
            subCmd.Parameters.AddWithValue("now", now);
            subCmd.Parameters.AddWithValue("tenant", tenantId);
            await subCmd.ExecuteNonQueryAsync(ct);
        }

        // Optional matching audit event so the dashboard's submission
        // flow can be triaged from the audit log later. Not strictly
        // required by gate.external_system.roundtrip (the data source
        // looks at OutboundSubmission rows, not audit events) but
        // mirrors what production writes.
        await EmitAuditEventAsync(
            tenantId,
            eventType: "nickerp.inspection.submission_dispatched",
            entityType: "OutboundSubmission",
            entityId: submissionId.ToString(),
            payload: new { submissionId, caseId, status = "accepted" },
            ct);
    }

    /// <summary>
    /// Helper #5 — invoke the production
    /// <see cref="MultiTenantInvariantProbe"/> for the supplied tenant
    /// and return the result. The probe runs three sub-checks
    /// (RLS read isolation, system-context register integrity,
    /// cross-tenant export gate refusal); all three must pass for
    /// <c>gate.multi_tenant.invariants</c> to flip Pass. This helper
    /// surfaces the probe directly so a test can assert the sub-check
    /// outcomes; the full gate state still rolls up through
    /// <c>PilotReadinessService.GetReadinessAsync</c>.
    /// </summary>
    public async Task<MultiTenantInvariantProbeResult> RunMultiTenantInvariantProbeAsync(
        long forTenantId,
        CancellationToken ct = default)
    {
        await using var tenancyCtx = BuildTenancyDbContext();
        // Pilot:SourceRoot is unset by default in the test environment,
        // so the system-context-register sub-check records a
        // pass-with-skip-note; that's fine for the gate signal (overall
        // pass requires all three sub-checks, and skip-note counts as
        // pass per the probe's documented contract).
        var probe = new MultiTenantInvariantProbe(
            tenancyCtx,
            TimeProvider.System,
            NullLogger<MultiTenantInvariantProbe>.Instance);
        return await probe.RunAsync(forTenantId, ct);
    }

    /// <summary>
    /// Helper #6 — invoke the production
    /// <see cref="PilotReadinessService"/> for the supplied tenant and
    /// return the report. Wires up the same dependency graph the portal
    /// uses, with one shim — <see cref="EfBackedInspectionPilotProbeDataSource"/>
    /// reads <c>InspectionDbContext</c> directly here rather than going
    /// through DI (the portal-side
    /// <c>InspectionPilotProbeDataSource</c> needs a Blazor service
    /// scope; we don't have one in raw test code). The semantics are
    /// identical — both query the same EF tables.
    /// </summary>
    public async Task<PilotReadinessReport> GetReadinessReportAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        await using var tenancyCtx = BuildTenancyDbContext();
        await using var auditCtx = BuildAuditDbContext();
        await using var inspectionCtx = BuildInspectionDbContext();

        var inspection = new EfBackedInspectionPilotProbeDataSource(inspectionCtx);
        var probe = new MultiTenantInvariantProbe(
            tenancyCtx,
            TimeProvider.System,
            NullLogger<MultiTenantInvariantProbe>.Instance);
        var svc = new PilotReadinessService(
            tenancyCtx, auditCtx, inspection, probe,
            TimeProvider.System,
            NullLogger<PilotReadinessService>.Instance);

        return await svc.GetReadinessAsync(tenantId, ct);
    }

    // ---- internals ----------------------------------------------------------

    /// <summary>
    /// Append one row to <c>audit.events</c> for the supplied tenant.
    /// Mirrors the production
    /// <c>NickERP.Platform.Audit.Database.Services.EventPublisher</c>
    /// shape: server-assigned EventId, deterministic IdempotencyKey
    /// (so duplicate emits dedupe), JSON payload via
    /// <see cref="JsonSerializer"/>. We bypass the publisher to keep
    /// the fixture's dependency surface narrow — the audit row is the
    /// load-bearing artifact and going via publisher would add IBus
    /// wiring noise the test doesn't need.
    /// </summary>
    private async Task<Guid> EmitAuditEventAsync(
        long tenantId,
        string eventType,
        string entityType,
        string entityId,
        object payload,
        CancellationToken ct)
    {
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(payload);
        var idempotencyKey = $"pa|{tenantId}|{eventType}|{entityId}|{eventId:N}";

        await using var conn = new NpgsqlConnection(PlatformConnectionString);
        await conn.OpenAsync(ct);
        await using (var setTenant = new NpgsqlCommand(
            $"SET app.tenant_id = '{tenantId}';", conn))
            await setTenant.ExecuteNonQueryAsync(ct);

        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO audit.events
                (""EventId"", ""TenantId"", ""ActorUserId"", ""CorrelationId"",
                 ""EventType"", ""EntityType"", ""EntityId"", ""Payload"",
                 ""OccurredAt"", ""IngestedAt"", ""IdempotencyKey"",
                 ""PrevEventHash"")
              VALUES (@id, @tenant, NULL, NULL,
                      @evtType, @entType, @entId, @payload::jsonb,
                      @now, @now, @ipk,
                      NULL);", conn);
        cmd.Parameters.AddWithValue("id", eventId);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("evtType", eventType);
        cmd.Parameters.AddWithValue("entType", entityType);
        cmd.Parameters.AddWithValue("entId", entityId);
        cmd.Parameters.AddWithValue("payload", json);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("ipk", idempotencyKey);
        await cmd.ExecuteNonQueryAsync(ct);
        return eventId;
    }

    private TenancyDbContext BuildTenancyDbContext()
    {
        var opts = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(PlatformConnectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new TenancyDbContext(opts);
    }

    private AuditDbContext BuildAuditDbContext()
    {
        var opts = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(PlatformConnectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new AuditDbContext(opts);
    }

    private InspectionDbContext BuildInspectionDbContext()
    {
        var opts = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseNpgsql(InspectionConnectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new InspectionDbContext(opts);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(_adminConnectionString);
            await conn.OpenAsync();

            await TerminateAsync(conn, PlatformDbName);
            await TerminateAsync(conn, InspectionDbName);

            await using (var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{PlatformDbName}\" WITH (FORCE);", conn))
                await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{InspectionDbName}\" WITH (FORCE);", conn))
                await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort teardown — the unique nickerp_e2e_pa_* prefix
            // lets a sweeper drop leftovers offline if a crash leaves
            // them behind.
        }
    }

    private static async Task TerminateAsync(NpgsqlConnection conn, string dbName)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();",
            conn);
        cmd.Parameters.AddWithValue("db", dbName);
        try { await cmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Lightweight EF-backed implementation of
    /// <see cref="IInspectionPilotProbeDataSource"/> the fixture uses
    /// when constructing the <see cref="PilotReadinessService"/>. The
    /// portal's production implementation
    /// (<c>NickERP.Portal.Services.InspectionPilotProbeDataSource</c>)
    /// runs the same queries; we re-implement here rather than take a
    /// project reference on the portal because the E2E tests project
    /// already has Inspection.Database and we want zero coupling to
    /// Blazor service-scope semantics.
    /// </summary>
    private sealed class EfBackedInspectionPilotProbeDataSource : IInspectionPilotProbeDataSource
    {
        private readonly InspectionDbContext _db;

        public EfBackedInspectionPilotProbeDataSource(InspectionDbContext db)
        {
            _db = db;
        }

        public Task<bool> HasDecisionedRealCaseAsync(long tenantId, CancellationToken ct = default)
        {
            return _db.Cases
                .AsNoTracking()
                .Where(c => c.TenantId == tenantId && !c.IsSynthetic)
                .Where(c => _db.Verdicts.Any(v => v.CaseId == c.Id))
                .AnyAsync(ct);
        }

        public Task<bool> HasSuccessfulOutboundSubmissionAsync(long tenantId, CancellationToken ct = default)
        {
            return _db.OutboundSubmissions
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId
                    && s.Status == "accepted"
                    && s.LastAttemptAt != null)
                .AnyAsync(ct);
        }

        public async Task<Guid?> LatestDecisionedRealCaseIdAsync(long tenantId, CancellationToken ct = default)
        {
            return await _db.Cases
                .AsNoTracking()
                .Where(c => c.TenantId == tenantId && !c.IsSynthetic)
                .Where(c => _db.Verdicts.Any(v => v.CaseId == c.Id))
                .OrderByDescending(c => c.OpenedAt)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct);
        }
    }
}
