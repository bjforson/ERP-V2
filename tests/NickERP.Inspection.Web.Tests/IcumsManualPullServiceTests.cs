using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.PostHocOutcomes;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 22 / B2.3 — covers the manual-pull happy path + the four
/// guard-rail failure paths (window inverted, instance not found,
/// adapter not registered, capability flag off).
/// </summary>
public sealed class IcumsManualPullServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly FakePluginRegistry _registry = new();
    private readonly RecordingWriter _writer = new();

    public IcumsManualPullServiceTests()
    {
        var dbName = "manualpull-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        services.AddSingleton<IPluginRegistry>(_registry);
        services.AddSingleton<IPostHocOutcomeWriter>(_writer);
        services.AddScoped<IcumsManualPullService>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private async Task<Guid> SeedExternalSystemAsync(string typeCode, bool isActive = true)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var id = Guid.NewGuid();
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = id, TypeCode = typeCode, DisplayName = $"{typeCode} primary",
            ConfigJson = "{}", IsActive = isActive, TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task PullAsync_RejectsInvertedWindow()
    {
        var esiId = await SeedExternalSystemAsync("icums-gh");
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsManualPullService>();
        var result = await svc.PullAsync(
            esiId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(-1));
        Assert.False(result.Success);
        Assert.Contains("Until must be strictly after Since", result.Notice);
    }

    [Fact]
    public async Task PullAsync_InstanceNotFound_Fails()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsManualPullService>();
        var result = await svc.PullAsync(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Notice);
    }

    [Fact]
    public async Task PullAsync_AdapterNotRegistered_Fails()
    {
        var esiId = await SeedExternalSystemAsync("nonexistent-type");
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsManualPullService>();
        var result = await svc.PullAsync(
            esiId,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow);
        Assert.False(result.Success);
        Assert.Contains("No plugin registered", result.Notice);
    }

    [Fact]
    public async Task PullAsync_AdapterCapabilityOff_Fails()
    {
        var esiId = await SeedExternalSystemAsync("icums-no-pull");
        _registry.Register("icums-no-pull", new FakeAdapter(supportsPull: false));
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsManualPullService>();
        var result = await svc.PullAsync(
            esiId,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow);
        Assert.False(result.Success);
        Assert.Contains("SupportsOutcomePull=false", result.Notice);
    }

    [Fact]
    public async Task PullAsync_HappyPath_FetchesAndWrites()
    {
        var esiId = await SeedExternalSystemAsync("icums-gh");
        var fakeDocs = new[]
        {
            new AuthorityDocumentDto(
                InstanceId: esiId,
                DocumentType: "BOE",
                ReferenceNumber: "BOE-001",
                ReceivedAt: DateTimeOffset.UtcNow,
                PayloadJson: "{}"),
            new AuthorityDocumentDto(
                InstanceId: esiId,
                DocumentType: "BOE",
                ReferenceNumber: "BOE-002",
                ReceivedAt: DateTimeOffset.UtcNow,
                PayloadJson: "{}"),
        };
        _registry.Register("icums-gh", new FakeAdapter(supportsPull: true, docs: fakeDocs));

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IcumsManualPullService>();
        var result = await svc.PullAsync(
            esiId,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow);

        Assert.True(result.Success);
        Assert.Equal(2, result.Fetched);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(2, _writer.Records.Count);
        Assert.All(_writer.Records, r => Assert.Equal("manual_pull", r.EntryMethod));
    }

    /// <summary>Fake plugin registry — only supports the surface IcumsManualPullService uses.</summary>
    private sealed class FakePluginRegistry : IPluginRegistry
    {
        private readonly Dictionary<string, IInboundOutcomeAdapter> _byTypeCode = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string typeCode, IInboundOutcomeAdapter adapter) => _byTypeCode[typeCode] = adapter;

        public IReadOnlyList<RegisteredPlugin> All => Array.Empty<RegisteredPlugin>();
        public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType) => Array.Empty<RegisteredPlugin>();
        public RegisteredPlugin? FindByTypeCode(string module, string typeCode) => null;

        public T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class
        {
            if (!_byTypeCode.TryGetValue(typeCode, out var adapter))
                throw new KeyNotFoundException(
                    $"No plugin registered with (Module='{module}', TypeCode='{typeCode}').");
            if (adapter is T t) return t;
            throw new InvalidOperationException(
                $"Plugin '{typeCode}' is not assignable to {typeof(T).FullName}.");
        }
    }

    /// <summary>Fake outcome adapter — returns a fixed list of docs.</summary>
    private sealed class FakeAdapter : IInboundOutcomeAdapter
    {
        private readonly IReadOnlyList<AuthorityDocumentDto> _docs;

        public FakeAdapter(bool supportsPull, IReadOnlyList<AuthorityDocumentDto>? docs = null)
        {
            _docs = docs ?? Array.Empty<AuthorityDocumentDto>();
            Capabilities = new ExternalSystemCapabilities(
                SupportedDocumentTypes: new[] { "BOE" },
                SupportsPushNotifications: false,
                SupportsBulkFetch: false,
                SupportsOutcomePull: supportsPull,
                SupportsOutcomePush: false);
        }

        public string TypeCode => "fake-adapter";

        public ExternalSystemCapabilities Capabilities { get; }

        public Task<IReadOnlyList<AuthorityDocumentDto>> FetchOutcomesAsync(
            ExternalSystemConfig cfg, OutcomeWindow window, CancellationToken ct) =>
            Task.FromResult(_docs);

        public Task<IReadOnlyList<AuthorityDocumentDto>> ReceiveOutcomeWebhookAsync(
            ExternalSystemConfig cfg, InboundWebhookEnvelope envelope, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AuthorityDocumentDto>>(Array.Empty<AuthorityDocumentDto>());

        public Task<IReadOnlyList<AuthorityDocumentDto>> FetchDocumentsAsync(
            ExternalSystemConfig cfg, CaseLookupCriteria lookup, CancellationToken ct) =>
            Task.FromResult(_docs);

        public Task<SubmissionResult> SubmitAsync(
            ExternalSystemConfig cfg, OutboundSubmissionRequest request, CancellationToken ct) =>
            Task.FromResult(new SubmissionResult(true, null, null));

        public Task<ConnectionTestResult> TestAsync(
            ExternalSystemConfig cfg, CancellationToken ct) =>
            Task.FromResult(new ConnectionTestResult(true, "ok"));
    }

    /// <summary>Test double — captures every record handed to the writer.</summary>
    private sealed class RecordingWriter : IPostHocOutcomeWriter
    {
        public List<PostHocOutcomeRecord> Records { get; } = new();

        public Task<OutcomeWriteOutcome> WriteAsync(PostHocOutcomeRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.FromResult(OutcomeWriteOutcome.Inserted);
        }
    }
}
