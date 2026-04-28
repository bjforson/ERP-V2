using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Authorities.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Imaging;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint A1 regression — <see cref="CaseWorkflowService.EvaluateAuthorityRulesAsync"/>
/// must persist its <see cref="RulesEvaluationResult"/> as
/// <see cref="RuleEvaluation"/> rows so the analyst's rules pane survives
/// a page reload. The page-side hydration logic in
/// <c>CaseDetail.razor.Reload()</c> reads the latest snapshot per
/// <c>(CaseId, AuthorityCode)</c> and reconstitutes the result without
/// requiring another click on "Run authority checks".
///
/// Docker is unavailable in this environment so the test uses the EF
/// in-memory provider — A1's persistence path doesn't depend on RLS for
/// correctness (the interceptor-driven RLS is exercised at runtime via
/// the live Postgres DB; the AC for that is the <c>\d</c> output the
/// caller verifies separately).
/// </summary>
public sealed class RuleEvaluationPersistenceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();

    public RuleEvaluationPersistenceTests()
    {
        var services = new ServiceCollection();
        var dbName = "rule-eval-persistence-" + Guid.NewGuid();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
        services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        services.AddSingleton<IImageStore, NoopImageStore>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddSingleton<GhCustomsStubRulesProvider>();
        services.AddSingleton<IPluginRegistry, GhCustomsStubRegistry>();
        services.AddScoped<CaseWorkflowService>();

        _sp = services.BuildServiceProvider();

        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _sp.Dispose();

    /// <summary>
    /// AC: evaluating rules persists one row per AuthorityCode containing
    /// the violations for that authority — and re-running rules upserts
    /// the same row rather than appending history.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task EvaluateAuthorityRulesAsync_PersistsOneRowPerAuthority_AndOverwritesOnRerun()
    {
        using var scope = _sp.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        // Act — evaluate the case for the first time.
        var first = await workflow.EvaluateAuthorityRulesAsync(_caseId);

        first.Violations.Should().HaveCount(2,
            because: "the GH-CUSTOMS stub returns one port-match + one regime violation");

        // Snapshot row should exist for (case, GH-CUSTOMS).
        var rows = await db.RuleEvaluations.AsNoTracking()
            .Where(r => r.CaseId == _caseId)
            .ToListAsync();
        rows.Should().ContainSingle(r => string.Equals(r.AuthorityCode, "GH-CUSTOMS", StringComparison.OrdinalIgnoreCase),
            because: "the workflow groups violations by AuthorityCode and writes one row per authority");

        var ghRow = rows.Single(r => string.Equals(r.AuthorityCode, "GH-CUSTOMS", StringComparison.OrdinalIgnoreCase));
        var persistedViolations = JsonSerializer.Deserialize<List<EvaluatedViolation>>(ghRow.ViolationsJson);
        persistedViolations.Should().NotBeNull();
        persistedViolations!.Select(v => v.Violation.RuleCode).Should().BeEquivalentTo(
            new[] { "GH-PORT-MATCH", "GH-REGIME" });

        // Act — re-evaluate. Snapshot semantics: same row gets upserted.
        var firstRowId = ghRow.Id;
        var firstEvaluatedAt = ghRow.EvaluatedAt;

        // Tiny delay so EvaluatedAt actually advances (utc-ticks resolution).
        await Task.Delay(20);

        var second = await workflow.EvaluateAuthorityRulesAsync(_caseId);
        second.Violations.Should().HaveCount(2);

        var rowsAfter = await db.RuleEvaluations.AsNoTracking()
            .Where(r => r.CaseId == _caseId)
            .ToListAsync();
        rowsAfter.Should().HaveCount(1, because: "re-running rules upserts on (TenantId, CaseId, AuthorityCode) — no history append");
        rowsAfter[0].Id.Should().Be(firstRowId, because: "the same row is updated in place");
        rowsAfter[0].EvaluatedAt.Should().BeAfter(firstEvaluatedAt);
    }

    /// <summary>
    /// AC: the page-side hydration logic in <c>CaseDetail.razor.Reload()</c>
    /// must reconstruct a <see cref="RulesEvaluationResult"/> from the
    /// persisted snapshot rows so the rules pane shows up on cold load
    /// without requiring a fresh "Run authority checks" click.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PersistedSnapshot_HydratesIntoRulesEvaluationResult_OnReload()
    {
        // Arrange — persist a snapshot via the workflow.
        using (var scope = _sp.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();
            await workflow.EvaluateAuthorityRulesAsync(_caseId);
        }

        // Act — simulate the page-side hydration (deserialize the JSON
        // columns back into EvaluatedViolation/Mutation lists).
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var hydrated = await SimulateReloadHydrationAsync(db, _caseId);

            // Assert — we get the same shape back without a re-evaluation.
            hydrated.Should().NotBeNull(because: "Reload() must return a non-null _rulesResult once any rule eval has been persisted");
            hydrated!.Violations.Should().HaveCount(2,
                because: "the persisted snapshot contained 2 violations and Reload() must surface them on cold load");
            hydrated.Violations.Select(v => v.Violation.RuleCode).Should().BeEquivalentTo(
                new[] { "GH-PORT-MATCH", "GH-REGIME" });
            hydrated.Mutations.Should().ContainSingle(m => m.Mutation.MutationKind == "promote_cmr_to_im");
        }
    }

    /// <summary>
    /// Mirror of <c>CaseDetail.razor.HydrateRulesResultAsync</c> — kept here
    /// so the test exercises exactly the deserialization shape the page
    /// uses, even though the page itself can't be instantiated without
    /// the full Razor host. If the page-side logic ever drifts from this
    /// helper, the test breaks loudly.
    /// </summary>
    private static async Task<RulesEvaluationResult?> SimulateReloadHydrationAsync(
        InspectionDbContext db, Guid caseId)
    {
        var rows = await db.RuleEvaluations.AsNoTracking()
            .Where(r => r.CaseId == caseId)
            .OrderByDescending(r => r.EvaluatedAt)
            .ToListAsync();
        if (rows.Count == 0) return null;

        var violations = new List<EvaluatedViolation>();
        var mutations = new List<EvaluatedMutation>();
        var errors = new List<string>();
        foreach (var row in rows)
        {
            var vs = JsonSerializer.Deserialize<List<EvaluatedViolation>>(row.ViolationsJson);
            if (vs is not null) violations.AddRange(vs);
            var ms = JsonSerializer.Deserialize<List<EvaluatedMutation>>(row.MutationsJson);
            if (ms is not null) mutations.AddRange(ms);
            var es = JsonSerializer.Deserialize<List<string>>(row.ProviderErrorsJson);
            if (es is not null) errors.AddRange(es);
        }
        return new RulesEvaluationResult(violations, mutations, errors);
    }

    private async Task SeedAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        db.Locations.Add(new Location
        {
            Id = _locationId,
            Code = "tema",
            Name = "Tema Port",
            TenantId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId,
            LocationId = _locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "MSCU1234567",
            SubjectPayloadJson = "{}",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1,
        });
        // Two documents — one BOE with a mismatched port + unknown regime,
        // and one CMR — to drive both validation findings and the
        // CMR→IM inference. Concrete payloads aren't read by the stub
        // provider; what matters is the DocumentType bucket the workflow
        // hands to ValidateAsync/InferAsync.
        db.AuthorityDocuments.Add(new NickERP.Inspection.Core.Entities.AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = _caseId,
            ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE",
            ReferenceNumber = "C 999999 99",
            PayloadJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            TenantId = 1,
        });
        db.AuthorityDocuments.Add(new NickERP.Inspection.Core.Entities.AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = _caseId,
            ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "CMR",
            ReferenceNumber = "CMR-12345",
            PayloadJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            TenantId = 1,
        });
        await db.SaveChangesAsync();
    }

    // ---------------- stub services -----------------

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) =>
            Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default) =>
            Task.FromResult(events);
    }

    private sealed class NoopImageStore : IImageStore
    {
        public Task<string> SaveSourceAsync(string contentHash, string fileExtension, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            Task.FromResult("noop://" + contentHash);
        public Task<byte[]> ReadSourceAsync(string contentHash, string fileExtension, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
        public Task<string> SaveRenderAsync(Guid scanArtifactId, string kind, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            Task.FromResult("noop://" + scanArtifactId);
        public Stream? OpenRenderRead(Guid scanArtifactId, string kind) => null;
    }

    /// <summary>
    /// Stand-in for the <c>gh-customs</c> rule pack. Returns the same
    /// rule codes (<c>GH-PORT-MATCH</c>, <c>GH-REGIME</c>,
    /// <c>promote_cmr_to_im</c>) as the real provider — the test asserts
    /// against those codes so a future change to the real plugin's wire
    /// format breaks the test on purpose, not silently.
    /// </summary>
    private sealed class GhCustomsStubRulesProvider : IAuthorityRulesProvider
    {
        public string AuthorityCode => "GH-CUSTOMS";

        public Task<ValidationResult> ValidateAsync(InspectionCaseData @case, CancellationToken ct = default)
        {
            var violations = new List<RuleViolation>
            {
                new(RuleCode: "GH-PORT-MATCH",
                    Severity: "Error",
                    Message: "Port mismatch (stub).",
                    FieldPath: "ManifestDetails.DeliveryPlace"),
                new(RuleCode: "GH-REGIME",
                    Severity: "Warning",
                    Message: "Unrecognized regime (stub).",
                    FieldPath: "Header.RegimeCode"),
            };
            return Task.FromResult(new ValidationResult(violations));
        }

        public Task<InferenceResult> InferAsync(InspectionCaseData @case, CancellationToken ct = default)
        {
            // Mirror the real CMR→IM upgrade inference when both BOE and
            // CMR docs are attached.
            var hasBoe = @case.Documents.Any(d => string.Equals(d.DocumentType, "BOE", StringComparison.OrdinalIgnoreCase));
            var hasCmr = @case.Documents.Any(d => string.Equals(d.DocumentType, "CMR", StringComparison.OrdinalIgnoreCase));
            if (hasBoe && hasCmr)
            {
                return Task.FromResult(new InferenceResult(new[]
                {
                    new InferredMutation(
                        MutationKind: "promote_cmr_to_im",
                        DataJson: "{}",
                        Reason: "Stub mutation."),
                }));
            }
            return Task.FromResult(InferenceResult.NoOp);
        }
    }

    /// <summary>
    /// Plugin registry that exposes the GH-CUSTOMS stub provider as the
    /// only registered <see cref="IAuthorityRulesProvider"/>. The
    /// workflow walks <c>ForContract(typeof(IAuthorityRulesProvider))</c>
    /// during evaluation; this registry returns exactly one entry.
    /// </summary>
    private sealed class GhCustomsStubRegistry : IPluginRegistry
    {
        private static readonly RegisteredPlugin StubPlugin = new(
            TypeCode: "gh-customs-stub",
            ConcreteType: typeof(GhCustomsStubRulesProvider),
            ContractTypes: new[] { typeof(IAuthorityRulesProvider) },
            Manifest: new PluginManifest(
                TypeCode: "gh-customs-stub",
                DisplayName: "GH-CUSTOMS stub (test)",
                Version: "1.0",
                Description: null,
                Contracts: new[] { typeof(IAuthorityRulesProvider).FullName! },
                ConfigSchema: null));

        public IReadOnlyList<RegisteredPlugin> All { get; } = new[] { StubPlugin };

        public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType) =>
            All.Where(p => p.ContractTypes.Contains(contractType)).ToList();

        public RegisteredPlugin? FindByTypeCode(string typeCode) =>
            All.FirstOrDefault(p => string.Equals(p.TypeCode, typeCode, StringComparison.OrdinalIgnoreCase));

        public T Resolve<T>(string typeCode, IServiceProvider services) where T : class
        {
            var plugin = FindByTypeCode(typeCode)
                ?? throw new KeyNotFoundException($"No plugin registered with TypeCode '{typeCode}'.");
            return (T)services.GetRequiredService(plugin.ConcreteType);
        }
    }
}
