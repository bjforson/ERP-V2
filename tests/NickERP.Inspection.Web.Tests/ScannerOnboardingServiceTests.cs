using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 46 / Phase D — coverage for the
/// <see cref="ScannerOnboardingService"/> wired up in Phase A. Tests
/// the append-on-overwrite contract, the latest-per-field reader, and
/// the <c>nickerp.inspection.scanner_onboarded</c> emission shape.
/// </summary>
public sealed class ScannerOnboardingServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly RecordingEventPublisher _events;
    private const long TenantId = 1L;
    private const string TypeCode = "fs6000";

    public ScannerOnboardingServiceTests()
    {
        var services = new ServiceCollection();
        var dbName = "scanner-onboarding-" + Guid.NewGuid();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(TenantId);
            return t;
        });
        _events = new RecordingEventPublisher();
        services.AddSingleton<IEventPublisher>(_events);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));

        services.AddScoped<ScannerOnboardingService>();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task Fields_lists_all_12_questionnaire_codes()
    {
        // The 12 Annex B Table 55 fields — sanity check that the static
        // list survived a refactor. Renaming a code orphans prior rows
        // (the reader keys on FieldName), so this guards against
        // accidental rename.
        ScannerOnboardingService.Fields.Should().HaveCountGreaterThanOrEqualTo(12);
        var codes = ScannerOnboardingService.Fields.Select(f => f.FieldName).ToList();
        codes.Should().Contain("manufacturer_model");
        codes.Should().Contain("image_export_format");
        codes.Should().Contain("api_sdk_availability");
        codes.Should().Contain("network_access");
        codes.Should().Contain("image_ownership");
        codes.Should().Contain("performance");
        codes.Should().Contain("image_size");
        codes.Should().Contain("material_channels");
        codes.Should().Contain("dual_view_pairing");
        codes.Should().Contain("time_sync");
        codes.Should().Contain("operator_identity");
        codes.Should().Contain("local_storage");
    }

    [Fact]
    public async Task RecordResponseAsync_inserts_one_row_per_call()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        await svc.RecordResponseAsync(TypeCode, "manufacturer_model", "FS6000 / v3.2 / Win11");

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var rows = await db.ScannerOnboardingResponses.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].ScannerDeviceTypeId.Should().Be(TypeCode);
        rows[0].FieldName.Should().Be("manufacturer_model");
        rows[0].Value.Should().Be("FS6000 / v3.2 / Win11");
        rows[0].TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task RecordResponseAsync_re_recording_appends_does_not_overwrite()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        await svc.RecordResponseAsync(TypeCode, "performance", "300/hr");
        await svc.RecordResponseAsync(TypeCode, "performance", "400/hr");
        await svc.RecordResponseAsync(TypeCode, "performance", "500/hr");

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var rows = await db.ScannerOnboardingResponses.AsNoTracking()
            .Where(r => r.FieldName == "performance")
            .OrderBy(r => r.RecordedAt)
            .ToListAsync();
        rows.Should().HaveCount(3,
            because: "the service appends on re-record so the questionnaire history is visible without a separate history table");
        rows.Select(r => r.Value).Should().BeEquivalentTo(new[] { "300/hr", "400/hr", "500/hr" });
    }

    [Fact]
    public async Task GetCurrentResponsesAsync_returns_latest_per_field()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        // Three answers across two fields — the reader takes the latest
        // per field by RecordedAt.
        await svc.RecordResponseAsync(TypeCode, "performance", "300/hr");
        await Task.Delay(2);
        await svc.RecordResponseAsync(TypeCode, "performance", "500/hr");
        await svc.RecordResponseAsync(TypeCode, "image_size", "2048x1024");

        var current = await svc.GetCurrentResponsesAsync(TypeCode);
        current.Should().HaveCount(2);
        current["performance"].Value.Should().Be("500/hr");
        current["image_size"].Value.Should().Be("2048x1024");
    }

    [Fact]
    public async Task GetCurrentResponsesAsync_filters_by_scanner_type()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        await svc.RecordResponseAsync("fs6000", "performance", "300/hr");
        await svc.RecordResponseAsync("ase", "performance", "100/hr");

        var fs6000 = await svc.GetCurrentResponsesAsync("fs6000");
        fs6000.Should().ContainKey("performance");
        fs6000["performance"].Value.Should().Be("300/hr");

        var ase = await svc.GetCurrentResponsesAsync("ase");
        ase.Should().ContainKey("performance");
        ase["performance"].Value.Should().Be("100/hr");
    }

    [Fact]
    public async Task GetCurrentResponsesAsync_returns_empty_when_no_responses()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();
        var current = await svc.GetCurrentResponsesAsync("never_recorded");
        current.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkOnboardingCompleteAsync_emits_scanner_onboarded_event()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        await svc.RecordResponseAsync(TypeCode, "manufacturer_model", "Acme | FS6000 | v3.2 | Win11");
        await svc.RecordResponseAsync(TypeCode, "performance", "500/hr");
        _events.Events.Clear();

        await svc.MarkOnboardingCompleteAsync(TypeCode, hasPlugin: true);

        _events.Events.Should().ContainSingle(
            e => e.EventType == "nickerp.inspection.scanner_onboarded");
        var evt = _events.Events.Single(e => e.EventType == "nickerp.inspection.scanner_onboarded");
        evt.EntityType.Should().Be("ScannerDeviceType");
        evt.EntityId.Should().Be(TypeCode);
        evt.TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task MarkOnboardingCompleteAsync_extracts_manufacturer_and_model_from_pipe_separated_value()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        await svc.RecordResponseAsync(TypeCode, "manufacturer_model", "Acme | FS6000 | v3.2 | Win11");
        _events.Events.Clear();

        await svc.MarkOnboardingCompleteAsync(TypeCode, hasPlugin: true);

        var evt = _events.Events.Single(e => e.EventType == "nickerp.inspection.scanner_onboarded");
        var payloadJson = evt.Payload;
        payloadJson.GetProperty("manufacturer").GetString().Should().Be("Acme");
        payloadJson.GetProperty("model").GetString().Should().Be("FS6000");
        payloadJson.GetProperty("hasPlugin").GetBoolean().Should().BeTrue();
        payloadJson.GetProperty("scannerDeviceTypeId").GetString().Should().Be(TypeCode);
    }

    [Fact]
    public async Task MarkOnboardingCompleteAsync_records_field_count()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        await svc.RecordResponseAsync(TypeCode, "manufacturer_model", "x");
        await svc.RecordResponseAsync(TypeCode, "performance", "y");
        await svc.RecordResponseAsync(TypeCode, "image_size", "z");
        _events.Events.Clear();

        await svc.MarkOnboardingCompleteAsync(TypeCode, hasPlugin: false);

        var evt = _events.Events.Single(e => e.EventType == "nickerp.inspection.scanner_onboarded");
        evt.Payload.GetProperty("fieldsAnswered").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task MarkOnboardingCompleteAsync_does_not_throw_with_no_responses()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        // No prior RecordResponseAsync calls — operator skips the
        // questionnaire entirely. Service should still emit the event
        // with manufacturer / model = empty string.
        Func<Task> act = () => svc.MarkOnboardingCompleteAsync(TypeCode, hasPlugin: false);
        await act.Should().NotThrowAsync();

        var evt = _events.Events.Single(e => e.EventType == "nickerp.inspection.scanner_onboarded");
        evt.Payload.GetProperty("fieldsAnswered").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task RecordResponseAsync_throws_on_empty_type_code()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();
        Func<Task> act = () => svc.RecordResponseAsync(" ", "performance", "x");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordResponseAsync_throws_on_empty_field_name()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();
        Func<Task> act = () => svc.RecordResponseAsync(TypeCode, "", "x");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordResponseAsync_normalises_type_code_and_field_name_whitespace()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();

        await svc.RecordResponseAsync("  fs6000  ", "  performance  ", "500/hr");

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var row = await db.ScannerOnboardingResponses.AsNoTracking().FirstAsync();
        row.ScannerDeviceTypeId.Should().Be("fs6000");
        row.FieldName.Should().Be("performance");
    }

    [Fact]
    public async Task GetCurrentResponsesAsync_throws_on_empty_type_code()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScannerOnboardingService>();
        Func<Task> act = () => svc.GetCurrentResponsesAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
