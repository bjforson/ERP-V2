using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 13 / P2-FU-edge-auth — exercises
/// <see cref="EdgeNodeApiKeyService"/>. Coverage:
///
/// <list type="bullet">
///   <item><description>IssueAsync returns plaintext exactly once + persisted hash matches the plaintext under the same hasher.</description></item>
///   <item><description>Listed rows surface the prefix but never the hash or plaintext.</description></item>
///   <item><description>RevokeAsync sets RevokedAt; second call is a no-op.</description></item>
///   <item><description>Validation: blank edge id, negative tenant, past expiry rejected.</description></item>
/// </list>
/// </summary>
public sealed class EdgeNodeApiKeyServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueAsync_returns_plaintext_exactly_once_and_hash_matches()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("svc-issue");
        var (svc, hasher) = BuildService(db);

        var issuance = await svc.IssueAsync(
            tenantId: 17,
            edgeNodeId: "edge-tema-1",
            description: "first key",
            expiresAt: null,
            createdByUserId: Guid.NewGuid());

        // Plaintext is non-empty + has the documented length-ish (32-byte
        // base64 trims to ~43 chars, but URL-safe base64 without padding
        // is roughly that range — we verify via prefix length).
        issuance.Plaintext.Should().NotBeNullOrEmpty();
        issuance.Prefix.Should().HaveLength(EdgeKeyHasher.KeyPrefixLength);
        issuance.Plaintext.Should().StartWith(issuance.Prefix);

        // The persisted hash matches a fresh hash of the plaintext under
        // the same hasher — so the auth handler will authenticate it.
        var row = await db.EdgeNodeApiKeys.AsNoTracking().SingleAsync();
        var freshHash = hasher.ComputeHash(issuance.Plaintext);
        row.KeyHash.Should().Equal(freshHash);
        row.KeyPrefix.Should().Be(issuance.Prefix);
        row.TenantId.Should().Be(17);
        row.EdgeNodeId.Should().Be("edge-tema-1");
        row.Description.Should().Be("first key");
        row.RevokedAt.Should().BeNull();
        row.ExpiresAt.Should().BeNull();
        row.CreatedByUserId.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_surfaces_prefix_but_not_hash_or_plaintext()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("svc-list");
        var (svc, _) = BuildService(db);

        var i1 = await svc.IssueAsync(17, "edge-1", description: "a");
        var i2 = await svc.IssueAsync(17, "edge-1", description: "b");
        var i3 = await svc.IssueAsync(17, "edge-2", description: "c");

        var all = await svc.ListAsync(17);
        all.Should().HaveCount(3);
        all.Select(k => k.KeyPrefix).Should().BeEquivalentTo(new[] { i1.Prefix, i2.Prefix, i3.Prefix });
        // Summary record has no plaintext / hash field — by construction.
        // Just assert order is newest-first.
        all[0].IssuedAt.Should().BeOnOrAfter(all[^1].IssuedAt);

        // Filter by edge node.
        var edge1 = await svc.ListAsync(17, "edge-1");
        edge1.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RevokeAsync_sets_RevokedAt_and_second_call_is_no_op()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("svc-revoke");
        var (svc, _) = BuildService(db);

        var issuance = await svc.IssueAsync(17, "edge-1");
        (await db.EdgeNodeApiKeys.AsNoTracking().SingleAsync()).RevokedAt.Should().BeNull();

        var first = await svc.RevokeAsync(17, issuance.KeyId);
        first.Should().BeTrue();
        (await db.EdgeNodeApiKeys.AsNoTracking().SingleAsync()).RevokedAt.Should().NotBeNull();

        var second = await svc.RevokeAsync(17, issuance.KeyId);
        second.Should().BeFalse("second revoke is a no-op (idempotent).");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RevokeAsync_returns_false_for_unknown_keyId()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("svc-revoke-unknown");
        var (svc, _) = BuildService(db);

        var result = await svc.RevokeAsync(17, Guid.NewGuid());
        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueAsync_validates_inputs()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("svc-validate");
        var (svc, _) = BuildService(db);

        // Negative / zero tenant.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.IssueAsync(0, "edge-1"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.IssueAsync(-1, "edge-1"));

        // Blank edge node id.
        await Assert.ThrowsAsync<ArgumentException>(() => svc.IssueAsync(17, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.IssueAsync(17, "   "));

        // Edge node id too long.
        await Assert.ThrowsAsync<ArgumentException>(() => svc.IssueAsync(17, new string('x', 101)));

        // Past expiry.
        await Assert.ThrowsAsync<ArgumentException>(() => svc.IssueAsync(17, "edge-1",
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAuthorizedEdgeNodesAsync_returns_distinct_edge_ids_for_tenant()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("svc-list-auth");
        db.EdgeNodeAuthorizations.AddRange(
            new EdgeNodeAuthorization { EdgeNodeId = "edge-a", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow },
            new EdgeNodeAuthorization { EdgeNodeId = "edge-b", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow },
            new EdgeNodeAuthorization { EdgeNodeId = "edge-c", TenantId = 99, AuthorizedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var (svc, _) = BuildService(db);

        var t17 = await svc.ListAuthorizedEdgeNodesAsync(17);
        t17.Should().BeEquivalentTo(new[] { "edge-a", "edge-b" });

        var t99 = await svc.ListAuthorizedEdgeNodesAsync(99);
        t99.Should().BeEquivalentTo(new[] { "edge-c" });
    }

    private static (EdgeNodeApiKeyService svc, EdgeKeyHasher hasher) BuildService(AuditDbContext db)
    {
        var config = EdgeReplayEndpointTests.BuildConfig(hashKey: "svc-test-hash-key");
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());
        var svc = new EdgeNodeApiKeyService(
            db,
            hasher,
            NullLogger<EdgeNodeApiKeyService>.Instance);
        return (svc, hasher);
    }
}
