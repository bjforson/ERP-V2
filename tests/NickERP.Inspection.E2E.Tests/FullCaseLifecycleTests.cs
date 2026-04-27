using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy;
using Npgsql;

namespace NickERP.Inspection.E2E.Tests;

/// <summary>
/// Sprint D4 — End-to-End demo smoke test. Drives one container scan
/// from raw FS6000 byte-drop all the way to a closed case with a
/// verdict submitted to the ICUMS outbox, asserting every checkpoint
/// the demo loop is supposed to clear:
///
/// <list type="number">
///   <item>The <c>ScannerIngestionWorker</c> picks up the dropped triplet
///         and creates a case keyed to the file stem.</item>
///   <item>The <c>PreRenderWorker</c> renders the thumbnail (and
///         eventually the preview) derivatives.</item>
///   <item><c>FetchDocumentsAsync</c> indexes the BOE drop folder, finds
///         a document keyed to the same stem, and auto-fires the
///         authority rules pack (D3 wiring).</item>
///   <item>The Tema-port BOE doesn't trip <c>GH-PORT-MATCH</c>.</item>
///   <item>Verdict submission writes a JSON file to the ICUMS outbox.</item>
///   <item>Eight or more <c>nickerp.inspection.*</c> events land on
///         <c>audit.events</c>.</item>
///   <item>RLS actually filters: a non-superuser connection without
///         <c>app.tenant_id</c> sees zero rows; with it, sees the case.</item>
/// </list>
///
/// Marked <c>Category=Integration</c> so unit-test runs (filter
/// <c>Category!=Integration</c>) skip it, preserving F2's <5s unit-test
/// runtime AC.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FullCaseLifecycleTests
{
    private const string ContainerNumber = "MSCU8675309";

    [Fact]
    public async Task FullCaseLifecycle_FromFs6000Drop_ToVerdictSubmission()
    {
        // Two-minute budget per the D4 AC. The hot path uses the host's
        // first ScannerIngestionWorker discovery cycle (which runs
        // immediately at startup, not after the 60s DiscoveryInterval
        // sleep), so we have to seed the device row + drop the triplet
        // BEFORE invoking factory.Server. That keeps the test under
        // budget without modifying the worker.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // ----- 1. Stand up the test databases -------------------------
        await using var db = await PostgresFixture.CreateAsync(ct);

        // ----- 2. Stage the temp directory tree ----------------------
        var rootTemp = Path.Combine(
            Path.GetTempPath(),
            "nickerp-e2e-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var watchPath = Path.Combine(rootTemp, "fs6000-watch");
        var icumsDrop = Path.Combine(rootTemp, "icums-drop");
        var icumsOutbox = Path.Combine(rootTemp, "icums-outbox");
        var imagingStore = Path.Combine(rootTemp, "imaging-store");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(icumsDrop);
        Directory.CreateDirectory(icumsOutbox);
        Directory.CreateDirectory(imagingStore);

        try
        {
            var fakeUserId = Guid.NewGuid();

            // ----- 3. Apply migrations + seed BEFORE the host starts --
            // The ScannerIngestionWorker runs its first discovery cycle
            // immediately on startup; if the device row isn't there yet,
            // the test waits the full 60s DiscoveryInterval for the next
            // cycle and the 2-minute D4 budget gets very tight. Apply
            // schema standalone, seed configuration rows, drop fixtures,
            // THEN boot the host.
            await TestSchemaApplier.ApplyAllAsync(
                db.PlatformConnectionString,
                db.InspectionConnectionString,
                ct);

            // Seed an IdentityUser for the dev-bypass path. We don't
            // exercise dev-bypass over HTTP in this test — the workflow
            // service runs through a stub AuthenticationStateProvider —
            // but DbIdentityResolver still does a user lookup if any
            // request happens to come through, and it fails noisily if
            // the row's missing.
            await SeedIdentityUserAsync(db.PlatformConnectionString, fakeUserId, ct);

            // Seed location, scanner instance, external-system instance
            // directly via Npgsql so we don't have to wire up a fresh
            // InspectionDbContext at this stage (the host owns one and
            // we'll use it from the test scopes after startup).
            Guid locationId, scannerInstanceId, externalSystemInstanceId;
            (locationId, scannerInstanceId, externalSystemInstanceId) =
                await SeedInspectionInstancesAsync(
                    db.InspectionConnectionString,
                    watchPath,
                    icumsDrop,
                    icumsOutbox,
                    ct);

            // RLS role grants must come AFTER migrations apply (the
            // schema must exist) but can be done either before or after
            // the host starts; doing it now keeps the assertion at
            // step 11 dependency-free.
            await db.PrepareRlsRoleAsync(ct);

            // ----- 4. Drop fixtures into the watch / drop folders ----
            E2EFixtures.WriteFs6000Triplet(watchPath, ContainerNumber);
            E2EFixtures.WriteIcumsBatchForContainer(icumsDrop, ContainerNumber);

            // ----- 5. Boot the host ----------------------------------
            var contentRoot = AppContext.BaseDirectory;
            await using var factory = new E2EWebApplicationFactory(
                platformConnectionString: db.PlatformConnectionString,
                inspectionConnectionString: db.InspectionConnectionString,
                imagingStorageRoot: imagingStore,
                contentRoot: contentRoot,
                fakeUserId: fakeUserId);

            // Force the host to fully boot. The migrations-at-startup
            // path is a no-op now (schema already applied) but
            // RunMigrationsOnStartup stays on so the path is still
            // exercised end-to-end.
            _ = factory.Server;

            // ----- 7. Wait for ScannerIngestionWorker to open a case -
            // Discovery interval is 60s in the worker; the test budget
            // gives it a generous 90s ceiling to first-pass + ingest.
            var caseId = await WaitForAsync(async () =>
            {
                using var scope = factory.CreateTenantScope();
                var inspection = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
                var c = await inspection.Cases
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.SubjectIdentifier == ContainerNumber, ct);
                return c?.Id;
            }, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1), ct,
            because: "ScannerIngestionWorker must open a case keyed to the dropped triplet stem");

            using (var scope = factory.CreateTenantScope())
            {
                var inspection = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
                var caseRow = await inspection.Cases.AsNoTracking().FirstAsync(x => x.Id == caseId, ct);
                caseRow.LocationId.Should().Be(locationId, because: "case is keyed to the FS6000 instance's location");
                caseRow.SubjectIdentifier.Should().Be(ContainerNumber);
                caseRow.State.Should().BeOneOf(
                    InspectionWorkflowState.Open,
                    InspectionWorkflowState.Validated);
                (DateTimeOffset.UtcNow - caseRow.OpenedAt).Should().BeLessThan(TimeSpan.FromMinutes(2));
            }

            // ----- 8. Wait for the thumbnail render --------------------
            // Render rows live on `scan_render_artifacts`, keyed by the
            // owning ScanArtifact id (not Scan id). The join fans out
            // ScanRenderArtifact → ScanArtifact → Scan → Case so we can
            // ask "does any thumbnail row exist for an artifact whose
            // scan belongs to the e2e case?". 60s budget covers the worst
            // case of one full PreRenderWorker poll cycle plus an
            // ImageSharp render (FS6000 PNG decode + 256px resize).
            await WaitForAsync(async () =>
            {
                using var scope = factory.CreateTenantScope();
                var inspection = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
                var hasThumb = await (
                    from r in inspection.ScanRenderArtifacts.AsNoTracking()
                    join a in inspection.ScanArtifacts.AsNoTracking()
                        on r.ScanArtifactId equals a.Id
                    join s in inspection.Scans.AsNoTracking()
                        on a.ScanId equals s.Id
                    where s.CaseId == caseId && r.Kind == "thumbnail"
                    select r.Id
                ).AnyAsync(ct);
                return hasThumb ? (object?)true : null;
            }, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(1), ct,
            because: "PreRenderWorker must produce a thumbnail derivative for the ingested scan");

            // ----- 9. Drive the post-ingest workflow -------------------
            using (var scope = factory.CreateTenantScope())
            {
                var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();

                // 9a. Fetch documents — D3 wiring should auto-fire rules.
                var fetchResult = await workflow.FetchDocumentsAsync(
                    caseId, externalSystemInstanceId, ct);
                fetchResult.Documents.Count.Should().BeGreaterThanOrEqualTo(1,
                    because: "the BOE batch JSON contains one BOE keyed to this container");
                fetchResult.Documents[0].DocumentType.Should().Be("BOE");
                fetchResult.Rules.Should().NotBeNull(
                    because: "D3 auto-fires EvaluateAuthorityRulesAsync after a successful fetch");
                fetchResult.Rules!.Violations
                    .Should().NotContain(v =>
                        v.Violation.RuleCode == "GH-PORT-MATCH"
                        && v.Violation.Severity == "Error",
                    because: "the test BOE's DeliveryPlace 'WTTMA1MPS3' resolves to TMA, matching tema");

                // 9b. Assign self + start review.
                var session = await workflow.AssignSelfAndStartReviewAsync(caseId, ct);
                session.AnalystUserId.Should().Be(factory.FakeUserId);

                // 9c. Set verdict.
                var verdict = await workflow.SetVerdictAsync(
                    caseId, VerdictDecision.Clear, "e2e-clear", confidence: 0.9, ct);
                verdict.Decision.Should().Be(VerdictDecision.Clear);

                // 9d. Submit — adapter writes a JSON to the outbox.
                var submission = await workflow.SubmitAsync(
                    caseId, externalSystemInstanceId, ct);
                submission.Status.Should().Be("accepted",
                    because: "the icums-gh outbox adapter returns Accepted=true on a successful file write");

                var outboxFiles = Directory.EnumerateFiles(icumsOutbox, "*.json").ToList();
                outboxFiles.Should().NotBeEmpty(
                    because: "icums-gh.SubmitAsync writes one JSON per IdempotencyKey");
                outboxFiles.Should().Contain(
                    p => Path.GetFileNameWithoutExtension(p)
                         .Contains(submission.IdempotencyKey, StringComparison.Ordinal),
                    because: "the outbox filename is the submission's IdempotencyKey");

                // 9e. Close the case.
                await workflow.CloseCaseAsync(caseId, ct);
            }

            // ----- 10. Audit assertion ---------------------------------
            using (var scope = factory.CreateTenantScope())
            {
                var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
                var events = await audit.Events
                    .AsNoTracking()
                    .Where(e => e.EventType.StartsWith("nickerp.inspection."))
                    .OrderBy(e => e.OccurredAt)
                    .Select(e => e.EventType)
                    .ToListAsync(ct);

                events.Count.Should().BeGreaterThanOrEqualTo(8,
                    because: "the demo loop must emit ≥8 inspection events: "
                             + "case_opened, scan_recorded, document_fetched, "
                             + "case_validated, rules_evaluated, case_assigned, "
                             + "verdict_set, submission_dispatched, case_closed");
                events.Should().Contain("nickerp.inspection.case_opened");
                events.Should().Contain("nickerp.inspection.scan_recorded");
                events.Should().Contain("nickerp.inspection.document_fetched");
                events.Should().Contain("nickerp.inspection.case_validated");
                events.Should().Contain("nickerp.inspection.rules_evaluated");
                events.Should().Contain("nickerp.inspection.case_assigned");
                events.Should().Contain("nickerp.inspection.verdict_set");
                events.Should().Contain("nickerp.inspection.submission_dispatched");
                events.Should().Contain("nickerp.inspection.case_closed");
            }

            // ----- 11. RLS sanity --------------------------------------
            // The default postgres role has BYPASSRLS, so we connect as
            // the per-test NOSUPERUSER NOBYPASSRLS role created in the
            // PostgresFixture. Without app.tenant_id pushed, the policy
            // sees TenantId vs '0' and returns no rows; with the right
            // tenant pushed, the case row reappears.
            await AssertRlsAsync(db.InspectionRlsConnectionString, ct);
        }
        finally
        {
            try { Directory.Delete(rootTemp, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Poll <paramref name="probe"/> until it returns a non-null value
    /// or <paramref name="timeout"/> elapses, sleeping
    /// <paramref name="pollInterval"/> between attempts. Returns the
    /// non-null value (handy when the probe also surfaces an id).
    /// </summary>
    private static async Task<T> WaitForAsync<T>(
        Func<Task<T?>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct,
        string? because = null)
        where T : struct
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await probe();
                if (result is not null) return result.Value;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            await Task.Delay(pollInterval, ct);
        }

        var msg = $"Timed out after {timeout.TotalSeconds:0.#}s waiting for probe to return a value"
                  + (because is null ? "." : $" — {because}.");
        if (lastError is not null) throw new TimeoutException(msg, lastError);
        throw new TimeoutException(msg);
    }

    private static async Task<object> WaitForAsync(
        Func<Task<object?>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct,
        string? because = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await probe();
                if (result is not null) return result;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            await Task.Delay(pollInterval, ct);
        }

        var msg = $"Timed out after {timeout.TotalSeconds:0.#}s waiting for probe to return a value"
                  + (because is null ? "." : $" — {because}.");
        if (lastError is not null) throw new TimeoutException(msg, lastError);
        throw new TimeoutException(msg);
    }

    /// <summary>
    /// Seed location, scanner instance, external-system instance for
    /// tenant 1. Returns the freshly-minted ids. Goes via raw Npgsql so
    /// we don't have to wire up an InspectionDbContext outside the host's
    /// service scope.
    /// </summary>
    private static async Task<(Guid LocationId, Guid ScannerId, Guid ExternalId)>
        SeedInspectionInstancesAsync(
            string connectionString,
            string watchPath,
            string icumsDrop,
            string icumsOutbox,
            CancellationToken ct)
    {
        var locationId = Guid.NewGuid();
        var scannerId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("SET app.tenant_id = '1';", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        // Locations
        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO inspection.locations
                (""Id"", ""Code"", ""Name"", ""Region"", ""TimeZone"",
                 ""IsActive"", ""CreatedAt"", ""TenantId"")
              VALUES (@id, 'tema', 'Tema Port', 'Greater Accra', 'Africa/Accra',
                      true, @now, 1);", conn))
        {
            cmd.Parameters.AddWithValue("id", locationId);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Scanner instance — config carries WatchPath + a 1s poll so the
        // FS6000 adapter picks the dropped triplet up on its first pass.
        var scannerCfg = $"{{\"WatchPath\":\"{watchPath.Replace("\\", "\\\\")}\",\"PollIntervalSeconds\":1}}";
        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO inspection.scanner_device_instances
                (""Id"", ""LocationId"", ""StationId"", ""TypeCode"", ""DisplayName"",
                 ""Description"", ""ConfigJson"", ""IsActive"", ""CreatedAt"", ""TenantId"")
              VALUES (@id, @locId, NULL, 'fs6000', 'E2E FS6000',
                      'End-to-end test scanner', @cfg::jsonb, true, @now, 1);", conn))
        {
            cmd.Parameters.AddWithValue("id", scannerId);
            cmd.Parameters.AddWithValue("locId", locationId);
            cmd.Parameters.AddWithValue("cfg", scannerCfg);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // External system — icums-gh adapter, file-based drop + outbox.
        var externalCfg =
            $"{{\"BatchDropPath\":\"{icumsDrop.Replace("\\", "\\\\")}\","
            + $"\"OutboxPath\":\"{icumsOutbox.Replace("\\", "\\\\")}\","
            + "\"CacheTtlSeconds\":1}";
        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO inspection.external_system_instances
                (""Id"", ""TypeCode"", ""DisplayName"", ""Description"", ""Scope"",
                 ""ConfigJson"", ""IsActive"", ""CreatedAt"", ""TenantId"")
              VALUES (@id, 'icums-gh', 'E2E ICUMS', NULL, 0,
                      @cfg::jsonb, true, @now, 1);", conn))
        {
            cmd.Parameters.AddWithValue("id", externalId);
            cmd.Parameters.AddWithValue("cfg", externalCfg);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return (locationId, scannerId, externalId);
    }

    /// <summary>
    /// Seed the dev-bypass user. Direct ADO so we don't need to register
    /// a second IdentityDbContext-as-postgres connection from the test.
    /// </summary>
    private static async Task SeedIdentityUserAsync(
        string platformConnectionString,
        Guid userId,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(platformConnectionString);
        await conn.OpenAsync(ct);
        // Match the host's tenant context so the RLS policies on
        // identity.identity_users (added in the F1 migration) admit the
        // INSERT.
        await using (var setTenant = new NpgsqlCommand("SET app.tenant_id = '1';", conn))
            await setTenant.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO identity.identity_users
                (""Id"", ""Email"", ""NormalizedEmail"", ""DisplayName"", ""IsActive"",
                 ""CreatedAt"", ""UpdatedAt"", ""TenantId"")
              VALUES (@id, @email, @nemail, @display, true, @now, @now, 1);",
            conn);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("email", "dev@nickscan.com");
        cmd.Parameters.AddWithValue("nemail", "DEV@NICKSCAN.COM");
        cmd.Parameters.AddWithValue("display", "E2E Local Dev");
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AssertRlsAsync(string rlsConnectionString, CancellationToken ct)
    {
        await using (var conn = new NpgsqlConnection(rlsConnectionString))
        {
            await conn.OpenAsync(ct);
            // No app.tenant_id set => policy compares TenantId to '0' and excludes everything.
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM inspection.cases;", conn);
            var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
            count.Should().Be(0,
                because: "RLS must filter all rows when app.tenant_id is unset");
        }

        await using (var conn = new NpgsqlConnection(rlsConnectionString))
        {
            await conn.OpenAsync(ct);
            await using (var setTenant = new NpgsqlCommand("SET app.tenant_id = '1';", conn))
                await setTenant.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM inspection.cases;", conn);
            var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
            count.Should().BeGreaterThanOrEqualTo(1,
                because: "the test seeded one case under tenant 1");
        }
    }
}
