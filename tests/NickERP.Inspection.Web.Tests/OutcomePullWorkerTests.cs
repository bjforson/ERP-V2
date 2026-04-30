using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.PostHocOutcomes;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 13 / §6.11 — integration tests for <see cref="OutcomePullWorker"/>
/// + <see cref="PostHocOutcomeWriter"/> using the EF InMemory provider.
///
/// <para>
/// Coverage:
/// <list type="bullet">
/// <item>Worker pulls from a mock adapter, advances cursor, persists the
/// fetched outcomes (Active phase).</item>
/// <item>Shadow phase persists the AuthorityDocument row but does NOT
/// update <see cref="AnalystReview.PostHocOutcomeJson"/>.</item>
/// <item>Active phase (Phase 2) updates AnalystReview.</item>
/// <item>Authoritative phase (Phase 3): supersession appends to
/// <c>supersedes_chain</c> and the analyst review's normalised JSON
/// is rewritten to the latest non-superseded entry.</item>
/// <item>Cursor advances on a clean batch and stays put on a writer
/// failure (so the next cycle replays the same window).</item>
/// <item>Adapter failure on FetchOutcomesAsync leaves the cursor + the
/// AuthorityDocument table unchanged.</item>
/// </list>
/// </para>
/// </summary>
public sealed class OutcomePullWorkerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _reviewSessionId = Guid.NewGuid();
    private readonly Guid _analystReviewId = Guid.NewGuid();
    private readonly long _tenantId = 1;
    private readonly RecordingOutcomeAdapter _adapter = new();

    public OutcomePullWorkerTests()
    {
        var dbName = "outcome-pull-" + Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase("tenancy-" + dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddScoped<IPostHocOutcomeWriter, PostHocOutcomeWriter>();

        services.AddSingleton(_adapter);
        services.AddSingleton<IPluginRegistry>(sp => new SinglePluginRegistry(_adapter));

        services.Configure<OutcomeIngestionOptions>(o =>
        {
            o.Enabled = true;
            o.PullInterval = TimeSpan.FromMinutes(30);
            o.WindowOverlap = TimeSpan.FromHours(24);
            o.SkewBuffer = TimeSpan.FromMinutes(5);
            o.StartupDelay = TimeSpan.Zero;
        });

        services.AddSingleton<OutcomePullWorker>(sp => new OutcomePullWorker(
            sp,
            sp.GetRequiredService<IOptions<OutcomeIngestionOptions>>(),
            NullLogger<OutcomePullWorker>.Instance));

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    // ----- Test 1: Active phase — adapter-emitted outcome lands in DB + AnalystReview ----

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DrainOnce_ActivePhase_PersistsOutcomeAndUpdatesAnalystReview()
    {
        await SeedTenantAndCaseAsync();
        await SeedPhaseAndCursorAsync(PostHocRolloutPhaseValue.PrimaryPlus5PctAudit, lastWindowUntil: DateTimeOffset.UtcNow.AddHours(-2));
        _adapter.NextOutcomes = new[]
        {
            BuildAdapterDto("DECL-001", "REF-001", new DateTimeOffset(2026, 4, 26, 13, 11, 0, TimeSpan.Zero), "Seized")
        };

        var worker = _sp.GetRequiredService<OutcomePullWorker>();
        var pulled = await InvokeDrainOnceAsync(worker);

        Assert.Equal(1, pulled);

        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        // Document persisted
        var docs = await db.AuthorityDocuments.AsNoTracking().ToListAsync();
        var posthoc = Assert.Single(docs);
        Assert.Equal("PostHocOutcome", posthoc.DocumentType);
        Assert.Equal("REF-001", posthoc.ReferenceNumber);

        // PayloadJson stamped with idempotency_key + posthoc_phase
        var payload = JsonNode.Parse(posthoc.PayloadJson) as JsonObject;
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrEmpty(payload!["idempotency_key"]?.GetValue<string>()));
        Assert.Equal("primary-plus-5pct-audit", payload["posthoc_phase"]?.GetValue<string>());

        // Analyst review updated (active phase emits training signal).
        // Resolve via case -> ReviewSession -> AnalystReview so the
        // assertion targets the right row even when the seed has many.
        var caseT1 = await db.Cases.AsNoTracking().FirstAsync(c => c.SubjectIdentifier == "DECL-001");
        var review = await (
            from rs in db.ReviewSessions.AsNoTracking()
            join rev in db.AnalystReviews.AsNoTracking() on rs.Id equals rev.ReviewSessionId
            where rs.CaseId == caseT1.Id
            select rev).FirstAsync();
        Assert.False(string.IsNullOrEmpty(review.PostHocOutcomeJson));
        var reviewPayload = JsonNode.Parse(review.PostHocOutcomeJson!) as JsonObject;
        Assert.Equal("Seized", reviewPayload!["outcome"]?.GetValue<string>());
        Assert.Equal("REF-001", reviewPayload["decision_reference"]?.GetValue<string>());

        // Cursor advanced
        var cursor = await db.OutcomePullCursors.AsNoTracking().FirstAsync();
        Assert.Equal(0, cursor.ConsecutiveFailures);
        Assert.True(cursor.LastSuccessfulPullAt > DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    // ----- Test 2: Shadow phase — persisted but does NOT touch AnalystReview ----

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DrainOnce_ShadowPhase_PersistsOutcomeButDoesNotUpdateAnalystReview()
    {
        await SeedTenantAndCaseAsync();
        await SeedPhaseAndCursorAsync(PostHocRolloutPhaseValue.Shadow, lastWindowUntil: DateTimeOffset.UtcNow.AddHours(-2));
        _adapter.NextOutcomes = new[]
        {
            BuildAdapterDto("DECL-002", "REF-002", new DateTimeOffset(2026, 4, 26, 14, 0, 0, TimeSpan.Zero), "Cleared")
        };

        var worker = _sp.GetRequiredService<OutcomePullWorker>();
        await InvokeDrainOnceAsync(worker);

        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        // Document persisted with phase=shadow
        var doc = await db.AuthorityDocuments.AsNoTracking().FirstAsync();
        var payload = JsonNode.Parse(doc.PayloadJson) as JsonObject;
        Assert.Equal("shadow", payload!["posthoc_phase"]?.GetValue<string>());

        // Analyst review NOT touched — Shadow phase persists-only.
        // Resolve the right review via case -> session -> review so the
        // assertion targets the case-under-test rather than relying on
        // FirstAsync's insertion-order behaviour.
        var caseT2 = await db.Cases.AsNoTracking().FirstAsync(c => c.SubjectIdentifier == "DECL-002");
        var review = await (
            from rs in db.ReviewSessions.AsNoTracking()
            join rev in db.AnalystReviews.AsNoTracking() on rs.Id equals rev.ReviewSessionId
            where rs.CaseId == caseT2.Id
            select rev).FirstAsync();
        Assert.True(string.IsNullOrEmpty(review.PostHocOutcomeJson));
    }

    // ----- Test 3: Authoritative (Primary, phase 3) — supersession appends + override ----

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DrainOnce_PrimaryPhase_SupersessionOverridesPriorClassification()
    {
        await SeedTenantAndCaseAsync();
        await SeedPhaseAndCursorAsync(PostHocRolloutPhaseValue.Primary, lastWindowUntil: DateTimeOffset.UtcNow.AddHours(-2));

        // First cycle — initial Cleared verdict.
        _adapter.NextOutcomes = new[]
        {
            BuildAdapterDto("DECL-003", "REF-CLEAR", new DateTimeOffset(2026, 4, 26, 10, 0, 0, TimeSpan.Zero), "Cleared")
        };
        var worker = _sp.GetRequiredService<OutcomePullWorker>();
        await InvokeDrainOnceAsync(worker);

        // Second cycle — correction from Cleared to Seized supersedes the prior decision.
        _adapter.NextOutcomes = new[]
        {
            BuildAdapterDto("DECL-003", "REF-SEIZE", new DateTimeOffset(2026, 4, 27, 16, 0, 0, TimeSpan.Zero), "Seized",
                supersedesDecisionReference: "REF-CLEAR")
        };
        await InvokeDrainOnceAsync(worker);

        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        // Both documents preserved; supersession is append-only per §6.11.7
        var docs = await db.AuthorityDocuments.AsNoTracking().OrderBy(d => d.ReceivedAt).ToListAsync();
        Assert.Equal(2, docs.Count);
        Assert.Equal("REF-CLEAR", docs[0].ReferenceNumber);
        Assert.Equal("REF-SEIZE", docs[1].ReferenceNumber);

        // The newer payload carries supersedes_chain + supersedes_document_id
        var newer = JsonNode.Parse(docs[1].PayloadJson) as JsonObject;
        Assert.Equal(docs[0].Id.ToString(), newer!["supersedes_document_id"]?.GetValue<string>());
        var chain = newer["supersedes_chain"] as JsonArray;
        Assert.NotNull(chain);
        Assert.Single(chain!);

        // AnalystReview's PostHocOutcomeJson now reflects the SEIZED supersession (override)
        // Find the analyst review attached to the case under test (DECL-003).
        var caseUnderTest = await db.Cases.AsNoTracking()
            .FirstAsync(c => c.SubjectIdentifier == "DECL-003");
        var review = await (
            from rs in db.ReviewSessions.AsNoTracking()
            join rev in db.AnalystReviews.AsNoTracking() on rs.Id equals rev.ReviewSessionId
            where rs.CaseId == caseUnderTest.Id
            select rev).FirstAsync();
        Assert.False(string.IsNullOrEmpty(review.PostHocOutcomeJson));
        var reviewPayload = JsonNode.Parse(review.PostHocOutcomeJson!) as JsonObject;
        Assert.Equal("Seized", reviewPayload!["outcome"]?.GetValue<string>());
        Assert.Equal(docs[1].Id.ToString(), reviewPayload["document_id"]?.GetValue<string>());
        var reviewChain = reviewPayload["supersedes_chain"] as JsonArray;
        Assert.Single(reviewChain!);
    }

    // ----- Test 4: Adapter throws — cursor unchanged, no rows persisted ----

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DrainOnce_AdapterFails_LeavesCursorUnchangedAndDoesNotPersist()
    {
        await SeedTenantAndCaseAsync();
        var origUntil = DateTimeOffset.UtcNow.AddHours(-2);
        await SeedPhaseAndCursorAsync(PostHocRolloutPhaseValue.PrimaryPlus5PctAudit, lastWindowUntil: origUntil);

        _adapter.ShouldThrow = true;
        _adapter.NextOutcomes = Array.Empty<AuthorityDocumentDto>();

        var worker = _sp.GetRequiredService<OutcomePullWorker>();
        await InvokeDrainOnceAsync(worker);

        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var docs = await db.AuthorityDocuments.AsNoTracking().ToListAsync();
        Assert.Empty(docs);

        var cursor = await db.OutcomePullCursors.AsNoTracking().FirstAsync();
        Assert.Equal(origUntil, cursor.LastPullWindowUntil);
    }

    // ----- Test 5: DevEval-only phase — worker skips, no pull invocation ----

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DrainOnce_DevEvalManualOnlyPhase_AdapterIsNotCalled()
    {
        await SeedTenantAndCaseAsync();
        await SeedPhaseAndCursorAsync(PostHocRolloutPhaseValue.DevEvalManualOnly, lastWindowUntil: DateTimeOffset.UtcNow.AddHours(-2));
        _adapter.NextOutcomes = new[]
        {
            BuildAdapterDto("DECL-004", "REF-004", DateTimeOffset.UtcNow, "Seized")
        };

        var worker = _sp.GetRequiredService<OutcomePullWorker>();
        await InvokeDrainOnceAsync(worker);

        Assert.Equal(0, _adapter.FetchInvocationCount);

        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        Assert.Empty(await db.AuthorityDocuments.AsNoTracking().ToListAsync());
    }

    // ----- Test 6: Idempotent dedup — same outcome twice returns Deduplicated ----

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DrainOnce_DuplicateOutcome_DedupsViaIdempotencyKey()
    {
        await SeedTenantAndCaseAsync();
        await SeedPhaseAndCursorAsync(PostHocRolloutPhaseValue.PrimaryPlus5PctAudit, lastWindowUntil: DateTimeOffset.UtcNow.AddHours(-2));

        var dto = BuildAdapterDto("DECL-005", "REF-005",
            new DateTimeOffset(2026, 4, 26, 13, 11, 0, TimeSpan.Zero), "Seized");

        // First cycle: insert
        _adapter.NextOutcomes = new[] { dto };
        var worker = _sp.GetRequiredService<OutcomePullWorker>();
        await InvokeDrainOnceAsync(worker);

        // Second cycle: same outcome. Writer dedups; only one row persists.
        _adapter.NextOutcomes = new[] { dto };
        await InvokeDrainOnceAsync(worker);

        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var docs = await db.AuthorityDocuments.AsNoTracking().ToListAsync();
        Assert.Single(docs);
    }

    // ----- helpers ---------------------------------------------------

    private async Task SeedTenantAndCaseAsync()
    {
        using var scope = _sp.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        tenancy.Tenants.Add(new Tenant
        {
            Id = _tenantId,
            Code = "t1",
            Name = "Test Tenant",
            IsActive = true,
            BillingPlan = "internal",
            TimeZone = "UTC",
            Locale = "en",
            Currency = "USD",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await tenancy.SaveChangesAsync();

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        // Seed a Location, Case, ReviewSession, AnalystReview so the
        // case-lookup + analyst-review-update paths have data to work with.
        var locationId = Guid.NewGuid();
        db.Locations.Add(new Location
        {
            Id = locationId,
            Code = "loc1",
            Name = "Loc 1",
            TimeZone = "UTC",
            IsActive = true,
            TenantId = _tenantId
        });

        // Seed an external system instance so the FK on
        // outcome_pull_cursors / posthoc_rollout_phase resolves.
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = _instanceId,
            TypeCode = "test-authority",
            DisplayName = "Test Authority",
            Description = "Test stub",
            Scope = ExternalSystemBindingScope.Shared,
            ConfigJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });

        // Use one of the adapter's outcome DECL- prefixes — but we want
        // the case to match for ANY of them, so SubjectIdentifier needs
        // the prefix wildcard. Tests build DTOs with declaration_number
        // = "DECL-NNN"; the writer's case lookup uses
        // SubjectIdentifier == declaration_number as the second path.
        // Seed multiple cases (one per test's DECL-x) so each test
        // matches without conflict.
        for (var i = 1; i <= 9; i++)
        {
            var caseId = i == 1 ? _caseId : Guid.NewGuid();
            db.Cases.Add(new InspectionCase
            {
                Id = caseId,
                LocationId = locationId,
                SubjectType = CaseSubjectType.Container,
                SubjectIdentifier = $"DECL-00{i}",
                SubjectPayloadJson = "{}",
                State = InspectionWorkflowState.Open,
                OpenedAt = DateTimeOffset.UtcNow.AddDays(-i),
                StateEnteredAt = DateTimeOffset.UtcNow.AddDays(-i),
                TenantId = _tenantId
            });

            var sessionId = Guid.NewGuid();
            db.ReviewSessions.Add(new ReviewSession
            {
                Id = sessionId,
                CaseId = caseId,
                AnalystUserId = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow.AddDays(-i).AddHours(1),
                Outcome = "in-progress",
                TenantId = _tenantId
            });
            db.AnalystReviews.Add(new AnalystReview
            {
                Id = i == 1 ? _analystReviewId : Guid.NewGuid(),
                ReviewSessionId = sessionId,
                TimeToDecisionMs = 1000,
                RoiInteractionsJson = "[]",
                ConfidenceScore = 0.8,
                VerdictChangesJson = "[]",
                PeerDisagreementCount = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-i).AddHours(2),
                TenantId = _tenantId
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedPhaseAndCursorAsync(
        PostHocRolloutPhaseValue phase, DateTimeOffset lastWindowUntil)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        db.PostHocRolloutPhases.Add(new PostHocRolloutPhase
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExternalSystemInstanceId = _instanceId,
            CurrentPhase = phase,
            PhaseEnteredAt = DateTimeOffset.UtcNow.AddDays(-7),
            GateNotesJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-7)
        });

        db.OutcomePullCursors.Add(new OutcomePullCursor
        {
            ExternalSystemInstanceId = _instanceId,
            TenantId = _tenantId,
            LastSuccessfulPullAt = lastWindowUntil,
            LastPullWindowUntil = lastWindowUntil,
            ConsecutiveFailures = 0
        });

        await db.SaveChangesAsync();
    }

    private static AuthorityDocumentDto BuildAdapterDto(
        string declarationNumber, string decisionReference, DateTimeOffset decidedAt,
        string outcome, string? supersedesDecisionReference = null)
    {
        var payload = new JsonObject
        {
            ["$schema"] = "test.posthoc-outcome.v1",
            ["declaration_number"] = declarationNumber,
            ["container_id"] = "MSCU0000001",
            ["outcome"] = outcome,
            ["decided_at"] = decidedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["decision_reference"] = decisionReference,
            ["supersedes_decision_reference"] = supersedesDecisionReference,
            ["entry_method"] = "api"
        };
        return new AuthorityDocumentDto(
            InstanceId: Guid.Empty, // worker fills via cfg.InstanceId in the persistence path
            DocumentType: "PostHocOutcome",
            ReferenceNumber: decisionReference,
            ReceivedAt: DateTimeOffset.UtcNow,
            PayloadJson: payload.ToJsonString());
    }

    /// <summary>Reflection helper — DrainOnceAsync is internal to the worker.</summary>
    private static async Task<int> InvokeDrainOnceAsync(OutcomePullWorker worker)
    {
        var method = typeof(OutcomePullWorker).GetMethod(
            "DrainOnceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<int>)method!.Invoke(worker, new object[] { CancellationToken.None })!;
        return await task;
    }

    // ----- in-memory plugin registry returning the recording adapter -----

    /// <summary>
    /// Bare-minimum <see cref="IPluginRegistry"/> wired to one stub
    /// adapter. Sprint 13 worker depends only on Resolve, so the other
    /// methods return reasonable defaults.
    /// </summary>
    private sealed class SinglePluginRegistry : IPluginRegistry
    {
        private readonly RecordingOutcomeAdapter _adapter;
        public SinglePluginRegistry(RecordingOutcomeAdapter adapter) => _adapter = adapter;

        public IReadOnlyList<RegisteredPlugin> All { get; } = Array.Empty<RegisteredPlugin>();

        public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType) =>
            Array.Empty<RegisteredPlugin>();

        public RegisteredPlugin? FindByTypeCode(string module, string typeCode) => null;

        public T Resolve<T>(string module, string typeCode, IServiceProvider services)
            where T : class
        {
            if (string.Equals(typeCode, "test-authority", StringComparison.OrdinalIgnoreCase)
                && typeof(T) == typeof(IInboundOutcomeAdapter))
            {
                return (T)(object)_adapter;
            }
            throw new KeyNotFoundException($"No plugin registered with (Module='{module}', TypeCode='{typeCode}').");
        }
    }
}

/// <summary>
/// Fake <see cref="IInboundOutcomeAdapter"/> that returns a configurable
/// outcome list per call. Counts invocations + can be flipped to throw,
/// so the worker's "leave-cursor-on-failure" path is exercised in
/// isolation.
/// </summary>
internal sealed class RecordingOutcomeAdapter : IInboundOutcomeAdapter
{
    public string TypeCode => "test-authority";
    public ExternalSystemCapabilities Capabilities { get; } =
        new(SupportedDocumentTypes: new[] { "PostHocOutcome" },
            SupportsPushNotifications: false,
            SupportsBulkFetch: true,
            SupportsOutcomePull: true,
            SupportsOutcomePush: false);

    public IReadOnlyList<AuthorityDocumentDto> NextOutcomes { get; set; } = Array.Empty<AuthorityDocumentDto>();
    public bool ShouldThrow { get; set; }
    public int FetchInvocationCount { get; private set; }

    public Task<ConnectionTestResult> TestAsync(ExternalSystemConfig config, CancellationToken ct = default) =>
        Task.FromResult(new ConnectionTestResult(true, "ok"));

    public Task<IReadOnlyList<AuthorityDocumentDto>> FetchDocumentsAsync(
        ExternalSystemConfig config, CaseLookupCriteria lookup, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuthorityDocumentDto>>(Array.Empty<AuthorityDocumentDto>());

    public Task<IReadOnlyList<AuthorityDocumentDto>> FetchOutcomesAsync(
        ExternalSystemConfig cfg, OutcomeWindow window, CancellationToken ct)
    {
        FetchInvocationCount++;
        if (ShouldThrow) throw new InvalidOperationException("simulated authority API failure");
        return Task.FromResult(NextOutcomes);
    }

    public Task<IReadOnlyList<AuthorityDocumentDto>> ReceiveOutcomeWebhookAsync(
        ExternalSystemConfig cfg, InboundWebhookEnvelope envelope, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AuthorityDocumentDto>>(Array.Empty<AuthorityDocumentDto>());

    public Task<SubmissionResult> SubmitAsync(
        ExternalSystemConfig config, OutboundSubmissionRequest request, CancellationToken ct = default) =>
        Task.FromResult(new SubmissionResult(true, null, null));
}
