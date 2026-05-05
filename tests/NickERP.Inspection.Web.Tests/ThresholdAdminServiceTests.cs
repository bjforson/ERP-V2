using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
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
/// <see cref="ThresholdAdminService"/> wireup added in Phase B. The
/// tests assert:
///
/// <list type="bullet">
///   <item><description>Approve writes one
///   <see cref="ThresholdProfileHistory"/> row per (model, class)
///   delta against the prior Active row's ValuesJson.</description></item>
///   <item><description>Approve emits one
///   <c>nickerp.inspection.threshold_changed</c> audit event per
///   delta with the documented payload shape.</description></item>
///   <item><description>Bootstrap (no prior Active row) records
///   OldThreshold null but still emits one row + event per
///   class.</description></item>
///   <item><description>The history table is append-only — a second
///   approve creates new rows, never touches existing ones.</description></item>
///   <item><description>Approve still emits the original
///   <c>scanner_threshold_proposal_approved</c> event.</description></item>
/// </list>
/// </summary>
public sealed class ThresholdAdminServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly RecordingEventPublisher _events;
    private readonly Guid _scannerId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private const long TenantId = 1L;

    public ThresholdAdminServiceTests()
    {
        var services = new ServiceCollection();
        var dbName = "threshold-admin-" + Guid.NewGuid();
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
        services.AddSingleton<AuthenticationStateProvider>(
            new FakeAuthStateProvider(_userId));

        services.AddScoped<ThresholdAdminService>();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    /// <summary>
    /// Seed an Active row + a Proposed row. Returns the Proposed
    /// row id so the test can call ApproveAsync.
    /// </summary>
    private async Task<Guid> SeedActiveAndProposedAsync(
        string activeJson,
        string proposedJson,
        string? rationaleJson = null)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.ScannerThresholdProfiles.Add(new ScannerThresholdProfile
        {
            Id = Guid.NewGuid(),
            ScannerDeviceInstanceId = _scannerId,
            Version = 1,
            Status = ScannerThresholdProfileStatus.Active,
            ValuesJson = activeJson,
            ProposedBy = ScannerThresholdProposalSource.Bootstrap,
            ProposalRationaleJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-7),
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-7),
            TenantId = TenantId,
        });
        var proposedId = Guid.NewGuid();
        db.ScannerThresholdProfiles.Add(new ScannerThresholdProfile
        {
            Id = proposedId,
            ScannerDeviceInstanceId = _scannerId,
            Version = 2,
            Status = ScannerThresholdProfileStatus.Proposed,
            ValuesJson = proposedJson,
            ProposedBy = ScannerThresholdProposalSource.Manual,
            ProposalRationaleJson = rationaleJson ?? """{"note":"manual tune"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TenantId = TenantId,
        });
        await db.SaveChangesAsync();
        return proposedId;
    }

    [Fact]
    public async Task ApproveAsync_writes_one_history_row_per_changed_threshold()
    {
        // Active: weapon=0.5, narcotics=0.7. Proposed: weapon=0.6,
        // narcotics=0.7 (only weapon changed). Expect one history row.
        var activeJson = """{"fs6000":{"weapon":0.5,"narcotics":0.7}}""";
        var proposedJson = """{"fs6000":{"weapon":0.6,"narcotics":0.7}}""";
        var proposedId = await SeedActiveAndProposedAsync(activeJson, proposedJson);

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        await svc.ApproveAsync(proposedId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var history = await db.ThresholdProfileHistory.AsNoTracking().ToListAsync();
        history.Should().HaveCount(1,
            because: "only the weapon class changed; narcotics held the same value so no row");
        history[0].ModelId.Should().Be("fs6000");
        history[0].ClassId.Should().Be("weapon");
        history[0].OldThreshold.Should().Be(0.5);
        history[0].NewThreshold.Should().Be(0.6);
        history[0].ChangedByUserId.Should().Be(_userId);
        history[0].TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task ApproveAsync_emits_threshold_changed_event_per_delta()
    {
        var activeJson = """{"fs6000":{"weapon":0.5,"narcotics":0.7}}""";
        var proposedJson = """{"fs6000":{"weapon":0.6,"narcotics":0.8}}""";
        var proposedId = await SeedActiveAndProposedAsync(activeJson, proposedJson);
        _events.Events.Clear();

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        await svc.ApproveAsync(proposedId);

        var changedEvents = _events.Events
            .Where(e => e.EventType == "nickerp.inspection.threshold_changed")
            .ToList();
        changedEvents.Should().HaveCount(2,
            because: "weapon + narcotics both changed; one event per delta");
    }

    [Fact]
    public async Task ApproveAsync_threshold_changed_payload_shape()
    {
        var activeJson = """{"fs6000":{"weapon":0.5}}""";
        var proposedJson = """{"fs6000":{"weapon":0.6}}""";
        var proposedId = await SeedActiveAndProposedAsync(activeJson, proposedJson,
            rationaleJson: """{"note":"raise weapon threshold for live traffic"}""");
        _events.Events.Clear();

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        await svc.ApproveAsync(proposedId);

        var evt = _events.Events
            .Single(e => e.EventType == "nickerp.inspection.threshold_changed");
        var payload = evt.Payload;
        payload.GetProperty("modelId").GetString().Should().Be("fs6000");
        payload.GetProperty("classId").GetString().Should().Be("weapon");
        payload.GetProperty("oldThreshold").GetDouble().Should().Be(0.5);
        payload.GetProperty("newThreshold").GetDouble().Should().Be(0.6);
        payload.GetProperty("scannerDeviceInstanceId").GetGuid().Should().Be(_scannerId);
        payload.GetProperty("tenantId").GetInt64().Should().Be(TenantId);
        payload.GetProperty("reason").GetString().Should().Be("raise weapon threshold for live traffic");
    }

    [Fact]
    public async Task ApproveAsync_bootstrap_records_OldThreshold_null()
    {
        // No prior Active row — every class in the proposal is treated
        // as a new threshold (OldThreshold null per the documented
        // contract).
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var proposedId = Guid.NewGuid();
        db.ScannerThresholdProfiles.Add(new ScannerThresholdProfile
        {
            Id = proposedId,
            ScannerDeviceInstanceId = _scannerId,
            Version = 0,
            Status = ScannerThresholdProfileStatus.Proposed,
            ValuesJson = """{"fs6000":{"weapon":0.5}}""",
            ProposedBy = ScannerThresholdProposalSource.Bootstrap,
            ProposalRationaleJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TenantId = TenantId,
        });
        await db.SaveChangesAsync();

        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        await svc.ApproveAsync(proposedId);

        var history = await db.ThresholdProfileHistory.AsNoTracking().ToListAsync();
        history.Should().ContainSingle();
        history[0].OldThreshold.Should().BeNull(
            because: "no prior Active row → bootstrap path");
        history[0].NewThreshold.Should().Be(0.5);
    }

    [Fact]
    public async Task ApproveAsync_no_diff_writes_no_history_rows()
    {
        // Identical JSON — no delta.
        var json = """{"fs6000":{"weapon":0.5}}""";
        var proposedId = await SeedActiveAndProposedAsync(json, json);
        _events.Events.Clear();

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        await svc.ApproveAsync(proposedId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var history = await db.ThresholdProfileHistory.AsNoTracking().ToListAsync();
        history.Should().BeEmpty(
            because: "the JSON is identical so no per-class delta exists");
        _events.Events.Should().NotContain(e => e.EventType == "nickerp.inspection.threshold_changed");
    }

    [Fact]
    public async Task ApproveAsync_still_emits_proposal_approved_event()
    {
        var activeJson = """{"fs6000":{"weapon":0.5}}""";
        var proposedJson = """{"fs6000":{"weapon":0.6}}""";
        var proposedId = await SeedActiveAndProposedAsync(activeJson, proposedJson);
        _events.Events.Clear();

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        await svc.ApproveAsync(proposedId);

        _events.Events.Should().Contain(
            e => e.EventType == "nickerp.inspection.scanner_threshold_proposal_approved",
            because: "the original event must continue to fire alongside the new threshold_changed events");
    }

    [Fact]
    public async Task ApproveAsync_history_is_append_only_across_two_approves()
    {
        // Approve once — should write 1 history row.
        // Then create a new proposal off the (still Proposed) profile…
        // we can't, the partial-unique-on-Active wouldn't allow a second
        // Active. Instead we simulate by approving with a new proposal
        // each time.
        var firstActive = """{"fs6000":{"weapon":0.4}}""";
        var firstProposed = """{"fs6000":{"weapon":0.5}}""";
        var proposed1 = await SeedActiveAndProposedAsync(firstActive, firstProposed);

        using (var scope = _sp.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
            await svc.ApproveAsync(proposed1);
        }

        // Add another proposal with a NEW delta. Mark the previously
        // Proposed-then-Shadow row Active so the second approve sees a
        // prior Active.
        Guid proposed2;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var existing = await db.ScannerThresholdProfiles
                .FirstAsync(p => p.Id == proposed1);
            // Hand-promote the row to Active so the next approve diffs
            // against it. The seed Active row we wrote earlier stays as
            // a stale Active, but the partial unique index isn't
            // enforced by the EF in-memory provider, so the test isn't
            // crashing on it — what we want is the latest-by-Version
            // pick.
            existing.Status = ScannerThresholdProfileStatus.Active;
            await db.SaveChangesAsync();

            proposed2 = Guid.NewGuid();
            db.ScannerThresholdProfiles.Add(new ScannerThresholdProfile
            {
                Id = proposed2,
                ScannerDeviceInstanceId = _scannerId,
                Version = 3,
                Status = ScannerThresholdProfileStatus.Proposed,
                ValuesJson = """{"fs6000":{"weapon":0.7}}""",
                ProposedBy = ScannerThresholdProposalSource.Manual,
                ProposalRationaleJson = """{"note":"second tune"}""",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                TenantId = TenantId,
            });
            await db.SaveChangesAsync();

            var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
            await svc.ApproveAsync(proposed2);
        }

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var history = await db.ThresholdProfileHistory.AsNoTracking()
                .OrderBy(h => h.ChangedAt)
                .ToListAsync();
            history.Should().HaveCount(2,
                because: "first approve wrote 1 row (0.4→0.5); second approve wrote 1 row (0.5→0.7); both rows still present");
            history[0].OldThreshold.Should().Be(0.4);
            history[0].NewThreshold.Should().Be(0.5);
            history[1].OldThreshold.Should().Be(0.5);
            history[1].NewThreshold.Should().Be(0.7);
        }
    }

    [Fact]
    public async Task ApproveAsync_handles_multi_model_proposal()
    {
        // Two model keys, multiple class keys each — one diff per
        // (model, class) pair that actually changed.
        var activeJson = """{"fs6000":{"weapon":0.5,"narcotics":0.7},"ase":{"weapon":0.4}}""";
        var proposedJson = """{"fs6000":{"weapon":0.6,"narcotics":0.7},"ase":{"weapon":0.5}}""";
        var proposedId = await SeedActiveAndProposedAsync(activeJson, proposedJson);

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        await svc.ApproveAsync(proposedId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var history = await db.ThresholdProfileHistory.AsNoTracking().ToListAsync();
        history.Should().HaveCount(2,
            because: "fs6000.weapon + ase.weapon both changed; fs6000.narcotics held");
        history.Select(h => (h.ModelId, h.ClassId)).Should().BeEquivalentTo(new[]
        {
            ("fs6000", "weapon"),
            ("ase", "weapon"),
        });
    }

    [Fact]
    public async Task ApproveAsync_records_reason_from_rationale_jsonb()
    {
        var activeJson = """{"fs6000":{"weapon":0.5}}""";
        var proposedJson = """{"fs6000":{"weapon":0.6}}""";
        var proposedId = await SeedActiveAndProposedAsync(
            activeJson, proposedJson,
            rationaleJson: """{"note":"raise weapon to reduce false positives"}""");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        await svc.ApproveAsync(proposedId);

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var row = await db.ThresholdProfileHistory.AsNoTracking().FirstAsync();
        row.Reason.Should().Be("raise weapon to reduce false positives");
    }

    [Fact]
    public async Task ApproveAsync_tolerates_malformed_rationale()
    {
        var activeJson = """{"fs6000":{"weapon":0.5}}""";
        var proposedJson = """{"fs6000":{"weapon":0.6}}""";
        var proposedId = await SeedActiveAndProposedAsync(
            activeJson, proposedJson,
            rationaleJson: "this is not valid json");

        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        Func<Task> act = () => svc.ApproveAsync(proposedId);
        await act.Should().NotThrowAsync();

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var row = await db.ThresholdProfileHistory.AsNoTracking().FirstAsync();
        row.Reason.Should().BeNull(
            because: "non-JSON rationale falls back to null without throwing");
    }

    [Fact]
    public async Task ApproveAsync_throws_for_unknown_profile_id()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        Func<Task> act = () => svc.ApproveAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task ApproveAsync_refuses_to_approve_already_active_row()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var rowId = Guid.NewGuid();
        db.ScannerThresholdProfiles.Add(new ScannerThresholdProfile
        {
            Id = rowId,
            ScannerDeviceInstanceId = _scannerId,
            Version = 1,
            Status = ScannerThresholdProfileStatus.Active,
            ValuesJson = "{}",
            ProposalRationaleJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TenantId = TenantId,
        });
        await db.SaveChangesAsync();

        var svc = scope.ServiceProvider.GetRequiredService<ThresholdAdminService>();
        Func<Task> act = () => svc.ApproveAsync(rowId);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Active*");
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        private readonly Guid _userId;
        public FakeAuthStateProvider(Guid userId) => _userId = userId;
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test"),
                new Claim("nickerp:id", _userId.ToString()),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
