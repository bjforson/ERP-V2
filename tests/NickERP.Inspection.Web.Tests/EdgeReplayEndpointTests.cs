using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Web.Endpoints;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 11 / P2 + Sprint 13 / P2-FU-edge-auth — exercises
/// <see cref="EdgeReplayEndpoint.HandleAsync"/> at the handler-method
/// level. Drives an EF in-memory <see cref="AuditDbContext"/> + a
/// hand-built <see cref="DefaultHttpContext"/>.
///
/// <para>
/// Two auth modes covered: per-node API key (Sprint 13, the preferred
/// path going forward) and the legacy <c>X-Edge-Token</c> shared
/// secret (Sprint 11, kept alive during the rollout window via
/// <c>EdgeAuth:AllowLegacyToken</c>). Most "happy path" coverage of
/// the replay logic itself uses the legacy path because it's the
/// simplest auth shape — we don't re-cover replay branches under
/// per-node auth, only the auth decisions themselves.
/// </para>
/// </summary>
public sealed class EdgeReplayEndpointTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Returns_401_when_token_missing()
    {
        await using var db = BuildDb("edge-anon");
        var http = new DefaultHttpContext();
        var auth = BuildAuthHandler(db, BuildConfig(serverSecret: "expected"));

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>());
        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        result.GetType().Name.Should().Be("UnauthorizedHttpResult");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Returns_401_when_token_mismatched()
    {
        await using var db = BuildDb("edge-bad-tok");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Edge-Token"] = "wrong";
        var auth = BuildAuthHandler(db, BuildConfig(serverSecret: "expected"));

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>());
        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        result.GetType().Name.Should().Be("UnauthorizedHttpResult");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Returns_401_when_server_secret_unconfigured()
    {
        await using var db = BuildDb("edge-no-secret");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Edge-Token"] = "anything";
        var auth = BuildAuthHandler(db, BuildConfig(serverSecret: null));

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>());
        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        result.GetType().Name.Should().Be("UnauthorizedHttpResult");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Returns_400_when_envelope_malformed()
    {
        await using var db = BuildDb("edge-bad-env");
        var http = NewAuthedHttp();
        var auth = BuildAuthHandler(db, BuildConfig());

        var body = new EdgeReplayRequestDto("", new List<EdgeReplayEventDto>());
        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        result.GetType().Name.Should().Contain("BadRequest");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Per_entry_error_when_edge_not_authorized_for_tenant()
    {
        await using var db = BuildDb("edge-unauth");
        // Authorize edge-1 for tenant 17, but the request will reference tenant 99.
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1",
            TenantId = 17,
            AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = BuildAuthHandler(db, BuildConfig());

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new("audit.event.replay", 99, DateTimeOffset.UtcNow.AddMinutes(-5),
                JsonSerializer.SerializeToElement(new { eventType = "x.y", entityType = "T", entityId = "1" }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);
        var dto = ExtractValue<EdgeReplayResponseDto>(result);
        dto.Should().NotBeNull();
        dto!.Results.Should().HaveCount(1);
        dto.Results[0].Ok.Should().BeFalse();
        dto.Results[0].Error.Should().Contain("not authorized");

        // No audit.events row written.
        var events = await db.Events.AsNoTracking().ToListAsync();
        events.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Successful_replay_writes_audit_row_with_replay_metadata_and_preserves_OccurredAt()
    {
        await using var db = BuildDb("edge-ok");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-tema-1",
            TenantId = 17,
            AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = BuildAuthHandler(db, BuildConfig());

        var edgeStamp = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
        var body = new EdgeReplayRequestDto("edge-tema-1", new List<EdgeReplayEventDto>
        {
            new("audit.event.replay", 17, edgeStamp,
                JsonSerializer.SerializeToElement(new
                {
                    eventType = "inspection.scan.captured",
                    entityType = "ScanArtifact",
                    entityId = "abc-123",
                    actorUserId = (Guid?)null,
                    correlationId = "trace-1"
                }))
        });

        var clock = new EdgeFixedClock(new DateTimeOffset(2026, 4, 29, 11, 30, 0, TimeSpan.Zero));
        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body, clock);

        var dto = ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeTrue();

        var row = await db.Events.AsNoTracking().SingleAsync();
        row.TenantId.Should().Be(17);
        row.EventType.Should().Be("inspection.scan.captured");
        row.EntityType.Should().Be("ScanArtifact");
        row.EntityId.Should().Be("abc-123");
        // Edge-captured wall-clock survives as OccurredAt.
        row.OccurredAt.Should().Be(edgeStamp);
        // Server's replay-time becomes IngestedAt.
        row.IngestedAt.Should().Be(clock.Now);

        // Replay metadata injected at top of payload.
        using var doc = JsonDocument.Parse(row.Payload.RootElement.GetRawText());
        doc.RootElement.GetProperty("replay_source").GetString().Should().Be("edge");
        doc.RootElement.GetProperty("replay_node_id").GetString().Should().Be("edge-tema-1");
        doc.RootElement.GetProperty("replayed_at").GetString().Should().NotBeNullOrEmpty();

        // Replay-log summary row written.
        var summary = await db.EdgeNodeReplayLogs.AsNoTracking().SingleAsync();
        summary.EdgeNodeId.Should().Be("edge-tema-1");
        summary.EventCount.Should().Be(1);
        summary.OkCount.Should().Be(1);
        summary.FailedCount.Should().Be(0);
        summary.FailuresJson.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Idempotent_replay_does_not_create_duplicate_audit_row()
    {
        await using var db = BuildDb("edge-idem");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = BuildAuthHandler(db, BuildConfig());
        var edgeStamp = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
        var payload = JsonSerializer.SerializeToElement(new
        {
            eventType = "x.y", entityType = "T", entityId = "1"
        });

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new("audit.event.replay", 17, edgeStamp, payload)
        });

        // First pass — writes the audit row.
        var first = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);
        ExtractValue<EdgeReplayResponseDto>(first)!.Results.Single().Ok.Should().BeTrue();

        // Second pass — same edge, same timestamp, same payload. The
        // idempotency key is deterministic; the existing-row branch
        // returns ok=true without inserting again.
        var second = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);
        ExtractValue<EdgeReplayResponseDto>(second)!.Results.Single().Ok.Should().BeTrue();

        var rows = await db.Events.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(1, "the second pass must collapse onto the first via the idempotency key.");

        // Two replay-log summary rows are expected (one per pass) — ops
        // visibility into the retry happening.
        var summaries = await db.EdgeNodeReplayLogs.AsNoTracking().CountAsync();
        summaries.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Future_edge_timestamp_rejected_per_entry()
    {
        await using var db = BuildDb("edge-future");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = BuildAuthHandler(db, BuildConfig());
        var clock = new EdgeFixedClock(new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));
        // 30 minutes in the future — well past the 60s clock-skew tolerance.
        var futureStamp = clock.Now.AddMinutes(30);

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new("audit.event.replay", 17, futureStamp,
                JsonSerializer.SerializeToElement(new { eventType = "x", entityType = "T", entityId = "1" }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body, clock);

        var dto = ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("future");

        (await db.Events.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unsupported_eventTypeHint_rejected_per_entry()
    {
        await using var db = BuildDb("edge-bad-hint");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = BuildAuthHandler(db, BuildConfig());

        // Sprint 17 — the three supported hints are audit.event.replay,
        // inspection.scan.captured, inspection.scanner.status.changed.
        // Use a future-tense voucher.disbursed to exercise the unsupported
        // path (callout in plan-mode walk: voucher payments are post-pilot).
        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new("voucher.disbursed", 17, DateTimeOffset.UtcNow.AddMinutes(-1),
                JsonSerializer.SerializeToElement(new { x = 1 }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);
        var dto = ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("unsupported eventTypeHint");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Malformed_payload_rejected_per_entry()
    {
        await using var db = BuildDb("edge-bad-payload");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = BuildAuthHandler(db, BuildConfig());
        // Missing eventType / entityType / entityId.
        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new("audit.event.replay", 17, DateTimeOffset.UtcNow.AddMinutes(-1),
                JsonSerializer.SerializeToElement(new { actorUserId = Guid.NewGuid() }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);
        var dto = ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("missing required fields");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Mixed_batch_records_per_entry_results_and_summary()
    {
        await using var db = BuildDb("edge-mixed");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = BuildAuthHandler(db, BuildConfig());
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-1);

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new("audit.event.replay", 17, stamp,
                JsonSerializer.SerializeToElement(new { eventType = "x", entityType = "T", entityId = "1" })),
            new("audit.event.replay", 99, stamp,
                JsonSerializer.SerializeToElement(new { eventType = "x", entityType = "T", entityId = "2" })),
            new("audit.event.replay", 17, stamp.AddSeconds(1),
                JsonSerializer.SerializeToElement(new { eventType = "x", entityType = "T", entityId = "3" }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);
        var dto = ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Should().HaveCount(3);
        dto.Results[0].Ok.Should().BeTrue();
        dto.Results[1].Ok.Should().BeFalse();
        dto.Results[1].Error.Should().Contain("not authorized");
        dto.Results[2].Ok.Should().BeTrue();

        var summary = await db.EdgeNodeReplayLogs.AsNoTracking().SingleAsync();
        summary.EventCount.Should().Be(3);
        summary.OkCount.Should().Be(2);
        summary.FailedCount.Should().Be(1);
        summary.FailuresJson.Should().NotBeNull().And.Contain("not authorized");
    }

    // ------------------------------------------------------------------
    // Sprint 13 / P2-FU-edge-auth — legacy fallback toggle coverage.
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Legacy_X_Edge_Token_rejected_when_AllowLegacyToken_is_false()
    {
        await using var db = BuildDb("edge-legacy-off");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Edge-Token"] = "test-secret";
        // Legacy explicitly disabled — even a correctly-presented X-Edge-Token
        // must be rejected so ops can complete the rollout.
        var auth = BuildAuthHandler(db, BuildConfig(serverSecret: "test-secret", allowLegacy: false));

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>());
        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        result.GetType().Name.Should().Be("UnauthorizedHttpResult");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Per_node_API_key_authenticates_when_present_in_X_Edge_Api_Key_header()
    {
        await using var db = BuildDb("edge-per-node-ok");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-tema-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var config = BuildConfig(allowLegacy: false, hashKey: "test-hash-key");
        var auth = BuildAuthHandler(db, config);

        // Issue a key and seed it directly (mirrors the Issue flow).
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());
        var plaintext = EdgeKeyHasher.GenerateApiKey();
        db.EdgeNodeApiKeys.Add(new EdgeNodeApiKey
        {
            TenantId = 17,
            EdgeNodeId = "edge-tema-1",
            KeyHash = hasher.ComputeHash(plaintext),
            KeyPrefix = EdgeKeyHasher.ComputePrefix(plaintext),
            IssuedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.Request.Headers["X-Edge-Api-Key"] = plaintext;

        var body = new EdgeReplayRequestDto("edge-tema-1", new List<EdgeReplayEventDto>
        {
            new("audit.event.replay", 17, DateTimeOffset.UtcNow.AddMinutes(-1),
                JsonSerializer.SerializeToElement(new { eventType = "x", entityType = "T", entityId = "1" }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        var dto = ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeTrue();
    }

    // ------------------------------------------------------------------

    internal static AuditDbContext BuildDb(string name)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(name + "-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestAuditDbContext(options);
    }

    internal static IConfiguration BuildConfig(
        string? serverSecret = "test-secret",
        bool allowLegacy = true,
        string? hashKey = null)
    {
        var dict = new Dictionary<string, string?>();
        if (serverSecret is not null)
            dict[EdgeAuthHandler.LegacySharedSecretConfigKey] = serverSecret;
        dict[EdgeAuthHandler.LegacyTokenConfigKey] = allowLegacy.ToString();
        if (hashKey is not null)
            dict[EdgeKeyHasher.HashKeyConfigKey] = hashKey;
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    internal static EdgeAuthHandler BuildAuthHandler(AuditDbContext db, IConfiguration config)
    {
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());
        return new EdgeAuthHandler(
            db,
            new TenantContext(),
            config,
            hasher,
            NullLogger<EdgeAuthHandler>.Instance);
    }

    private static DefaultHttpContext NewAuthedHttp()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Edge-Token"] = "test-secret";
        return http;
    }

    internal static T? ExtractValue<T>(IResult result) where T : class
    {
        var prop = result.GetType().GetProperty("Value");
        return prop?.GetValue(result) as T;
    }

    /// <summary>
    /// Test-only DbContext subclass — adds the JsonDocument↔string
    /// converter required by the EF in-memory provider on
    /// <see cref="DomainEventRow.Payload"/>. Mirrors the
    /// <see cref="NotificationsEndpointsTests"/> fixture.
    /// </summary>
    internal sealed class TestAuditDbContext : AuditDbContext
    {
        public TestAuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }
        protected override void OnAuditModelCreating(ModelBuilder modelBuilder)
        {
            base.OnAuditModelCreating(modelBuilder);
            var conv = new ValueConverter<JsonDocument, string>(
                v => v.RootElement.GetRawText(),
                v => JsonDocument.Parse(v, default));
            modelBuilder.Entity<DomainEventRow>()
                .Property(e => e.Payload)
                .HasConversion(conv);
            // EdgeNodeReplayLog.FailuresJson is jsonb in Postgres — for
            // the in-memory provider we don't need a converter (string).
        }
    }
}

/// <summary>Local fixed-clock for endpoint tests.</summary>
internal sealed class EdgeFixedClock : TimeProvider
{
    public EdgeFixedClock(DateTimeOffset now) => Now = now;
    public DateTimeOffset Now { get; }
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Test envelope — returns a deterministic 32-byte fallback hash key.
/// Lets the unit tests run without spinning up a real
/// <see cref="IDataProtectionProvider"/>.
/// </summary>
internal sealed class TestEdgeKeyHashEnvelope : IEdgeKeyHashEnvelope
{
    public byte[] DeriveFallbackHashKey()
    {
        // Fixed bytes — match across every test invocation so a key
        // hashed in one fixture verifies in another.
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)i;
        return key;
    }
}
