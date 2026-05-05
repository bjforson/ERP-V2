using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Retention;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Retention;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Platform.Tenancy.Features;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 44 / Phase D — coverage for <see cref="RetentionService"/>:
/// SetRetentionClassAsync + ApplyLegalHoldAsync + ReleaseLegalHoldAsync
/// + cascade-to-artifacts behavior + audit emission shapes
/// + GetRetentionPolicyAsync (tenant override + fallback).
/// </summary>
public sealed class RetentionServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly RecordingEventPublisher _events = new();
    private readonly RetentionFakeTimeProvider _clock = new();

    public RetentionServiceTests()
    {
        var dbName = "s44-retention-svc-" + Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(_tenantId);
            return t;
        });

        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IEventPublisher>(_events);
        services.AddSingleton<ITenantSettingsService>(new InMemoryTenantSettingsService());

        services.AddScoped<RetentionService>();

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task SetRetentionClass_changes_class_and_emits_audit()
    {
        var caseId = await SeedCaseAsync(RetentionClass.Standard);

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        var actor = Guid.NewGuid();
        await svc.SetRetentionClassAsync(caseId, RetentionClass.Extended, actor);

        // Re-read.
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var c = await db.Cases.AsNoTracking().FirstAsync(x => x.Id == caseId);
        Assert.Equal(RetentionClass.Extended, c.RetentionClass);
        Assert.NotNull(c.RetentionClassSetAt);
        Assert.Equal(actor, c.RetentionClassSetByUserId);

        var evt = Assert.Single(_events.Events.Where(e => e.EventType == "nickerp.inspection.retention_class_changed"));
        Assert.Equal(_tenantId, evt.TenantId);
        Assert.Equal(caseId.ToString(), evt.EntityId);
        Assert.Equal("Standard", evt.Payload.GetProperty("oldClass").GetString());
        Assert.Equal("Extended", evt.Payload.GetProperty("newClass").GetString());
    }

    [Fact]
    public async Task SetRetentionClass_throws_when_case_not_found()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetRetentionClassAsync(Guid.NewGuid(), RetentionClass.Extended, null));
    }

    [Fact]
    public async Task ApplyLegalHold_cascades_to_all_artifacts_and_emits_audit()
    {
        var caseId = await SeedCaseAsync(RetentionClass.Standard);
        var scanId = await SeedScanAsync(caseId);
        await SeedArtifactAsync(scanId, kind: "Primary");
        await SeedArtifactAsync(scanId, kind: "SideView");
        await SeedArtifactAsync(scanId, kind: "Material");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        var actor = Guid.NewGuid();
        await svc.ApplyLegalHoldAsync(caseId, "subpoena 24-001", actor);

        // Re-read.
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var c = await db.Cases.AsNoTracking().FirstAsync(x => x.Id == caseId);
        Assert.True(c.LegalHold);
        Assert.Equal("subpoena 24-001", c.LegalHoldReason);
        Assert.Equal(actor, c.LegalHoldAppliedByUserId);

        var artifacts = await db.Set<ScanArtifact>().AsNoTracking().Where(a => a.ScanId == scanId).ToListAsync();
        Assert.Equal(3, artifacts.Count);
        Assert.All(artifacts, a => Assert.True(a.LegalHold));
        Assert.All(artifacts, a => Assert.Equal("subpoena 24-001", a.LegalHoldReason));

        var evt = Assert.Single(_events.Events.Where(e => e.EventType == "nickerp.inspection.legal_hold_applied"));
        Assert.Equal(3, evt.Payload.GetProperty("artifactCount").GetInt32());
        Assert.Equal("subpoena 24-001", evt.Payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ApplyLegalHold_truncates_reason_at_500_chars()
    {
        var caseId = await SeedCaseAsync(RetentionClass.Standard);
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        var longReason = new string('x', 600);
        await svc.ApplyLegalHoldAsync(caseId, longReason, null);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var c = await db.Cases.AsNoTracking().FirstAsync();
        Assert.Equal(500, c.LegalHoldReason!.Length);
    }

    [Fact]
    public async Task ApplyLegalHold_rejects_blank_reason()
    {
        var caseId = await SeedCaseAsync(RetentionClass.Standard);
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => svc.ApplyLegalHoldAsync(caseId, "   ", null));
    }

    [Fact]
    public async Task ReleaseLegalHold_cascades_back_and_emits_audit()
    {
        var caseId = await SeedCaseAsync(RetentionClass.Standard);
        var scanId = await SeedScanAsync(caseId);
        await SeedArtifactAsync(scanId, kind: "Primary");
        await SeedArtifactAsync(scanId, kind: "SideView");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        await svc.ApplyLegalHoldAsync(caseId, "investigation IR-742", null);
        _events.Events.Clear();

        await svc.ReleaseLegalHoldAsync(caseId, "investigation closed", null);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var c = await db.Cases.AsNoTracking().FirstAsync();
        Assert.False(c.LegalHold);
        // Apply* fields persist for audit trail.
        Assert.NotNull(c.LegalHoldAppliedAt);
        Assert.Equal("investigation IR-742", c.LegalHoldReason);

        var artifacts = await db.Set<ScanArtifact>().AsNoTracking().ToListAsync();
        Assert.All(artifacts, a => Assert.False(a.LegalHold));

        var evt = Assert.Single(_events.Events.Where(e => e.EventType == "nickerp.inspection.legal_hold_released"));
        Assert.Equal("investigation closed", evt.Payload.GetProperty("releaseReason").GetString());
    }

    [Fact]
    public async Task ReleaseLegalHold_throws_when_not_held()
    {
        var caseId = await SeedCaseAsync(RetentionClass.Standard);
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReleaseLegalHoldAsync(caseId, "release", null));
    }

    [Fact]
    public async Task ListLegalHolds_returns_only_held_cases_for_tenant()
    {
        // Two cases: one held, one not.
        var heldId = await SeedCaseAsync(RetentionClass.Standard);
        var unheldId = await SeedCaseAsync(RetentionClass.Standard);

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        await svc.ApplyLegalHoldAsync(heldId, "test hold", null);

        var holds = await svc.ListLegalHoldsAsync(_tenantId);
        Assert.Single(holds);
        Assert.Equal(heldId, holds[0].CaseId);
        Assert.True(holds[0].LegalHold);
        Assert.Equal("test hold", holds[0].LegalHoldReason);
    }

    [Fact]
    public async Task ListByRetentionClass_paginates_and_orders_by_OpenedAt()
    {
        // Three Standard cases, OpenedAt at varying times.
        var c1 = await SeedCaseAsync(RetentionClass.Standard, openedAt: _clock.UtcNow.AddDays(-100));
        var c2 = await SeedCaseAsync(RetentionClass.Standard, openedAt: _clock.UtcNow.AddDays(-50));
        var c3 = await SeedCaseAsync(RetentionClass.Standard, openedAt: _clock.UtcNow.AddDays(-10));
        // One Extended (excluded by class filter).
        await SeedCaseAsync(RetentionClass.Extended);

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();

        var page1 = await svc.ListByRetentionClassAsync(_tenantId, RetentionClass.Standard, take: 2, skip: 0);
        Assert.Equal(2, page1.Count);
        // Newest first.
        Assert.Equal(c3, page1[0].CaseId);
        Assert.Equal(c2, page1[1].CaseId);

        var page2 = await svc.ListByRetentionClassAsync(_tenantId, RetentionClass.Standard, take: 2, skip: 2);
        Assert.Single(page2);
        Assert.Equal(c1, page2[0].CaseId);
    }

    [Fact]
    public async Task GetRetentionPolicy_returns_fallback_when_no_tenant_override()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();

        var standard = await svc.GetRetentionPolicyAsync(RetentionClass.Standard);
        Assert.Equal(1825, standard.RetentionDays);
        Assert.True(standard.IsAutoPurgeEligible);
        Assert.Equal("fallback", standard.Source);

        var extended = await svc.GetRetentionPolicyAsync(RetentionClass.Extended);
        Assert.Equal(2555, extended.RetentionDays);
        Assert.True(extended.IsAutoPurgeEligible);

        var enforcement = await svc.GetRetentionPolicyAsync(RetentionClass.Enforcement);
        Assert.Equal(3650, enforcement.RetentionDays);
        Assert.False(enforcement.IsAutoPurgeEligible);

        var training = await svc.GetRetentionPolicyAsync(RetentionClass.Training);
        Assert.Equal(int.MaxValue, training.RetentionDays);
        Assert.False(training.IsAutoPurgeEligible);

        var hold = await svc.GetRetentionPolicyAsync(RetentionClass.LegalHold);
        Assert.Equal(int.MaxValue, hold.RetentionDays);
        Assert.False(hold.IsAutoPurgeEligible);
    }

    [Fact]
    public async Task GetRetentionPolicy_uses_tenant_override_when_set()
    {
        var settings = (InMemoryTenantSettingsService)_sp.GetRequiredService<ITenantSettingsService>();
        settings.Set(_tenantId, RetentionPolicyDefaults.StandardDaysKey, "365");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RetentionService>();
        var p = await svc.GetRetentionPolicyAsync(RetentionClass.Standard);
        Assert.Equal(365, p.RetentionDays);
        Assert.Equal("tenant-setting", p.Source);
    }

    // ---------------------------------------------------------------
    // Seeding helpers
    // ---------------------------------------------------------------

    private async Task<Guid> SeedCaseAsync(
        RetentionClass cls,
        DateTimeOffset? openedAt = null,
        long? tenantId = null)
    {
        var tid = tenantId ?? _tenantId;
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(tid);
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId,
            LocationId = Guid.NewGuid(),
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = "X-" + caseId.ToString("N")[..6],
            SubjectPayloadJson = "{}",
            State = InspectionWorkflowState.Open,
            OpenedAt = openedAt ?? _clock.UtcNow,
            StateEnteredAt = _clock.UtcNow,
            RetentionClass = cls,
            TenantId = tid
        });
        await db.SaveChangesAsync();
        return caseId;
    }

    private async Task<Guid> SeedScanAsync(Guid caseId, long? tenantId = null)
    {
        var tid = tenantId ?? _tenantId;
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(tid);
        var scanId = Guid.NewGuid();
        db.Set<Scan>().Add(new Scan
        {
            Id = scanId,
            CaseId = caseId,
            CapturedAt = _clock.UtcNow,
            TenantId = tid
        });
        await db.SaveChangesAsync();
        return scanId;
    }

    private async Task SeedArtifactAsync(Guid scanId, string kind, long? tenantId = null)
    {
        var tid = tenantId ?? _tenantId;
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(tid);
        db.Set<ScanArtifact>().Add(new ScanArtifact
        {
            Id = Guid.NewGuid(),
            ScanId = scanId,
            ArtifactKind = kind,
            StorageUri = "test://" + kind,
            MimeType = "image/png",
            ContentHash = "deadbeef",
            CreatedAt = _clock.UtcNow,
            TenantId = tid
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Test-time TimeProvider with settable UtcNow.
/// </summary>
internal sealed class RetentionFakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

/// <summary>
/// Minimal in-memory <see cref="ITenantSettingsService"/> for the
/// retention tests. Keys are scoped per (tenantId, key); GetIntAsync
/// returns the default if no row.
/// </summary>
internal sealed class InMemoryTenantSettingsService : ITenantSettingsService
{
    private readonly Dictionary<(long, string), string> _store = new();

    public void Set(long tenantId, string key, string value) => _store[(tenantId, key)] = value;

    public Task<string> GetAsync(string settingKey, long tenantId, string defaultValue, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue((tenantId, settingKey), out var v) ? v : defaultValue);

    public Task<int> GetIntAsync(string settingKey, long tenantId, int defaultValue, CancellationToken ct = default)
    {
        if (_store.TryGetValue((tenantId, settingKey), out var v) && int.TryParse(v, out var n))
            return Task.FromResult(n);
        return Task.FromResult(defaultValue);
    }

    public Task<TenantSettingDto> SetAsync(string settingKey, long tenantId, string value, Guid? actorUserId, CancellationToken ct = default)
    {
        _store[(tenantId, settingKey)] = value;
        return Task.FromResult(new TenantSettingDto(Guid.NewGuid(), tenantId, settingKey, value, DateTimeOffset.UtcNow, actorUserId));
    }

    public Task<IReadOnlyList<TenantSettingDto>> ListAsync(long tenantId, CancellationToken ct = default)
    {
        IReadOnlyList<TenantSettingDto> rows = _store
            .Where(kv => kv.Key.Item1 == tenantId)
            .Select(kv => new TenantSettingDto(Guid.NewGuid(), tenantId, kv.Key.Item2, kv.Value, DateTimeOffset.UtcNow, null))
            .ToList();
        return Task.FromResult(rows);
    }
}
