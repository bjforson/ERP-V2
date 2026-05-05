using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Authorities.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Imaging;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Features;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 48 / Phase A — coverage for FU-strict-mode-block-on-error.
/// Asserts the per-tenant strict-mode flag actually gates
/// <see cref="CaseWorkflowService.SubmitAsync"/>:
/// <list type="bullet">
///   <item>Off + engine errors → submission proceeds (legacy behaviour).</item>
///   <item>On + engine errors → submission throws
///         <see cref="ValidationStrictModeException"/> with the failing
///         rule list.</item>
///   <item>On + no errors → submission proceeds.</item>
///   <item>Strict-mode toggle audits via
///         <c>nickerp.inspection.validation.strict_mode_changed</c>.</item>
/// </list>
/// </summary>
public sealed class StrictModeSubmissionTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _externalSystemInstanceId = Guid.NewGuid();
    private readonly InMemoryTenantSettingsService _settings = new();
    private readonly RecordingEventPublisher _events = new();
    private bool _engineEmitsError = true;

    public StrictModeSubmissionTests()
    {
        var services = new ServiceCollection();
        var dbName = "s48-strict-" + Guid.NewGuid();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(_tenantId);
            return t;
        });
        services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
        services.AddSingleton<IEventPublisher>(_events);
        services.AddSingleton<IImageStore>(new NoopImageStore());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ITenantSettingsService>(_settings);

        // Validation engine — uses an in-memory enablement provider and
        // a deterministic test rule whose severity is toggled by the
        // _engineEmitsError flag so each test scenario can choose
        // pass-or-fail.
        services.AddSingleton<InMemoryRuleEnablementProvider>();
        services.AddSingleton<IRuleEnablementProvider>(sp => sp.GetRequiredService<InMemoryRuleEnablementProvider>());
        services.AddScoped<IValidationRule>(_ => new ToggleableRule(() => _engineEmitsError));
        services.AddScoped<ValidationEngine>();

        services.AddSingleton<IPluginRegistry, AcceptingExternalSystemRegistry>();
        services.AddSingleton<AcceptingExternalAdapter>();
        services.AddScoped<CaseWorkflowService>();

        _sp = services.BuildServiceProvider();
        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task Submit_proceeds_when_strict_mode_off_even_with_engine_errors()
    {
        // strict-mode default false; engine returns Error.
        _engineEmitsError = true;
        await SeedVerdictAsync();

        using var scope = _sp.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();
        var submission = await workflow.SubmitAsync(_caseId, _externalSystemInstanceId);

        Assert.NotNull(submission);
        Assert.Equal("accepted", submission.Status);
    }

    [Fact]
    public async Task Submit_throws_when_strict_mode_on_and_engine_errors()
    {
        _settings.Set(_tenantId, CaseWorkflowService.StrictModeSettingKey, "true");
        _engineEmitsError = true;
        await SeedVerdictAsync();

        using var scope = _sp.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();

        var ex = await Assert.ThrowsAsync<ValidationStrictModeException>(
            () => workflow.SubmitAsync(_caseId, _externalSystemInstanceId));

        Assert.Equal(_caseId, ex.CaseId);
        Assert.Equal(1, ex.ErrorCount);
        Assert.Single(ex.FailingRuleIds);
        Assert.Equal("test.toggleable", ex.FailingRuleIds[0]);

        // No OutboundSubmission row should have been written — the gate
        // runs before the submission INSERT.
        using var verifyScope = _sp.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var rows = await db.OutboundSubmissions.AsNoTracking().Where(o => o.CaseId == _caseId).ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Submit_proceeds_when_strict_mode_on_and_engine_clean()
    {
        _settings.Set(_tenantId, CaseWorkflowService.StrictModeSettingKey, "true");
        _engineEmitsError = false; // engine returns Pass on this run
        await SeedVerdictAsync();

        using var scope = _sp.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();
        var submission = await workflow.SubmitAsync(_caseId, _externalSystemInstanceId);

        Assert.NotNull(submission);
        Assert.Equal("accepted", submission.Status);
    }

    [Fact]
    public async Task SetStrictMode_emits_strict_mode_changed_audit_event()
    {
        // Build a RulesAdminService — it needs TenancyDbContext + AuditDbContext
        // which we don't otherwise wire here; spin up minimal in-memory ones.
        var services = new ServiceCollection();
        var tenancyDbName = "s48-tenancy-" + Guid.NewGuid();
        var auditDbName = "s48-audit-" + Guid.NewGuid();
        services.AddDbContext<NickERP.Platform.Tenancy.Database.TenancyDbContext>(o =>
            o.UseInMemoryDatabase(tenancyDbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<NickERP.Platform.Audit.Database.AuditDbContext>(o =>
            o.UseInMemoryDatabase(auditDbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(_tenantId);
            return t;
        });
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var localEvents = new RecordingEventPublisher();
        services.AddSingleton<IEventPublisher>(localEvents);
        var localSettings = new InMemoryTenantSettingsService();
        services.AddSingleton<ITenantSettingsService>(localSettings);
        // ValidationEngine is a hard dep of RulesAdminService — wire one
        // with no rules so the constructor doesn't throw.
        services.AddSingleton<IRuleEnablementProvider, InMemoryRuleEnablementProvider>();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase("s48-rules-admin-" + Guid.NewGuid())
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ValidationEngine>();
        services.AddScoped<RulesAdminService>();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<RulesAdminService>();

        var actor = Guid.NewGuid();
        await admin.SetStrictModeAsync(_tenantId, true, actor);

        var emitted = Assert.Single(
            localEvents.Events,
            e => e.EventType == "nickerp.inspection.validation.strict_mode_changed");
        Assert.Equal(_tenantId, emitted.TenantId);
        Assert.Equal(actor, emitted.ActorUserId);
        Assert.Equal("TenantSetting", emitted.EntityType);
        Assert.True(emitted.Payload.GetProperty("newValue").GetBoolean());
        Assert.False(emitted.Payload.GetProperty("oldValue").GetBoolean());

        // Round-trip: the setting got stored.
        Assert.True(await admin.GetStrictModeAsync(_tenantId));
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
            TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId,
            LocationId = _locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "MSCU1234567",
            SubjectPayloadJson = "{}",
            State = InspectionWorkflowState.Verdict,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = _externalSystemInstanceId,
            TypeCode = "accepting-stub",
            DisplayName = "Accepting stub",
            Scope = ExternalSystemBindingScope.Shared,
            ConfigJson = "{}",
            IsActive = true,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedVerdictAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        if (await db.Verdicts.AsNoTracking().AnyAsync(v => v.CaseId == _caseId)) return;
        db.Verdicts.Add(new Verdict
        {
            Id = Guid.NewGuid(),
            CaseId = _caseId,
            Decision = VerdictDecision.Clear,
            Basis = "test",
            DecidedAt = DateTimeOffset.UtcNow,
            DecidedByUserId = Guid.NewGuid(),
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    // --------------- helpers ---------------

    private sealed class ToggleableRule : IValidationRule
    {
        private readonly Func<bool> _emitError;
        public ToggleableRule(Func<bool> emitError) => _emitError = emitError;
        public string RuleId => "test.toggleable";
        public string Description => "Toggleable test rule.";
        public ValidationOutcome Evaluate(ValidationContext context) =>
            _emitError()
                ? ValidationOutcome.Error(RuleId, "test error")
                : ValidationOutcome.Pass(RuleId);
    }

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

    private sealed class AcceptingExternalAdapter : IExternalSystemAdapter
    {
        public string TypeCode => "accepting-stub";
        public ExternalSystemCapabilities Capabilities => new(new[] { "BOE" }, false, false);
        public Task<ConnectionTestResult> TestAsync(ExternalSystemConfig config, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true, "ok"));
        public Task<IReadOnlyList<AuthorityDocumentDto>> FetchDocumentsAsync(ExternalSystemConfig config, CaseLookupCriteria lookup, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuthorityDocumentDto>>(Array.Empty<AuthorityDocumentDto>());
        public Task<SubmissionResult> SubmitAsync(ExternalSystemConfig config, OutboundSubmissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SubmissionResult(Accepted: true, AuthorityResponseJson: "{\"ok\":true}", Error: null));
    }

    private sealed class AcceptingExternalSystemRegistry : IPluginRegistry
    {
        private static readonly RegisteredPlugin StubPlugin = new(
            Module: "inspection",
            TypeCode: "accepting-stub",
            ConcreteType: typeof(AcceptingExternalAdapter),
            ContractTypes: new[] { typeof(IExternalSystemAdapter) },
            Manifest: new PluginManifest(
                TypeCode: "accepting-stub",
                DisplayName: "Accepting stub",
                Version: "1.0",
                Description: null,
                Contracts: new[] { typeof(IExternalSystemAdapter).FullName! },
                ConfigSchema: null) { Module = "inspection" });

        public IReadOnlyList<RegisteredPlugin> All { get; } = new[] { StubPlugin };

        public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType) =>
            All.Where(p => p.ContractTypes.Contains(contractType)).ToList();

        public RegisteredPlugin? FindByTypeCode(string module, string typeCode) =>
            All.FirstOrDefault(p =>
                string.Equals(p.Module, module, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.TypeCode, typeCode, StringComparison.OrdinalIgnoreCase));

        public T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class
        {
            var plugin = FindByTypeCode(module, typeCode)
                ?? throw new KeyNotFoundException($"No plugin (Module='{module}', TypeCode='{typeCode}').");
            return (T)services.GetRequiredService(plugin.ConcreteType);
        }
    }
}

// RecordingEventPublisher reused from CompletenessCheckerTests.cs (same
// namespace, same internal sealed type) — no local re-declaration needed.
