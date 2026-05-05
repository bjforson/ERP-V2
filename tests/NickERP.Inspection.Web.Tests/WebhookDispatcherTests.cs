using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Inspection.Webhooks.Abstractions;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 47 / Phase C — coverage for the IOutboundWebhookAdapter
/// contract, the WebhookEventTypes vocabulary, and the
/// WebhookDispatchWorker's mapping + idempotency + per-adapter
/// isolation + per-tenant fan-out + plugin-registry discovery.
///
/// <para>
/// Uses the EF in-memory provider (matching Sprint24WorkersTests +
/// SlaStateRefresherWorkerTests). The WebhookTestAuditDbContext mirrors
/// ReportsServiceTests's pattern for the JsonDocument↔string
/// converter (required because EF in-memory can't natively map jsonb).
/// </para>
/// </summary>
public sealed class WebhookDispatcherTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly RecordingWebhookEventPublisher _events = new();
    private readonly RecordingWebhookAdapter _primaryAdapter;
    private readonly SecondaryRecordingAdapter _secondaryAdapter;
    private readonly ThrowingWebhookAdapter _bombAdapter;

    public WebhookDispatcherTests()
    {
        var dbName = "s47-webhook-" + Guid.NewGuid();
        _primaryAdapter = new RecordingWebhookAdapter("siem-internal");
        _secondaryAdapter = new SecondaryRecordingAdapter();
        _bombAdapter = new ThrowingWebhookAdapter("crash-on-purpose");

        var services = new ServiceCollection();

        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<WebhookTestAuditDbContext>(o =>
            o.UseInMemoryDatabase("audit-" + dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<AuditDbContext>(sp => sp.GetRequiredService<WebhookTestAuditDbContext>());
        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase("tenancy-" + dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IEventPublisher>(_events);

        // Adapter registrations — each adapter registers its concrete
        // type so the dispatcher can resolve it after enumeration via
        // IPluginRegistry.GetContributedTypes.
        services.AddSingleton(_primaryAdapter);
        services.AddSingleton(_secondaryAdapter);
        services.AddSingleton(_bombAdapter);

        // Configurable plugin registry — tests can mutate the
        // contributed-types list to drive different discovery
        // scenarios.
        services.AddSingleton(new ConfigurableWebhookPluginRegistry());
        services.AddSingleton<IPluginRegistry>(sp => sp.GetRequiredService<ConfigurableWebhookPluginRegistry>());

        services.Configure<WebhookDispatchOptions>(o =>
        {
            o.Enabled = true;
            o.PollInterval = TimeSpan.FromSeconds(1);
            o.StartupDelay = TimeSpan.Zero;
            o.BatchLimit = 100;
        });

        services.AddSingleton<WebhookDispatchWorker>(sp => new WebhookDispatchWorker(
            sp,
            sp.GetRequiredService<IOptions<WebhookDispatchOptions>>(),
            NullLogger<WebhookDispatchWorker>.Instance));

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    // -----------------------------------------------------------------
    // IOutboundWebhookAdapter contract
    // -----------------------------------------------------------------

    [Fact]
    public void IOutboundWebhookAdapter_contract_loadable_via_DI()
    {
        // The adapter resolves through DI (singleton in this test
        // fixture; in production it is contributed via a plugin
        // assembly + registered by the plugin's DI extension).
        var resolved = _sp.GetRequiredService<RecordingWebhookAdapter>();
        Assert.NotNull(resolved);
        Assert.IsAssignableFrom<IOutboundWebhookAdapter>(resolved);
        Assert.Equal("siem-internal", resolved.AdapterName);
    }

    [Fact]
    public async Task IOutboundWebhookAdapter_DispatchAsync_invokable_via_mock()
    {
        var adapter = (IOutboundWebhookAdapter)_primaryAdapter;
        var evt = NewWebhookEvent(WebhookEventTypes.CASE_CREATED);
        await adapter.DispatchAsync(evt, CancellationToken.None);

        var seen = Assert.Single(_primaryAdapter.Received);
        Assert.Equal(WebhookEventTypes.CASE_CREATED, seen.EventType);
        Assert.Equal(_tenantId, seen.TenantId);
    }

    [Fact]
    public async Task IOutboundWebhookAdapter_propagates_cancellation()
    {
        var adapter = (IOutboundWebhookAdapter)_primaryAdapter;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // No-op adapter: it doesn't itself throw OperationCanceled,
        // but dispatcher contract permits adapters to throw — test
        // only that passing the token doesn't crash the adapter.
        await adapter.DispatchAsync(NewWebhookEvent(WebhookEventTypes.CASE_CREATED), cts.Token);
        Assert.Single(_primaryAdapter.Received);
    }

    // -----------------------------------------------------------------
    // WebhookEventTypes vocabulary stability
    // -----------------------------------------------------------------

    [Fact]
    public void WebhookEventTypes_constants_match_external_doc_table33()
    {
        // Snapshot the constants. Adding new entries is fine; renaming
        // an existing constant fails this test (signed-contract
        // safety). The expected-set order doesn't matter — the
        // assertion is set-equality.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "HIGH_RISK_SCAN_DETECTED",
            "INSPECTION_REQUIRED",
            "SCAN_REVIEWED",
            "CASE_CREATED",
            "GATEWAY_OFFLINE",
            "SCANNER_OFFLINE",
            "AI_MODEL_DRIFT_ALERT",
            "LEGAL_HOLD_APPLIED",
            "LEGAL_HOLD_RELEASED",
            "THRESHOLD_CHANGED"
        };

        var actual = new HashSet<string>(
            ReadAllStringConstants(typeof(WebhookEventTypes)),
            StringComparer.Ordinal);
        // Every expected constant must be present in the actual set.
        // New additions to the vocabulary are allowed (actual may be
        // a superset of expected).
        Assert.Subset(actual, expected);
    }

    [Fact]
    public void WebhookEventTypes_constants_are_const_strings()
    {
        // Every public field must be `public const string` so
        // accidental rename surfaces at compile time across signed-
        // contract assembly boundaries.
        var fields = typeof(WebhookEventTypes).GetFields(
            BindingFlags.Public | BindingFlags.Static);
        foreach (var f in fields)
        {
            Assert.True(f.IsLiteral && !f.IsInitOnly,
                $"WebhookEventTypes.{f.Name} must be `public const`, not `static readonly`.");
            Assert.Equal(typeof(string), f.FieldType);
        }
    }

    [Fact]
    public void WebhookEventTypes_at_least_ten_standard_events()
    {
        var fields = typeof(WebhookEventTypes).GetFields(
            BindingFlags.Public | BindingFlags.Static);
        Assert.True(fields.Length >= 10,
            $"Sprint 47 brief requires at least 10 standard event types; found {fields.Length}.");
    }

    [Fact]
    public void WebhookEventTypes_each_constant_value_matches_name()
    {
        // Convention: each constant's *value* matches its *name* — so
        // the audit-event match against subscribedTypes uses the same
        // string the consumer sees from WebhookEvent.EventType.
        var fields = typeof(WebhookEventTypes).GetFields(
            BindingFlags.Public | BindingFlags.Static);
        foreach (var f in fields)
        {
            var value = (string?)f.GetRawConstantValue();
            Assert.Equal(f.Name, value);
        }
    }

    [Fact]
    public void WebhookEventTypes_no_duplicate_values()
    {
        var values = ReadAllStringConstants(typeof(WebhookEventTypes));
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    // -----------------------------------------------------------------
    // Adapter discovery via IPluginRegistry.GetContributedTypes
    // -----------------------------------------------------------------

    [Fact]
    public void Adapter_discovery_returns_expected_types()
    {
        // Configure the registry to expose two adapter types.
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter), typeof(ThrowingWebhookAdapter));

        var contributed = ((IPluginRegistry)registry)
            .GetContributedTypes(typeof(IOutboundWebhookAdapter));

        Assert.Contains(typeof(RecordingWebhookAdapter), contributed);
        Assert.Contains(typeof(ThrowingWebhookAdapter), contributed);
    }

    [Fact]
    public async Task NoContributedAdapters_dispatch_is_NoOp()
    {
        await SeedTenantAsync();
        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED);

        // Default registry — no contributed types.
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes();

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        var dispatched = await InvokeDispatchAsync(worker);

        Assert.Equal(0, dispatched);
        Assert.Empty(_primaryAdapter.Received);
    }

    // -----------------------------------------------------------------
    // Mapping audit events → webhook events
    // -----------------------------------------------------------------

    [Fact]
    public async Task DispatchOnce_maps_known_vocabulary_audit_events()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        var dispatched = await InvokeDispatchAsync(worker);

        Assert.Equal(1, dispatched);
        var received = Assert.Single(_primaryAdapter.Received);
        Assert.Equal(WebhookEventTypes.CASE_CREATED, received.EventType);
        Assert.Equal(_tenantId, received.TenantId);
    }

    [Fact]
    public async Task DispatchOnce_skips_events_outside_vocabulary()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        // Audit event of a non-webhook type — internal NickERP event.
        await SeedAuditEventAsync("nickerp.inspection.case_opened");

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        var dispatched = await InvokeDispatchAsync(worker);

        Assert.Equal(0, dispatched);
        Assert.Empty(_primaryAdapter.Received);
    }

    [Fact]
    public async Task CASE_CREATED_audit_round_trips_to_mock_adapter_correctly()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        var entityId = Guid.NewGuid();
        await SeedAuditEventAsync(
            WebhookEventTypes.CASE_CREATED,
            entityId: entityId.ToString(),
            entityType: "InspectionCase",
            payloadJson: "{\"caseId\":\"" + entityId + "\",\"subject\":\"MSCU0000099\"}");

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        var received = Assert.Single(_primaryAdapter.Received);
        Assert.Equal(WebhookEventTypes.CASE_CREATED, received.EventType);
        Assert.Equal("InspectionCase", received.EntityType);
        Assert.Equal(entityId, received.EntityId);
        Assert.Equal("MSCU0000099", received.Payload["subject"]);
        Assert.NotEqual(Guid.Empty, received.IdempotencyKey);
    }

    [Fact]
    public async Task Mapping_lifts_payload_string_number_bool_correctly()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        await SeedAuditEventAsync(
            WebhookEventTypes.HIGH_RISK_SCAN_DETECTED,
            payloadJson: "{\"riskScore\":42,\"escalated\":true,\"label\":\"high\"}");

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        var received = Assert.Single(_primaryAdapter.Received);
        // Number lifts as long when fits Int64 — value-equal to 42
        // regardless of declared type. Cast through Convert so the
        // assertion is type-flexible.
        Assert.Equal(42L, Convert.ToInt64(received.Payload["riskScore"]));
        Assert.Equal(true, received.Payload["escalated"]);
        Assert.Equal("high", received.Payload["label"]);
    }

    [Fact]
    public async Task Mapping_handles_non_object_or_corrupt_payload_gracefully()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        // Top-level JSON array — not an object. Mapper must not throw;
        // payload comes through empty.
        await SeedAuditEventAsync(
            WebhookEventTypes.SCAN_REVIEWED,
            payloadJson: "[1,2,3]");

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        var received = Assert.Single(_primaryAdapter.Received);
        Assert.Equal(WebhookEventTypes.SCAN_REVIEWED, received.EventType);
        Assert.Empty(received.Payload);
    }

    [Fact]
    public void MapToWebhookEvent_uses_audit_event_id_as_idempotency_key()
    {
        var auditId = Guid.NewGuid();
        var row = new DomainEventRow
        {
            EventId = auditId,
            TenantId = _tenantId,
            EventType = WebhookEventTypes.CASE_CREATED,
            EntityType = "InspectionCase",
            EntityId = Guid.NewGuid().ToString(),
            Payload = JsonDocument.Parse("{}"),
            OccurredAt = DateTimeOffset.UtcNow,
            IngestedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = "k"
        };

        var mapped = WebhookDispatchWorker.MapToWebhookEvent(row, _tenantId);

        Assert.Equal(auditId, mapped.IdempotencyKey);
        Assert.Equal(_tenantId, mapped.TenantId);
    }

    [Fact]
    public void MapToWebhookEvent_handles_non_guid_entity_id()
    {
        var row = new DomainEventRow
        {
            EventId = Guid.NewGuid(),
            TenantId = _tenantId,
            EventType = WebhookEventTypes.SCANNER_OFFLINE,
            EntityType = "ScannerDeviceInstance",
            // Some entities use string ids (e.g. adapter names) — Guid
            // parse should fail cleanly with EntityId = null.
            EntityId = "fs6000-tema-1",
            Payload = JsonDocument.Parse("{}"),
            OccurredAt = DateTimeOffset.UtcNow,
            IngestedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = "k"
        };

        var mapped = WebhookDispatchWorker.MapToWebhookEvent(row, _tenantId);

        Assert.Null(mapped.EntityId);
        Assert.Equal("ScannerDeviceInstance", mapped.EntityType);
    }

    // -----------------------------------------------------------------
    // Idempotency via cursor + IdempotencyKey
    // -----------------------------------------------------------------

    [Fact]
    public async Task DispatchOnce_advances_cursor_after_success()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        var firstEventId = await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var cursor = await db.WebhookCursors.FirstAsync(c => c.AdapterName == "siem-internal");
        Assert.Equal(firstEventId, cursor.LastProcessedEventId);
    }

    [Fact]
    public async Task DispatchOnce_idempotent_no_double_send_on_replay()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);
        await InvokeDispatchAsync(worker); // replay — cursor at the event already

        // First tick dispatches; second tick is a no-op because the
        // cursor advanced past the only event.
        Assert.Single(_primaryAdapter.Received);
    }

    [Fact]
    public async Task FirstTick_with_zero_cursor_dispatches_from_start_of_stream()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        // Seed events BEFORE the worker has ever run; cursor will be
        // sentinel Guid.Empty on first tick.
        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED);
        await SeedAuditEventAsync(WebhookEventTypes.SCAN_REVIEWED);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        var dispatched = await InvokeDispatchAsync(worker);

        Assert.Equal(2, dispatched);
        Assert.Equal(2, _primaryAdapter.Received.Count);
    }

    [Fact]
    public async Task IdempotencyKey_is_audit_event_id_for_downstream_dedup()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        var auditId = await SeedAuditEventAsync(WebhookEventTypes.LEGAL_HOLD_APPLIED);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        var received = Assert.Single(_primaryAdapter.Received);
        Assert.Equal(auditId, received.IdempotencyKey);
    }

    [Fact]
    public async Task PerAdapter_cursor_advances_independently()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter), typeof(SecondaryRecordingAdapter));

        // Need a second registered adapter type. Register the fixture
        // _secondaryAdapter under a wrapper type whose AdapterName ==
        // "partner-risk".
        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var cursors = await db.WebhookCursors.ToListAsync();
        Assert.Equal(2, cursors.Count);
        Assert.Contains(cursors, c => c.AdapterName == "siem-internal");
        Assert.Contains(cursors, c => c.AdapterName == "partner-risk");
    }

    // -----------------------------------------------------------------
    // Per-adapter exception isolation
    // -----------------------------------------------------------------

    [Fact]
    public async Task BadAdapter_does_not_stop_others_on_same_tick()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(ThrowingWebhookAdapter), typeof(RecordingWebhookAdapter));

        await SeedAuditEventAsync(WebhookEventTypes.HIGH_RISK_SCAN_DETECTED);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        var dispatched = await InvokeDispatchAsync(worker);

        // ThrowingWebhookAdapter's bomb fires on dispatch; the
        // recording adapter must still see the event on the same tick.
        Assert.Single(_primaryAdapter.Received);
        Assert.Equal(1, dispatched); // only the recording adapter succeeded
    }

    [Fact]
    public async Task BadAdapter_failure_emits_audit_dispatch_failed()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(ThrowingWebhookAdapter));

        await SeedAuditEventAsync(WebhookEventTypes.AI_MODEL_DRIFT_ALERT);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        Assert.Contains(_events.Events, e => e.EventType == "nickerp.webhooks.dispatch_failed");
    }

    [Fact]
    public async Task BadAdapter_does_not_advance_cursor()
    {
        await SeedTenantAsync();
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(ThrowingWebhookAdapter));

        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var cursor = await db.WebhookCursors
            .FirstOrDefaultAsync(c => c.AdapterName == "crash-on-purpose");
        Assert.NotNull(cursor);
        // Cursor remains at sentinel — the failed event wasn't
        // dispatched, so the next tick retries from the same point.
        Assert.Equal(Guid.Empty, cursor!.LastProcessedEventId);
    }

    // -----------------------------------------------------------------
    // Per-tenant fan-out
    // -----------------------------------------------------------------

    [Fact]
    public async Task PerTenant_fanout_each_tenant_dispatched_independently()
    {
        await SeedTenantAsync(1, "t1");
        await SeedTenantAsync(2, "t2");
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED, tenantId: 1);
        await SeedAuditEventAsync(WebhookEventTypes.SCAN_REVIEWED, tenantId: 2);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        var dispatched = await InvokeDispatchAsync(worker);

        Assert.Equal(2, dispatched);
        Assert.Contains(_primaryAdapter.Received, e => e.TenantId == 1);
        Assert.Contains(_primaryAdapter.Received, e => e.TenantId == 2);
    }

    [Fact]
    public async Task Inactive_tenant_is_skipped()
    {
        await SeedTenantAsync(1, "t1", state: TenantState.Active);
        await SeedTenantAsync(2, "t2", state: TenantState.Suspended);
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED, tenantId: 1);
        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED, tenantId: 2);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        Assert.Single(_primaryAdapter.Received); // only tenant 1
        Assert.Equal(1, _primaryAdapter.Received[0].TenantId);
    }

    [Fact]
    public async Task Audit_dispatched_event_emitted_per_tenant_per_adapter()
    {
        await SeedTenantAsync(1, "t1");
        await SeedTenantAsync(2, "t2");
        var registry = _sp.GetRequiredService<ConfigurableWebhookPluginRegistry>();
        registry.SetContributedTypes(typeof(RecordingWebhookAdapter));

        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED, tenantId: 1);
        await SeedAuditEventAsync(WebhookEventTypes.CASE_CREATED, tenantId: 2);

        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        await InvokeDispatchAsync(worker);

        var dispatched = _events.Events
            .Where(e => e.EventType == "nickerp.webhooks.dispatched")
            .ToList();
        Assert.Equal(2, dispatched.Count);
        Assert.Contains(dispatched, e => e.TenantId == 1);
        Assert.Contains(dispatched, e => e.TenantId == 2);
    }

    // -----------------------------------------------------------------
    // Worker lifecycle / config
    // -----------------------------------------------------------------

    [Fact]
    public async Task Disabled_worker_ExecuteAsync_returns_immediately()
    {
        var services = new ServiceCollection();
        services.Configure<WebhookDispatchOptions>(o => { o.Enabled = false; });
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var sp = services.BuildServiceProvider();
        var worker = new WebhookDispatchWorker(
            sp,
            sp.GetRequiredService<IOptions<WebhookDispatchOptions>>(),
            NullLogger<WebhookDispatchWorker>.Instance);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(cts.Token);
    }

    [Fact]
    public void DefaultOptions_is_disabled()
    {
        Assert.False(new WebhookDispatchOptions().Enabled);
    }

    [Fact]
    public void DefaultOptions_PollInterval_is_thirty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new WebhookDispatchOptions().PollInterval);
    }

    [Fact]
    public void DefaultOptions_BatchLimit_is_one_hundred()
    {
        Assert.Equal(100, new WebhookDispatchOptions().BatchLimit);
    }

    [Fact]
    public void WorkerName_and_state_are_exposed()
    {
        var worker = _sp.GetRequiredService<WebhookDispatchWorker>();
        Assert.False(string.IsNullOrWhiteSpace(worker.WorkerName));
        Assert.NotNull(worker.GetState());
    }

    [Fact]
    public void AddNickErpInspectionWebhooks_extension_returns_services()
    {
        // The extension is intentionally a no-op today (no adapters
        // ship with v2). Verify it doesn't crash + returns the same
        // collection so callers can chain.
        var services = new ServiceCollection();
        var returned = services.AddNickErpInspectionWebhooks();
        Assert.Same(services, returned);
    }

    [Fact]
    public void StandardEventTypeSet_matches_constants_count()
    {
        // Defence-in-depth: if a constant is added to the vocabulary
        // but the worker's StandardEventTypeSet isn't updated, the
        // event silently drops out of dispatch. This test catches
        // that drift.
        var publicConstantCount = typeof(WebhookEventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Count(f => f.IsLiteral && f.FieldType == typeof(string));
        Assert.Equal(publicConstantCount, WebhookDispatchWorker.StandardEventTypeSet.Count);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private Task SeedTenantAsync() => SeedTenantAsync(_tenantId, "t1");

    private async Task SeedTenantAsync(long tenantId, string code, TenantState state = TenantState.Active)
    {
        using var scope = _sp.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        if (await tenancy.Tenants.AnyAsync(t => t.Id == tenantId)) return;
        tenancy.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Code = code,
            Name = "Tenant " + code,
            State = state,
            BillingPlan = "internal",
            TimeZone = "UTC",
            Locale = "en",
            Currency = "USD",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await tenancy.SaveChangesAsync();
    }

    private async Task<Guid> SeedAuditEventAsync(
        string eventType,
        long tenantId = 1,
        string? entityId = null,
        string entityType = "InspectionCase",
        string payloadJson = "{\"hello\":\"world\"}")
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        // Bump IngestedAt on each call so the cursor's stable order
        // can be tested deterministically.
        await Task.Delay(2); // ensure distinct ticks for IngestedAt
        db.Events.Add(new DomainEventRow
        {
            EventId = id,
            TenantId = tenantId,
            ActorUserId = null,
            CorrelationId = "corr-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId ?? Guid.NewGuid().ToString(),
            Payload = JsonDocument.Parse(payloadJson),
            OccurredAt = now,
            IngestedAt = now,
            IdempotencyKey = "k-" + id
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<int> InvokeDispatchAsync(WebhookDispatchWorker worker)
    {
        var method = worker.GetType().GetMethod(
            "DispatchOnceAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<int>)method!.Invoke(worker, new object[] { CancellationToken.None })!;
        return await task;
    }

    private WebhookEvent NewWebhookEvent(string eventType) =>
        new(eventType,
            _tenantId,
            EntityId: Guid.NewGuid(),
            EntityType: "InspectionCase",
            Payload: new Dictionary<string, object> { ["hello"] = "world" },
            OccurredAt: DateTimeOffset.UtcNow,
            IdempotencyKey: Guid.NewGuid());

    private static IReadOnlyList<string> ReadAllStringConstants(Type t)
    {
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.Static);
        return fields
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
    }
}

// =====================================================================
// Test doubles
// =====================================================================

/// <summary>
/// Recording adapter — captures every WebhookEvent it receives so
/// tests can assert on shape + ordering. Concrete class so the
/// dispatcher's IPluginRegistry.GetContributedTypes mock can return
/// it as a "plugin-contributed" type.
/// </summary>
internal sealed class RecordingWebhookAdapter : IOutboundWebhookAdapter
{
    public RecordingWebhookAdapter() : this("siem-internal") { }
    public RecordingWebhookAdapter(string name) { AdapterName = name; }
    public string AdapterName { get; }
    public List<WebhookEvent> Received { get; } = new();
    public Task DispatchAsync(WebhookEvent evt, CancellationToken ct)
    {
        Received.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Second concrete recording adapter — exists so per-adapter cursor
/// independence can be tested with two distinct contributed types.
/// </summary>
internal sealed class SecondaryRecordingAdapter : IOutboundWebhookAdapter
{
    public SecondaryRecordingAdapter() { }
    public string AdapterName => "partner-risk";
    public List<WebhookEvent> Received { get; } = new();
    public Task DispatchAsync(WebhookEvent evt, CancellationToken ct)
    {
        Received.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Throwing adapter — exercises per-adapter exception isolation.
/// </summary>
internal sealed class ThrowingWebhookAdapter : IOutboundWebhookAdapter
{
    public ThrowingWebhookAdapter() : this("crash-on-purpose") { }
    public ThrowingWebhookAdapter(string name) { AdapterName = name; }
    public string AdapterName { get; }
    public Task DispatchAsync(WebhookEvent evt, CancellationToken ct)
        => throw new InvalidOperationException("simulated downstream outage");
}

/// <summary>
/// Plugin registry with a mutable contributed-types list. Tests
/// reconfigure the list per-scenario to drive the dispatcher's
/// adapter-discovery path.
/// </summary>
internal sealed class ConfigurableWebhookPluginRegistry : IPluginRegistry
{
    private Type[] _contributed = Array.Empty<Type>();

    public void SetContributedTypes(params Type[] types) => _contributed = types ?? Array.Empty<Type>();

    public IReadOnlyList<RegisteredPlugin> All { get; } = Array.Empty<RegisteredPlugin>();
    public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType) => Array.Empty<RegisteredPlugin>();
    public RegisteredPlugin? FindByTypeCode(string module, string typeCode) => null;
    public T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class
        => throw new NotSupportedException();

    public IReadOnlyList<Type> GetContributedTypes(Type contractType)
    {
        if (!typeof(IOutboundWebhookAdapter).IsAssignableFrom(contractType))
            return Array.Empty<Type>();
        return _contributed;
    }
}

/// <summary>Lightweight RecordingEventPublisher mirroring the pattern in CompletenessCheckerTests.</summary>
internal sealed class RecordingWebhookEventPublisher : IEventPublisher
{
    public List<DomainEvent> Events { get; } = new();
    public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.FromResult(evt);
    }
    public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        Events.AddRange(events);
        return Task.FromResult<IReadOnlyList<DomainEvent>>(events);
    }
}

/// <summary>
/// Subclass of <see cref="AuditDbContext"/> that maps the
/// <c>JsonDocument</c> column to a string converter — required because
/// the EF in-memory provider can't natively map jsonb. Same shape as
/// <c>ReportsServiceTests.WebhookTestAuditDbContext</c>.
/// </summary>
internal sealed class WebhookTestAuditDbContext : AuditDbContext
{
    public WebhookTestAuditDbContext(DbContextOptions<WebhookTestAuditDbContext> options)
        : base(BuildBaseOptions(options))
    {
    }

    private static DbContextOptions<AuditDbContext> BuildBaseOptions(
        DbContextOptions<WebhookTestAuditDbContext> source)
    {
        var b = new DbContextOptionsBuilder<AuditDbContext>();
        foreach (var ext in source.Extensions)
        {
            ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)b)
                .AddOrUpdateExtension(ext);
        }
        return b.Options;
    }

    protected override void OnAuditModelCreating(ModelBuilder modelBuilder)
    {
        base.OnAuditModelCreating(modelBuilder);
        var conv = new ValueConverter<JsonDocument, string>(
            v => v.RootElement.GetRawText(),
            v => JsonDocument.Parse(v, default));
        modelBuilder.Entity<DomainEventRow>()
            .Property(e => e.Payload)
            .HasConversion(conv);
    }
}
