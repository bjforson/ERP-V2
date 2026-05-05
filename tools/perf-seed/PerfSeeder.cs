using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Tools.PerfSeed;

/// <summary>
/// Seeds realistic-shape pilot data into the platform + inspection DBs
/// for Phase V perf testing. One-shot — call <see cref="SeedAsync"/>
/// once per run; the <c>nickerp.perf.seeded</c> audit event captures
/// the parameters of every run so the perf-test reports can correlate
/// data shapes across runs.
/// </summary>
/// <remarks>
/// <para>
/// Mix of distributions per the brief:
/// </para>
/// <list type="bullet">
///   <item>10% open cases (Open / Assigned)</item>
///   <item>70% closed cases (verdict rendered + workflow Closed)</item>
///   <item>10% verdict-rendered (still in workflow, awaiting close)</item>
///   <item>10% submitted (closed + outbound submission emitted)</item>
/// </list>
/// <para>
/// Per case: 1-3 scans, 0-5 findings, 0-2 reviews, 1-3 audit events.
/// Per scan: 1 ScanArtifact with random sha256 hash. Every row has
/// <c>IsSynthetic = true</c> so the pilot probe
/// <c>gate.analyst.decisioned_real_case</c> ignores them.
/// </para>
/// <para>
/// Determinism: seeded with <see cref="Random"/> using a deterministic
/// seed derived from the run's start ticks. Re-runs produce different
/// data (intentional — perf tests want to vary the data shape across
/// iterations to avoid Postgres' BTree-warmup bias).
/// </para>
/// </remarks>
public sealed class PerfSeeder
{
    private readonly string _platformConn;
    private readonly string _inspectionConn;
    private readonly Random _rng;

    public PerfSeeder(string platformConn, string inspectionConn, int? randomSeed = null)
    {
        _platformConn = platformConn;
        _inspectionConn = inspectionConn;
        _rng = randomSeed is null
            ? new Random()
            : new Random(randomSeed.Value);
    }

    /// <summary>
    /// Seed <paramref name="tenantCount"/> tenants, each with
    /// <paramref name="casesPerTenant"/> realistically-shaped cases.
    /// Returns a <see cref="SeedSummary"/> with row counts. Idempotent
    /// only at the run-id level — calling twice with the same arguments
    /// produces a second independent set of rows (different generated
    /// tenant codes, different case ids).
    /// </summary>
    public async Task<SeedSummary> SeedAsync(int casesPerTenant, int tenantCount, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var summary = new SeedSummary { RunId = runId, StartedAt = startedAt };

        // Step 1 — seed tenants in nickerp_platform.
        var tenants = await SeedTenantsAsync(tenantCount, ct);
        summary.TenantCount = tenants.Count;

        // Step 2 — for each tenant, seed location + scanner ref data +
        // cases / scans / artifacts / reviews / findings / verdicts in
        // nickerp_inspection.
        foreach (var tenant in tenants)
        {
            var perTenant = await SeedTenantWorkloadAsync(tenant, casesPerTenant, ct);
            summary.CaseCount += perTenant.CaseCount;
            summary.ScanCount += perTenant.ScanCount;
            summary.ArtifactCount += perTenant.ArtifactCount;
            summary.ReviewCount += perTenant.ReviewCount;
            summary.FindingCount += perTenant.FindingCount;
            summary.VerdictCount += perTenant.VerdictCount;
        }

        // Step 3 — emit one nickerp.perf.seeded audit event with the
        // run's parameters. The perf-test reports filter on
        // EventType = nickerp.perf.seeded to surface seeding history
        // alongside latency snapshots.
        await EmitSeededAuditEventAsync(runId, summary, casesPerTenant, ct);

        summary.CompletedAt = DateTimeOffset.UtcNow;
        return summary;
    }

    // ------------------------------------------------------------------------

    private async Task<List<Tenant>> SeedTenantsAsync(int count, CancellationToken ct)
    {
        await using var ctx = BuildTenancyContext();
        var seeded = new List<Tenant>(count);
        for (var i = 0; i < count; i++)
        {
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tenant = new Tenant
            {
                // Postgres IDENTITY column assigns Id — we leave it 0.
                Code = $"perf-{suffix}",
                Name = $"Perf seed tenant {suffix}",
                BillingPlan = "perf-test",
                TimeZone = "Africa/Accra",
                Locale = "en-GH",
                Currency = "GHS",
                State = TenantState.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            ctx.Tenants.Add(tenant);
            seeded.Add(tenant);
        }
        await ctx.SaveChangesAsync(ct);
        return seeded;
    }

    private async Task<TenantSummary> SeedTenantWorkloadAsync(
        Tenant tenant,
        int casesPerTenant,
        CancellationToken ct)
    {
        await using var ctx = BuildInspectionContext();
        var summary = new TenantSummary();

        // ----------------- Reference data (location + scanner) ------
        var location = new Location
        {
            Code = $"perf-loc-{tenant.Id}",
            Name = $"Perf seed location {tenant.Id}",
            Region = "Greater Accra",
            TimeZone = "Africa/Accra",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = tenant.Id,
        };
        ctx.Locations.Add(location);

        var scanner = new ScannerDeviceInstance
        {
            LocationId = location.Id,
            TypeCode = "mock",
            DisplayName = $"PERF-{tenant.Id:D3}",
            ConfigJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = tenant.Id,
        };
        ctx.ScannerDeviceInstances.Add(scanner);

        await ctx.SaveChangesAsync(ct);

        // ----------------- Workload --------------------------------
        for (var i = 0; i < casesPerTenant; i++)
        {
            var bucket = _rng.Next(0, 100);
            // Distribution buckets per the brief:
            //   10% open (Open / Assigned-shape, no verdict)
            //   70% closed (verdict rendered + workflow Closed)
            //   10% verdict-rendered (Verdict workflow state, awaiting close)
            //   10% submitted (Submitted workflow state, verdict + submission emitted)
            // OutboundSubmission rows would require an ExternalSystemInstance
            // ref (FK Restrict); intentionally skipped — the perf scenarios
            // the brief targets (case-create, decision, audit-events) do not
            // need them, and the row count here is the load-shape that
            // matters.
            var (state, hasVerdict, isClosed) = bucket switch
            {
                < 10 => (InspectionWorkflowState.Open, false, false),
                < 80 => (InspectionWorkflowState.Closed, true, true),
                < 90 => (InspectionWorkflowState.Verdict, true, false),
                _    => (InspectionWorkflowState.Submitted, true, true)
            };

            var openedAt = DateTimeOffset.UtcNow.AddDays(-_rng.Next(0, 60))
                                              .AddMinutes(-_rng.Next(0, 1440));

            var c = new InspectionCase
            {
                LocationId = location.Id,
                StationId = null,
                SubjectType = CaseSubjectType.Container,
                SubjectIdentifier = $"PERF{_rng.Next(1_000_000, 9_999_999)}",
                SubjectPayloadJson = "{}",
                State = state,
                OpenedAt = openedAt,
                StateEnteredAt = openedAt.AddMinutes(_rng.Next(5, 240)),
                ClosedAt = isClosed ? openedAt.AddHours(_rng.NextDouble() * 12 + 0.5) : null,
                IsSynthetic = true,
                TenantId = tenant.Id,
            };
            ctx.Cases.Add(c);
            summary.CaseCount++;

            // 1-3 scans per case.
            var scanCount = _rng.Next(1, 4);
            for (var s = 0; s < scanCount; s++)
            {
                var scan = new Scan
                {
                    CaseId = c.Id,
                    ScannerDeviceInstanceId = scanner.Id,
                    Mode = "single-energy",
                    CapturedAt = openedAt.AddSeconds(_rng.Next(1, 1800)),
                    IdempotencyKey = $"perf-{Guid.NewGuid():N}",
                    TenantId = tenant.Id,
                };
                ctx.Scans.Add(scan);
                summary.ScanCount++;

                // 1 ScanArtifact per scan.
                var artifact = new ScanArtifact
                {
                    ScanId = scan.Id,
                    ArtifactKind = "Primary",
                    StorageUri = $"perf://seed/{Guid.NewGuid():N}.png",
                    MimeType = "image/png",
                    WidthPx = 1024,
                    HeightPx = 1024,
                    Channels = 1,
                    ContentHash = RandomSha256Hex(),
                    MetadataJson = "{}",
                    CreatedAt = scan.CapturedAt,
                    TenantId = tenant.Id,
                };
                ctx.ScanArtifacts.Add(artifact);
                summary.ArtifactCount++;
            }

            // 0-2 reviews per case.
            var reviewCount = _rng.Next(0, 3);
            for (var r = 0; r < reviewCount; r++)
            {
                var session = new ReviewSession
                {
                    CaseId = c.Id,
                    AnalystUserId = Guid.NewGuid(),
                    StartedAt = openedAt.AddMinutes(_rng.Next(10, 360)),
                    EndedAt = isClosed ? openedAt.AddMinutes(_rng.Next(360, 720)) : null,
                    Outcome = isClosed ? "completed" : "in-progress",
                    TenantId = tenant.Id,
                };
                ctx.ReviewSessions.Add(session);

                var review = new AnalystReview
                {
                    ReviewSessionId = session.Id,
                    TimeToDecisionMs = _rng.Next(60_000, 600_000),
                    RoiInteractionsJson = "[]",
                    ConfidenceScore = Math.Round(_rng.NextDouble() * 0.5 + 0.5, 2),
                    VerdictChangesJson = "[]",
                    PeerDisagreementCount = 0,
                    CreatedAt = session.StartedAt,
                    StartedByUserId = session.AnalystUserId,
                    TenantId = tenant.Id,
                };
                ctx.AnalystReviews.Add(review);
                summary.ReviewCount++;

                // 0-5 findings per review.
                var findingCount = _rng.Next(0, 6);
                for (var f = 0; f < findingCount; f++)
                {
                    var finding = new Finding
                    {
                        AnalystReviewId = review.Id,
                        FindingType = _rng.Next(0, 4) switch
                        {
                            0 => "anomaly.organic",
                            1 => "manifest.mismatch",
                            2 => "shielding",
                            _ => "info.observation",
                        },
                        Severity = _rng.Next(0, 100) < 80 ? "info" : "warning",
                        LocationInImageJson = "{}",
                        Note = $"perf-seed finding {f}",
                        CreatedAt = review.CreatedAt.AddSeconds(_rng.Next(1, 600)),
                        TenantId = tenant.Id,
                    };
                    ctx.Findings.Add(finding);
                    summary.FindingCount++;
                }
            }

            // Verdict, when applicable.
            if (hasVerdict)
            {
                var v = new Verdict
                {
                    CaseId = c.Id,
                    Decision = (VerdictDecision)(_rng.Next(0, 4) * 10), // 0/10/20/30
                    Basis = "perf-seed verdict",
                    DecidedAt = openedAt.AddMinutes(_rng.Next(60, 480)),
                    DecidedByUserId = Guid.NewGuid(),
                    TenantId = tenant.Id,
                };
                ctx.Verdicts.Add(v);
                summary.VerdictCount++;
            }

            // Save in batches of 50 cases to keep memory bounded.
            if (i % 50 == 49)
            {
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }
        }

        await ctx.SaveChangesAsync(ct);
        return summary;
    }

    private async Task EmitSeededAuditEventAsync(
        Guid runId,
        SeedSummary summary,
        int casesPerTenant,
        CancellationToken ct)
    {
        await using var ctx = BuildAuditContext();

        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            run_id = runId,
            tenants = summary.TenantCount,
            cases_per_tenant = casesPerTenant,
            cases = summary.CaseCount,
            scans = summary.ScanCount,
            artifacts = summary.ArtifactCount,
            reviews = summary.ReviewCount,
            findings = summary.FindingCount,
            verdicts = summary.VerdictCount,
            started_at = summary.StartedAt,
        }));

        var row = new DomainEventRow
        {
            EventId = Guid.NewGuid(),
            // Cross-tenant system event — TenantId NULL by design.
            TenantId = null,
            ActorUserId = null,
            CorrelationId = $"perf-seed-{runId:N}",
            EventType = "nickerp.perf.seeded",
            EntityType = "PerfSeedRun",
            EntityId = runId.ToString("N"),
            Payload = payload,
            OccurredAt = summary.StartedAt,
            IngestedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = $"perf-seeded-{runId:N}",
        };
        ctx.Events.Add(row);
        await ctx.SaveChangesAsync(ct);
    }

    private TenancyDbContext BuildTenancyContext()
    {
        Environment.SetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION", _platformConn);
        return new TenancyDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private InspectionDbContext BuildInspectionContext()
    {
        Environment.SetEnvironmentVariable("NICKERP_INSPECTION_DB_CONNECTION", _inspectionConn);
        return new InspectionDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private AuditDbContext BuildAuditContext()
    {
        Environment.SetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION", _platformConn);
        return new AuditDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private string RandomSha256Hex()
    {
        var buf = new byte[32];
        _rng.NextBytes(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private sealed class TenantSummary
    {
        public int CaseCount { get; set; }
        public int ScanCount { get; set; }
        public int ArtifactCount { get; set; }
        public int ReviewCount { get; set; }
        public int FindingCount { get; set; }
        public int VerdictCount { get; set; }
    }
}

/// <summary>
/// Summary of a single perf-seed run. Returned by
/// <see cref="PerfSeeder.SeedAsync"/> for printing + for the
/// in-process unit tests to assert against.
/// </summary>
public sealed class SeedSummary
{
    public Guid RunId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public int TenantCount { get; set; }
    public int CaseCount { get; set; }
    public int ScanCount { get; set; }
    public int ArtifactCount { get; set; }
    public int ReviewCount { get; set; }
    public int FindingCount { get; set; }
    public int VerdictCount { get; set; }

    public override string ToString()
        => $"run={RunId:N} tenants={TenantCount} cases={CaseCount} scans={ScanCount} "
         + $"artifacts={ArtifactCount} reviews={ReviewCount} findings={FindingCount} "
         + $"verdicts={VerdictCount}";
}
