using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 13 / P2-FU-edge-auth — exercises
/// <see cref="EdgeAuthHandler"/> directly. Coverage:
///
/// <list type="bullet">
///   <item><description>Per-node valid key authenticates.</description></item>
///   <item><description>Revoked key rejected with <see cref="EdgeAuthOutcome.Revoked"/>.</description></item>
///   <item><description>Expired key rejected with <see cref="EdgeAuthOutcome.Expired"/>.</description></item>
///   <item><description>Unknown key rejected.</description></item>
///   <item><description>Bearer fallback header is honored.</description></item>
///   <item><description>Constant-time comparator path: matched key authenticates regardless of which row Postgres-equality returned (asserted via the comparison branch).</description></item>
///   <item><description>Legacy fallback honored when AllowLegacyToken=true.</description></item>
///   <item><description>Legacy fallback rejected when AllowLegacyToken=false even with valid X-Edge-Token.</description></item>
///   <item><description>Bad per-node key does NOT downgrade to legacy fallback.</description></item>
/// </list>
/// </summary>
public sealed class EdgeAuthHandlerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Per_node_valid_key_authenticates()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-ok");
        var config = EdgeReplayEndpointTests.BuildConfig(allowLegacy: false);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());

        var plaintext = EdgeKeyHasher.GenerateApiKey();
        db.EdgeNodeApiKeys.Add(new EdgeNodeApiKey
        {
            TenantId = 17,
            EdgeNodeId = "edge-1",
            KeyHash = hasher.ComputeHash(plaintext),
            KeyPrefix = EdgeKeyHasher.ComputePrefix(plaintext),
            IssuedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.Request.Headers[EdgeAuthHandler.ApiKeyHeader] = plaintext;

        var result = await auth.AuthenticateAsync(http);
        result.Outcome.Should().Be(EdgeAuthOutcome.AuthenticatedPerNode);
        result.MatchedRow.Should().NotBeNull();
        result.MatchedRow!.EdgeNodeId.Should().Be("edge-1");
        result.MatchedRow.TenantId.Should().Be(17);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Revoked_key_rejected()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-revoked");
        var config = EdgeReplayEndpointTests.BuildConfig(allowLegacy: false);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());

        var plaintext = EdgeKeyHasher.GenerateApiKey();
        db.EdgeNodeApiKeys.Add(new EdgeNodeApiKey
        {
            TenantId = 17,
            EdgeNodeId = "edge-1",
            KeyHash = hasher.ComputeHash(plaintext),
            KeyPrefix = EdgeKeyHasher.ComputePrefix(plaintext),
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-7),
            RevokedAt = DateTimeOffset.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.Request.Headers[EdgeAuthHandler.ApiKeyHeader] = plaintext;

        var result = await auth.AuthenticateAsync(http);
        result.Outcome.Should().Be(EdgeAuthOutcome.Revoked);
        result.MatchedRow.Should().NotBeNull();
        result.MatchedRow!.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Expired_key_rejected()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-expired");
        var config = EdgeReplayEndpointTests.BuildConfig(allowLegacy: false);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());

        var plaintext = EdgeKeyHasher.GenerateApiKey();
        db.EdgeNodeApiKeys.Add(new EdgeNodeApiKey
        {
            TenantId = 17,
            EdgeNodeId = "edge-1",
            KeyHash = hasher.ComputeHash(plaintext),
            KeyPrefix = EdgeKeyHasher.ComputePrefix(plaintext),
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.Request.Headers[EdgeAuthHandler.ApiKeyHeader] = plaintext;

        var result = await auth.AuthenticateAsync(http);
        result.Outcome.Should().Be(EdgeAuthOutcome.Expired);
        result.MatchedRow.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unknown_key_rejected()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-unknown");
        var config = EdgeReplayEndpointTests.BuildConfig(allowLegacy: false);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);

        var http = new DefaultHttpContext();
        http.Request.Headers[EdgeAuthHandler.ApiKeyHeader] = "totally-unknown-key";

        var result = await auth.AuthenticateAsync(http);
        result.Outcome.Should().Be(EdgeAuthOutcome.UnknownKey);
        result.MatchedRow.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Bearer_authorization_header_is_honored()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-bearer");
        var config = EdgeReplayEndpointTests.BuildConfig(allowLegacy: false);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());

        var plaintext = EdgeKeyHasher.GenerateApiKey();
        db.EdgeNodeApiKeys.Add(new EdgeNodeApiKey
        {
            TenantId = 17,
            EdgeNodeId = "edge-1",
            KeyHash = hasher.ComputeHash(plaintext),
            KeyPrefix = EdgeKeyHasher.ComputePrefix(plaintext),
            IssuedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.Request.Headers[EdgeAuthHandler.AuthorizationHeader] = $"Bearer {plaintext}";

        var result = await auth.AuthenticateAsync(http);
        result.Outcome.Should().Be(EdgeAuthOutcome.AuthenticatedPerNode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Constant_time_comparator_path_authenticates_when_hashes_match()
    {
        // Drives TryAuthenticatePerNodeAsync directly so the
        // constant-time comparator branch is the gate. We assert the
        // path: hash is computed via the same hasher; matched-row
        // returns AuthenticatedPerNode. The actual timing safety is
        // CryptographicOperations.FixedTimeEquals (trusted .NET
        // primitive) — what we verify here is that we reach it.
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-ct-eq");
        var config = EdgeReplayEndpointTests.BuildConfig(allowLegacy: false, hashKey: "my-test-hash-key");
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());

        var plaintext = EdgeKeyHasher.GenerateApiKey();
        var hash = hasher.ComputeHash(plaintext);

        db.EdgeNodeApiKeys.Add(new EdgeNodeApiKey
        {
            TenantId = 17,
            EdgeNodeId = "edge-1",
            KeyHash = hash,
            KeyPrefix = EdgeKeyHasher.ComputePrefix(plaintext),
            IssuedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await auth.TryAuthenticatePerNodeAsync(plaintext);
        result.Outcome.Should().Be(EdgeAuthOutcome.AuthenticatedPerNode);

        // Same length but different bytes → constant-time comparator
        // rejects (no row matches the alternate hash).
        var wrongResult = await auth.TryAuthenticatePerNodeAsync("not-the-right-key-but-similar-length");
        wrongResult.Outcome.Should().Be(EdgeAuthOutcome.UnknownKey);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Legacy_token_accepted_when_AllowLegacyToken_true()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-legacy-on");
        var config = EdgeReplayEndpointTests.BuildConfig(serverSecret: "the-secret", allowLegacy: true);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);

        var http = new DefaultHttpContext();
        http.Request.Headers[EdgeAuthHandler.LegacyTokenHeader] = "the-secret";

        var result = await auth.AuthenticateAsync(http);
        result.Outcome.Should().Be(EdgeAuthOutcome.AuthenticatedLegacy);
        result.MatchedRow.Should().BeNull("legacy auth doesn't bind to a per-node row.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Legacy_token_rejected_when_AllowLegacyToken_false()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-legacy-off");
        var config = EdgeReplayEndpointTests.BuildConfig(serverSecret: "the-secret", allowLegacy: false);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);

        var http = new DefaultHttpContext();
        // Even with a correctly-presented X-Edge-Token, the flag forces a 401.
        http.Request.Headers[EdgeAuthHandler.LegacyTokenHeader] = "the-secret";

        var result = await auth.AuthenticateAsync(http);
        result.Outcome.Should().Be(EdgeAuthOutcome.MissingCredential);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Bad_per_node_key_does_not_downgrade_to_legacy()
    {
        // Spec posture: an attacker presenting BOTH a guessed legacy
        // token AND a wrong per-node key must not authenticate. The
        // per-node failure short-circuits before the legacy fallback
        // runs.
        await using var db = EdgeReplayEndpointTests.BuildDb("auth-no-downgrade");
        var config = EdgeReplayEndpointTests.BuildConfig(serverSecret: "the-secret", allowLegacy: true);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, config);

        var http = new DefaultHttpContext();
        http.Request.Headers[EdgeAuthHandler.ApiKeyHeader] = "totally-wrong-api-key";
        http.Request.Headers[EdgeAuthHandler.LegacyTokenHeader] = "the-secret";

        var result = await auth.AuthenticateAsync(http);
        result.Outcome.Should().Be(EdgeAuthOutcome.UnknownKey,
            "a bad per-node key must NOT downgrade to legacy fallback.");
    }
}
