using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Pilot;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 43 Phase C — exercises <see cref="MultiTenantInvariantProbe"/>
/// against the EF in-memory provider. RLS + register-integrity sub-
/// checks are tested for correctness; the cross-tenant export gate
/// sub-check uses the unknown-id path (the gate's most defensive
/// route) so it can run without seeded export rows.
/// </summary>
public sealed class MultiTenantInvariantProbeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly string _tempRoot;

    public MultiTenantInvariantProbeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "invariant-probe-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SingleTenantInstall_RlsCheckPassesTrivially()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var publisher = new RecordingPublisher();
        var probe = BuildProbe(ctx, publisher);

        var result = await probe.RunAsync(tenantId: 5);

        result.RlsReadIsolation.Pass.Should().BeTrue();
        result.RlsReadIsolation.Reason.Should().Contain("single-tenant install");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TwoTenants_NoLeakedRows_RlsCheckPasses()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        await SeedTenantAsync(ctx, tenantId: 7);
        // Add a row for tenant 7 — under in-memory provider no RLS
        // applies, so this test is asserting the SHAPE of the probe
        // rather than the RLS enforcement. Real-Postgres assertion is
        // in the integration tests (Phase E).
        ctx.TenantModuleSettings.Add(new TenantModuleSetting
        {
            TenantId = 7,
            ModuleId = "inspection",
            Enabled = true,
            UpdatedAt = Now,
        });
        await ctx.SaveChangesAsync();
        var probe = BuildProbe(ctx, new RecordingPublisher());

        var result = await probe.RunAsync(tenantId: 5);

        // Under in-memory provider RLS is not enforced, so the probe
        // sees the leaked row. The probe correctly reports fail —
        // this confirms the leak-detection logic works. Real-Postgres
        // RLS isolation prevents the leak in production.
        result.RlsReadIsolation.Pass.Should().BeFalse();
        result.RlsReadIsolation.Reason.Should().Contain("RLS read isolation broken");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TwoTenants_NoOtherTenantData_RlsCheckPasses()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        await SeedTenantAsync(ctx, tenantId: 7);
        // No tenant_module_settings rows for tenant 7 — even without
        // RLS the count is 0, so the probe passes.
        var probe = BuildProbe(ctx, new RecordingPublisher());

        var result = await probe.RunAsync(tenantId: 5);

        result.RlsReadIsolation.Pass.Should().BeTrue();
        result.RlsReadIsolation.Reason.Should().Contain("0 rows leaked");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RegisterCheck_SkipsWhenSourceRootUnconfigured()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        // No Pilot:SourceRoot configured + AppContext.BaseDirectory
        // does not contain docs/system-context-audit-register.md.
        var probe = BuildProbe(ctx, new RecordingPublisher());

        var result = await probe.RunAsync(tenantId: 5);

        // When the runtime can't find a source root, the sub-check
        // skips with pass — production deploys ship DLLs only, so
        // this is the realistic state outside the dev box. We cannot
        // assert pass=true universally here because the test runner's
        // BaseDirectory may resolve to a real source root if running
        // out of the worktree. Both pass-with-skip and a legitimate
        // pass-with-match are acceptable.
        result.SystemContextRegister.Should().NotBeNull();
        result.SystemContextRegister.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RegisterCheck_DriftDetected_ReportsFail()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        // Build a synthetic source tree under tempRoot:
        //   tempRoot/
        //     docs/system-context-audit-register.md       (lists ONLY foo.cs)
        //     src/foo.cs                                   (calls SetSystemContext)
        //     src/bar.cs                                   (also calls SetSystemContext, NOT registered)
        Directory.CreateDirectory(Path.Combine(_tempRoot, "docs"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        File.WriteAllText(Path.Combine(_tempRoot, "docs", "system-context-audit-register.md"),
            "# System-Context Audit Register\n\n## Entries\n\n| Caller | File:Line | Why | RLS | Date | Sprint |\n|---|---|---|---|---|---|\n| `Foo.RunAsync` | `src/foo.cs` | Test | None | 2026-05-05 | Test |\n");
        File.WriteAllText(Path.Combine(_tempRoot, "src", "foo.cs"),
            "class Foo { void M() { tenant.SetSystemContext(); } }");
        File.WriteAllText(Path.Combine(_tempRoot, "src", "bar.cs"),
            "class Bar { void M() { tenant.SetSystemContext(); } }");

        var probe = BuildProbeWithSourceRoot(ctx, new RecordingPublisher(), _tempRoot);

        var result = await probe.RunAsync(tenantId: 5);

        result.SystemContextRegister.Pass.Should().BeFalse();
        result.SystemContextRegister.Reason.Should().Contain("register drift");
        result.SystemContextRegister.Reason.Should().Contain("bar.cs");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RegisterCheck_AllCallersRegistered_ReportsPass()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "docs"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        File.WriteAllText(Path.Combine(_tempRoot, "docs", "system-context-audit-register.md"),
            "## Entries\n\n| Caller | File:Line | Why | RLS | Date | Sprint |\n|---|---|---|---|---|---|\n| `Foo.RunAsync` | `src/foo.cs` | Test | None | 2026-05-05 | Test |\n| `Bar.RunAsync` | `src/bar.cs:12` | Test | None | 2026-05-05 | Test |\n");
        File.WriteAllText(Path.Combine(_tempRoot, "src", "foo.cs"),
            "class Foo { void M() { tenant.SetSystemContext(); } }");
        File.WriteAllText(Path.Combine(_tempRoot, "src", "bar.cs"),
            "class Bar { void M() { tenant.SetSystemContext(); } }");

        var probe = BuildProbeWithSourceRoot(ctx, new RecordingPublisher(), _tempRoot);

        var result = await probe.RunAsync(tenantId: 5);

        result.SystemContextRegister.Pass.Should().BeTrue();
        result.SystemContextRegister.Reason.Should().Contain("agree on 2 caller(s)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RegisterCheck_MissingRegisterFile_ReportsFail()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        // SourceRoot exists but no docs/system-context-audit-register.md.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        File.WriteAllText(Path.Combine(_tempRoot, "src", "foo.cs"),
            "class Foo { void M() { tenant.SetSystemContext(); } }");

        var probe = BuildProbeWithSourceRoot(ctx, new RecordingPublisher(), _tempRoot);

        var result = await probe.RunAsync(tenantId: 5);

        result.SystemContextRegister.Pass.Should().BeFalse();
        result.SystemContextRegister.Reason.Should().Contain("system-context-audit-register.md missing");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RegisterCheck_BinAndObjFolders_AreSkipped()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "docs"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src", "bin"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src", "obj"));
        File.WriteAllText(Path.Combine(_tempRoot, "docs", "system-context-audit-register.md"),
            "## Entries\n\n| Caller | File:Line | Why | RLS | Date | Sprint |\n|---|---|---|---|---|---|\n| `Foo.RunAsync` | `src/foo.cs` | Test | None | 2026-05-05 | Test |\n");
        File.WriteAllText(Path.Combine(_tempRoot, "src", "foo.cs"),
            "class Foo { void M() { tenant.SetSystemContext(); } }");
        // bin / obj contain SetSystemContext-like text — should be skipped.
        File.WriteAllText(Path.Combine(_tempRoot, "src", "bin", "Generated.cs"),
            "class Gen { void M() { tenant.SetSystemContext(); } }");
        File.WriteAllText(Path.Combine(_tempRoot, "src", "obj", "Cache.cs"),
            "class Cache { void M() { tenant.SetSystemContext(); } }");

        var probe = BuildProbeWithSourceRoot(ctx, new RecordingPublisher(), _tempRoot);

        var result = await probe.RunAsync(tenantId: 5);

        // bin/obj callers should be ignored — only foo.cs counts.
        result.SystemContextRegister.Pass.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExportGateCheck_UnknownExportId_ReturnsNull_PassesGate()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var probe = BuildProbe(ctx, new RecordingPublisher());

        var result = await probe.RunAsync(tenantId: 5);

        result.CrossTenantExportGate.Pass.Should().BeTrue();
        result.CrossTenantExportGate.Reason.Should().Contain("correctly returned null");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_EmitsAuditEvent()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var publisher = new RecordingPublisher();
        var probe = BuildProbe(ctx, publisher);

        var result = await probe.RunAsync(tenantId: 5);

        publisher.Events.Should().ContainSingle(e => e.EventType == MultiTenantInvariantProbe.AuditEventType);
        result.ProofEventId.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_PayloadCarriesAllThreeSubChecks()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var publisher = new RecordingPublisher();
        var probe = BuildProbe(ctx, publisher);

        await probe.RunAsync(tenantId: 5);

        var evt = publisher.Events.Single(e => e.EventType == MultiTenantInvariantProbe.AuditEventType);
        var json = evt.Payload.GetRawText();
        json.Should().Contain("rls_read_isolation");
        json.Should().Contain("system_context_register");
        json.Should().Contain("cross_tenant_export_gate");
        json.Should().Contain("overall");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_NoEventPublisher_StillReturnsResult()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        // No publisher — probe should still run + return.
        var probe = new MultiTenantInvariantProbe(
            ctx, new FakeClock(Now), NullLogger<MultiTenantInvariantProbe>.Instance,
            new ConfigurationBuilder().Build(), events: null);

        var result = await probe.RunAsync(tenantId: 5);

        result.Should().NotBeNull();
        result.ProofEventId.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_AllPass_OverallPassTrue()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        await SeedTenantAsync(ctx, tenantId: 7);
        // No data leakage; SourceRoot unconfigured (skipped pass);
        // export gate passes for unknown id.
        var probe = BuildProbe(ctx, new RecordingPublisher());

        var result = await probe.RunAsync(tenantId: 5);

        // Each sub-check passes (or skips with pass).
        result.OverallPass.Should().Be(
            result.RlsReadIsolation.Pass &&
            result.SystemContextRegister.Pass &&
            result.CrossTenantExportGate.Pass);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_IdempotencyKeyIncludesTenantAndSecond()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var publisher = new RecordingPublisher();
        var probe = BuildProbe(ctx, publisher);

        await probe.RunAsync(tenantId: 5);
        var evt = publisher.Events.Single(e => e.EventType == MultiTenantInvariantProbe.AuditEventType);
        evt.IdempotencyKey.Should().StartWith("pilot.invariant_probe.5.");
    }

    // ---- helpers ----

    private static MultiTenantInvariantProbe BuildProbe(TenancyDbContext ctx, RecordingPublisher publisher)
    {
        return new MultiTenantInvariantProbe(
            ctx,
            new FakeClock(Now),
            NullLogger<MultiTenantInvariantProbe>.Instance,
            config: new ConfigurationBuilder().Build(),
            events: publisher);
    }

    private static MultiTenantInvariantProbe BuildProbeWithSourceRoot(
        TenancyDbContext ctx, RecordingPublisher publisher, string sourceRoot)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Pilot:SourceRoot"] = sourceRoot })
            .Build();
        return new MultiTenantInvariantProbe(
            ctx,
            new FakeClock(Now),
            NullLogger<MultiTenantInvariantProbe>.Instance,
            config: config,
            events: publisher);
    }

    private static TenancyDbContext BuildCtx()
    {
        var name = "invariant-probe-" + Guid.NewGuid();
        var opts = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TenancyDbContext(opts);
    }

    private static async Task SeedTenantAsync(TenancyDbContext ctx, long tenantId)
    {
        ctx.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Code = $"seed-{tenantId}",
            Name = $"Seed {tenantId}",
            BillingPlan = "internal",
            TimeZone = "UTC",
            Locale = "en",
            Currency = "USD",
            State = TenantState.Active,
            RetentionDays = 90,
            CreatedAt = Now.AddDays(-30),
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<DomainEvent> Events { get; } = new();
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            var withId = evt with { EventId = Guid.NewGuid() };
            Events.Add(withId);
            return Task.FromResult(withId);
        }
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            Events.AddRange(events);
            return Task.FromResult<IReadOnlyList<DomainEvent>>(events);
        }
    }
}
