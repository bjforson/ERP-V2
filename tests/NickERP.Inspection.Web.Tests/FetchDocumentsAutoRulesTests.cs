using System.Security.Claims;
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
/// D3 regression: <see cref="CaseWorkflowService.FetchDocumentsAsync"/>
/// auto-fires <see cref="CaseWorkflowService.EvaluateAuthorityRulesAsync"/>
/// at the end of a successful fetch. The auto-evaluation must be
/// best-effort — a misbehaving rules registry / provider must not undo
/// the document fetch or the case state transition.
///
/// Docker is not available in this environment so the test uses the EF
/// in-memory provider. The auto-fire path doesn't depend on RLS, so the
/// in-memory provider is sufficient to exercise the try/catch behaviour.
/// </summary>
public sealed class FetchDocumentsAutoRulesTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _externalSystemId = Guid.NewGuid();

    public FetchDocumentsAutoRulesTests()
    {
        var services = new ServiceCollection();
        var dbName = "fetch-auto-rules-" + Guid.NewGuid();
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

        // The fetch path resolves an IExternalSystemAdapter and runs it; the
        // auto-fire path then asks the registry for IAuthorityRulesProvider
        // implementations. Our test registry returns a working stub for the
        // first contract and THROWS for the second — that's what triggers
        // the new try/catch wrapper inside FetchDocumentsAsync.
        services.AddSingleton<StubExternalSystemAdapter>();
        services.AddSingleton<IPluginRegistry, ThrowingRulesRegistry>();
        services.AddScoped<CaseWorkflowService>();

        _sp = services.BuildServiceProvider();

        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchDocumentsAsync_WhenAutoRulesEvaluationThrows_StillReturnsDocumentsAndValidatesCase()
    {
        // Regression guarded: a throwing rules provider/registry must not
        // undo the document fetch nor the Open → Validated transition.
        // FetchDocumentsAsync wraps EvaluateAuthorityRulesAsync in
        // try/catch precisely so the analyst can re-run rules manually if
        // the auto-fire fails.
        using var scope = _sp.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var result = await workflow.FetchDocumentsAsync(_caseId, _externalSystemId);

        result.Should().NotBeNull();
        result.Documents.Should().HaveCount(1, because: "the stub adapter emits exactly one BOE");
        result.Documents[0].DocumentType.Should().Be("BOE");
        result.Rules.Should().BeNull(because: "auto-evaluation threw and we surface that as Rules=null");

        // Persisted state — case advanced and the document landed.
        var reloaded = await db.Cases.AsNoTracking().FirstAsync(c => c.Id == _caseId);
        reloaded.State.Should().Be(InspectionWorkflowState.Validated);

        var savedDocs = await db.AuthorityDocuments.AsNoTracking()
            .Where(d => d.CaseId == _caseId).ToListAsync();
        savedDocs.Should().HaveCount(1);
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
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = _externalSystemId,
            TypeCode = "stub-external",
            DisplayName = "Stub External",
            ConfigJson = "{}",
            IsActive = true,
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
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
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
    /// Minimal external-system adapter that emits one BOE. The fetch path
    /// only needs an in-memory adapter — we're testing the workflow's
    /// post-fetch auto-fire behaviour, not adapter logic.
    /// </summary>
    private sealed class StubExternalSystemAdapter : IExternalSystemAdapter
    {
        public string TypeCode => "stub-external";
        public ExternalSystemCapabilities Capabilities { get; } =
            new(new[] { "BOE" }, false, false);

        public Task<ConnectionTestResult> TestAsync(ExternalSystemConfig config, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true, "ok"));

        public Task<IReadOnlyList<NickERP.Inspection.ExternalSystems.Abstractions.AuthorityDocumentDto>> FetchDocumentsAsync(
            ExternalSystemConfig config, CaseLookupCriteria lookup, CancellationToken ct = default)
        {
            IReadOnlyList<NickERP.Inspection.ExternalSystems.Abstractions.AuthorityDocumentDto> docs = new[]
            {
                new NickERP.Inspection.ExternalSystems.Abstractions.AuthorityDocumentDto(
                    config.InstanceId,
                    "BOE",
                    "C 999999 99",
                    DateTimeOffset.UtcNow,
                    "{\"Header\":{\"DeclarationNumber\":\"C 999999 99\"}}"),
            };
            return Task.FromResult(docs);
        }

        public Task<SubmissionResult> SubmitAsync(ExternalSystemConfig config, OutboundSubmissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SubmissionResult(true, null, null));
    }

    /// <summary>
    /// Plugin registry that resolves the external-system adapter normally
    /// but throws as soon as the workflow asks for the rules-provider list.
    /// That throw bubbles out of <c>EvaluateAuthorityRulesAsync</c> and
    /// must be caught by the new wrapper inside <c>FetchDocumentsAsync</c>.
    /// </summary>
    private sealed class ThrowingRulesRegistry : IPluginRegistry
    {
        private static readonly RegisteredPlugin StubExternalPlugin = new(
            Module: "inspection",
            TypeCode: "stub-external",
            ConcreteType: typeof(StubExternalSystemAdapter),
            ContractTypes: new[] { typeof(IExternalSystemAdapter) },
            Manifest: new PluginManifest(
                TypeCode: "stub-external",
                DisplayName: "Stub External",
                Version: "1.0",
                Description: null,
                Contracts: new[] { typeof(IExternalSystemAdapter).FullName! },
                ConfigSchema: null) { Module = "inspection" });

        public IReadOnlyList<RegisteredPlugin> All { get; } = new[] { StubExternalPlugin };

        public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType)
        {
            if (contractType == typeof(IAuthorityRulesProvider))
            {
                throw new InvalidOperationException(
                    "Simulated rules-registry failure for D3 regression test.");
            }
            return All.Where(p => p.ContractTypes.Contains(contractType)).ToList();
        }

        public RegisteredPlugin? FindByTypeCode(string module, string typeCode) =>
            All.FirstOrDefault(p =>
                string.Equals(p.Module, module, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.TypeCode, typeCode, StringComparison.OrdinalIgnoreCase));

        public T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class
        {
            var plugin = FindByTypeCode(module, typeCode)
                ?? throw new KeyNotFoundException($"No plugin registered with (Module='{module}', TypeCode='{typeCode}').");
            return (T)services.GetRequiredService(plugin.ConcreteType);
        }
    }
}
