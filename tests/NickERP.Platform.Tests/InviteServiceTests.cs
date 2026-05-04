using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Platform.Email;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Identity.Database.Services;
using NickERP.Platform.Tenancy;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 21 / Phase B — coverage for <see cref="InviteService"/>.
/// Uses the EF in-memory provider for the DB layer and a stub
/// <see cref="IEmailSender"/> + <see cref="IEmailTemplateProvider"/>
/// + <see cref="IInviteTokenHashEnvelope"/>. RLS / Postgres-specific
/// behaviour (the unique partial index, system-context push) is
/// covered by the live-DB integration suite once the migration is
/// applied; here we exercise the surface logic.
/// </summary>
public sealed class InviteServiceTests
{
    private readonly DateTimeOffset _now = new(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueInvite_PersistsRow_AndSendsEmail()
    {
        await using var ctx = BuildCtx();
        var (svc, sender, _) = BuildService(ctx);

        var result = await svc.IssueInviteAsync(
            tenantId: 5,
            tenantName: "Acme",
            email: "Alice@Example.COM",
            intendedRoles: "Tenant.Admin",
            issuedByUserId: Guid.NewGuid());

        result.Plaintext.Should().NotBeNullOrEmpty();
        result.Plaintext.Length.Should().BeGreaterThan(8);
        result.Prefix.Should().Be(result.Plaintext[..8]);
        result.EmailStatus.Should().Be(EmailSendStatus.Sent);

        var rows = await ctx.InviteTokens.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].TenantId.Should().Be(5);
        rows[0].Email.Should().Be("alice@example.com"); // normalized
        rows[0].TokenHash.Should().NotBeNullOrEmpty();
        rows[0].TokenPrefix.Should().HaveLength(8);
        rows[0].RedeemedAt.Should().BeNull();
        rows[0].RevokedAt.Should().BeNull();

        // Sender saw exactly one outbound message.
        sender.Sent.Should().ContainSingle();
        sender.Sent[0].To.Should().Be("alice@example.com");
        sender.Sent[0].Subject.Should().Contain("Acme");
        sender.Sent[0].BodyText.Should().Contain(result.Plaintext); // link includes token
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueInvite_WithoutPortalBaseUrl_SkipsEmail()
    {
        await using var ctx = BuildCtx();
        var (svc, sender, _) = BuildService(ctx, portalBaseUrl: null);

        var result = await svc.IssueInviteAsync(
            tenantId: 1, tenantName: "Acme", email: "a@b.com");

        result.EmailStatus.Should().Be(EmailSendStatus.Skipped);
        sender.Sent.Should().BeEmpty();
        // Row is still persisted — operator can re-issue.
        (await ctx.InviteTokens.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedeemInvite_HappyPath_ReturnsOk()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);

        var issuance = await svc.IssueInviteAsync(1, "Acme", "a@b.com");
        var redemption = await svc.RedeemInviteAsync(issuance.Plaintext);

        redemption.Outcome.Should().Be(InviteRedemptionOutcome.Ok);
        redemption.MatchedRow.Should().NotBeNull();
        redemption.MatchedRow!.Id.Should().Be(issuance.InviteId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedeemInvite_UnknownToken_ReturnsUnknownToken()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        var redemption = await svc.RedeemInviteAsync("totally-bogus-token-value");
        redemption.Outcome.Should().Be(InviteRedemptionOutcome.UnknownToken);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedeemInvite_EmptyToken_ReturnsUnknownToken()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        (await svc.RedeemInviteAsync(string.Empty)).Outcome.Should().Be(InviteRedemptionOutcome.UnknownToken);
        (await svc.RedeemInviteAsync("   ")).Outcome.Should().Be(InviteRedemptionOutcome.UnknownToken);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedeemInvite_AfterMarkRedeemed_ReturnsAlreadyRedeemed()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        var issuance = await svc.IssueInviteAsync(1, "Acme", "a@b.com");
        await svc.MarkRedeemedAsync(issuance.InviteId, Guid.NewGuid());

        var redemption = await svc.RedeemInviteAsync(issuance.Plaintext);
        redemption.Outcome.Should().Be(InviteRedemptionOutcome.AlreadyRedeemed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedeemInvite_AfterRevoke_ReturnsRevoked()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        var issuance = await svc.IssueInviteAsync(1, "Acme", "a@b.com");
        var revoked = await svc.RevokeInviteAsync(1, issuance.InviteId, Guid.NewGuid());
        revoked.Should().BeTrue();

        var redemption = await svc.RedeemInviteAsync(issuance.Plaintext);
        redemption.Outcome.Should().Be(InviteRedemptionOutcome.Revoked);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedeemInvite_AfterExpiry_ReturnsExpired()
    {
        await using var ctx = BuildCtx();
        var clock = new MutableClock(_now);
        var (svc, _, _) = BuildService(ctx, clock: clock);
        var issuance = await svc.IssueInviteAsync(1, "Acme", "a@b.com",
            expiresIn: TimeSpan.FromHours(1));

        clock.Set(_now.AddHours(2));
        var redemption = await svc.RedeemInviteAsync(issuance.Plaintext);
        redemption.Outcome.Should().Be(InviteRedemptionOutcome.Expired);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RevokeInvite_OnRedeemed_IsNoOp()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        var issuance = await svc.IssueInviteAsync(1, "Acme", "a@b.com");
        await svc.MarkRedeemedAsync(issuance.InviteId, Guid.NewGuid());

        var revoked = await svc.RevokeInviteAsync(1, issuance.InviteId, Guid.NewGuid());
        revoked.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkRedeemed_Twice_SecondReturnsFalse()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        var issuance = await svc.IssueInviteAsync(1, "Acme", "a@b.com");

        var first = await svc.MarkRedeemedAsync(issuance.InviteId, Guid.NewGuid());
        var second = await svc.MarkRedeemedAsync(issuance.InviteId, Guid.NewGuid());

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedeemInvite_FlipsTenantContextToSystem()
    {
        // The invite redemption path must SetSystemContext before
        // the lookup so RLS admits the read on identity.invite_tokens.
        // Verify the side-effect on ITenantContext.
        await using var ctx = BuildCtx();
        var tenantCtx = new TenantContext();
        tenantCtx.SetTenant(99);
        var (svc, _, _) = BuildService(ctx, tenantCtx: tenantCtx);

        await svc.RedeemInviteAsync("nonsense-token");
        tenantCtx.IsSystem.Should().BeTrue();
        tenantCtx.TenantId.Should().Be(TenantContext.SystemSentinel);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueInvite_RejectsZeroOrNegativeTenantId()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        var act1 = async () => await svc.IssueInviteAsync(0, "x", "a@b.com");
        var act2 = async () => await svc.IssueInviteAsync(-1, "x", "a@b.com");
        await act1.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await act2.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueInvite_RejectsEmptyEmail()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        var act = async () => await svc.IssueInviteAsync(1, "x", string.Empty);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueInvite_RejectsEmailOver320Chars()
    {
        await using var ctx = BuildCtx();
        var (svc, _, _) = BuildService(ctx);
        var huge = new string('a', 321) + "@example.com";
        var act = async () => await svc.IssueInviteAsync(1, "x", huge);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*320*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListInvites_ReturnsTenantScoped_NewestFirst()
    {
        await using var ctx = BuildCtx();
        var clock = new MutableClock(_now);
        var (svc, _, _) = BuildService(ctx, clock: clock);

        await svc.IssueInviteAsync(1, "Acme", "a@b.com");
        clock.Set(_now.AddMinutes(1));
        await svc.IssueInviteAsync(1, "Acme", "b@b.com");
        clock.Set(_now.AddMinutes(2));
        await svc.IssueInviteAsync(2, "Other", "c@b.com");

        var list = await svc.ListAsync(1);
        list.Should().HaveCount(2);
        list[0].Email.Should().Be("b@b.com"); // newest first
        list[1].Email.Should().Be("a@b.com");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InviteTokenHasher_GeneratesDistinctTokens()
    {
        var t1 = InviteTokenHasher.GenerateToken();
        var t2 = InviteTokenHasher.GenerateToken();
        t1.Should().NotBe(t2);
        t1.Length.Should().BeGreaterThanOrEqualTo(InviteTokenHasher.TokenPrefixLength);
        t1.Should().NotContain("=");
        t1.Should().NotContain("+");
        t1.Should().NotContain("/");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InviteTokenHasher_HashIsDeterministicForSameKey()
    {
        var hasher = new InviteTokenHasher(BuildConfig(), new ConstantEnvelope());
        var token = "fixed-token";
        var h1 = hasher.ComputeHash(token);
        var h2 = hasher.ComputeHash(token);
        h1.Should().BeEquivalentTo(h2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InviteTokenHasher_DifferentTokensYieldDifferentHashes()
    {
        var hasher = new InviteTokenHasher(BuildConfig(), new ConstantEnvelope());
        var h1 = hasher.ComputeHash("token-a");
        var h2 = hasher.ComputeHash("token-b");
        h1.Should().NotBeEquivalentTo(h2);
    }

    // ---- helpers ------------------------------------------------------

    private IdentityDbContext BuildCtx()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase("invite-svc-tests-" + Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new IdentityDbContext(options);
    }

    private (InviteService svc, RecordingSender sender, MutableClock clock) BuildService(
        IdentityDbContext ctx,
        string? portalBaseUrl = "https://portal.example",
        TenantContext? tenantCtx = null,
        MutableClock? clock = null)
    {
        var config = BuildConfig();
        var hasher = new InviteTokenHasher(config, new ConstantEnvelope());
        var sender = new RecordingSender();
        var templates = new EmbeddedResourceEmailTemplateProvider(
            NullLogger<EmbeddedResourceEmailTemplateProvider>.Instance);
        var emailOpts = Options.Create(new EmailOptions
        {
            DefaultFrom = "no-reply@example.com",
            Invite = new EmailOptions.InviteOptions
            {
                DefaultExpiryHours = 72,
                PortalBaseUrl = portalBaseUrl,
            },
        });
        var svc = new InviteService(
            ctx, hasher, sender, templates,
            tenantCtx ?? new TenantContext(),
            emailOpts,
            NullLogger<InviteService>.Instance,
            clock ?? new MutableClock(_now));
        return (svc, sender, clock ?? new MutableClock(_now));
    }

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InviteTokens:HashKey"] = "test-hash-key-deterministic",
        }).Build();

    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public string ProviderName => "recording";
        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ConstantEnvelope : IInviteTokenHashEnvelope
    {
        public byte[] DeriveFallbackHashKey() =>
            System.Text.Encoding.UTF8.GetBytes("constant-envelope-key-deterministic-32B");
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset now) => _now = now;
    }
}
